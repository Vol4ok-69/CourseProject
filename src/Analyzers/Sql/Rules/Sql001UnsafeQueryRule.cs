using Application.Analyzers;
using Domain.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SqlAnalyzer.Rules;

public sealed class Sql001UnsafeQueryRule : ISqlRule
{
    private static readonly HashSet<string> TargetMethodNames = new(StringComparer.Ordinal)
    {
        "FromSqlRaw",
        "ExecuteSqlRaw",
        "ExecuteSqlRawAsync",
        "SqlQueryRaw"
    };

    public IReadOnlyList<AnalyzerFinding> Analyze(SyntaxNode root, string filePath)
    {
        var findings = new List<AnalyzerFinding>();

        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            var methodName = GetTargetMethodName(invocation);
            if (methodName is null)
            {
                continue;
            }

            var firstArgument = invocation.ArgumentList.Arguments.FirstOrDefault();
            if (firstArgument is null)
            {
                continue;
            }

            if (!IsUnsafeDynamicExpression(firstArgument.Expression, invocation))
            {
                continue;
            }

            var lineSpan = invocation.GetLocation().GetLineSpan();
            var lineNumber = lineSpan.StartLinePosition.Line + 1;

            findings.Add(new AnalyzerFinding
            {
                FilePath = filePath,
                LineNumber = lineNumber,
                Rule = "SQL001",
                Message = $"Потенциально небезопасное формирование SQL: метод '{methodName}' вызван со строкой, сформированной через интерполяцию или конкатенацию.",
                Severity = Severity.Warning,
                Recommendation = "Используйте безопасный параметризованный метод 'FromSqlInterpolated' / 'ExecuteSqlInterpolatedAsync' либо передавайте параметры через явные объекты параметров запроса (DbParameter) во избежание SQL-инъекций."
            });
        }

        return findings;
    }

    private static string? GetTargetMethodName(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var name = memberAccess.Name.Identifier.Text;
            if (TargetMethodNames.Contains(name))
            {
                return name;
            }
        }

        return null;
    }

    private static bool IsUnsafeDynamicExpression(ExpressionSyntax expression, SyntaxNode contextNode)
    {
        switch (expression)
        {
            case InterpolatedStringExpressionSyntax interpolated:
                return interpolated.Contents.OfType<InterpolationSyntax>().Any();

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                return !IsCompileTimeConstant(binary, contextNode);

            case InvocationExpressionSyntax methodCall:
                return IsUnsafeStringFormattingCall(methodCall, contextNode);

            case IdentifierNameSyntax identifier:
                return IsIdentifierInitializedDynamically(identifier, contextNode);

            case ParenthesizedExpressionSyntax paren:
                return IsUnsafeDynamicExpression(paren.Expression, contextNode);

            default:
                return false;
        }
    }

    private static bool IsUnsafeStringFormattingCall(InvocationExpressionSyntax methodCall, SyntaxNode contextNode)
    {
        if (methodCall.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var caller = memberAccess.Expression.ToString();
            var methodName = memberAccess.Name.Identifier.Text;

            if (caller is "string" or "String")
            {
                var args = methodCall.ArgumentList.Arguments;

                if (methodName == "Concat")
                {
                    return args.Any(arg => !IsCompileTimeConstant(arg.Expression, contextNode));
                }

                if (methodName == "Format")
                {
                    if (args.Count == 0)
                    {
                        return false;
                    }

                    return args.Any(arg => !IsCompileTimeConstant(arg.Expression, contextNode));
                }

                if (methodName == "Join")
                {
                    return args.Any(arg => !IsCompileTimeConstant(arg.Expression, contextNode));
                }
            }
        }

        return false;
    }

    private static bool IsIdentifierInitializedDynamically(IdentifierNameSyntax identifier, SyntaxNode contextNode)
    {
        var variableName = identifier.Identifier.Text;
        var enclosingScope = FindEnclosingScope(contextNode);

        if (enclosingScope is null)
        {
            return false;
        }

        var declarator = FindLocalDeclaratorInScope(identifier, enclosingScope, variableName);
        if (declarator != null)
        {
            if (declarator.Parent is VariableDeclarationSyntax varDeclaration &&
                varDeclaration.Parent is LocalDeclarationStatementSyntax localDeclaration &&
                localDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword))
            {
                return declarator.Initializer?.Value is ExpressionSyntax constInit &&
                       !IsCompileTimeConstant(constInit, declarator);
            }

            if (declarator.Initializer?.Value is ExpressionSyntax initExpression &&
                IsUnsafeDynamicExpression(initExpression, declarator))
            {
                return true;
            }

            if (HasDynamicReassignment(enclosingScope, variableName, declarator.SpanStart, identifier.SpanStart))
            {
                return true;
            }

            return false;
        }

        var classDecl = contextNode.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (classDecl != null)
        {
            var fieldDeclarator = classDecl.Members
                .OfType<FieldDeclarationSyntax>()
                .SelectMany(f => f.Declaration.Variables)
                .FirstOrDefault(v => v.Identifier.Text == variableName);

            if (fieldDeclarator?.Initializer?.Value is ExpressionSyntax fieldInit)
            {
                return IsUnsafeDynamicExpression(fieldInit, fieldDeclarator);
            }
        }

        return false;
    }

    private static bool HasDynamicReassignment(
        SyntaxNode enclosingScope,
        string variableName,
        int startSpan,
        int endSpan)
    {
        var assignments = enclosingScope.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.SpanStart > startSpan && a.SpanStart < endSpan);

        foreach (var assignment in assignments)
        {
            if (assignment.Left is IdentifierNameSyntax leftId && leftId.Identifier.Text == variableName)
            {
                if (IsUnsafeDynamicExpression(assignment.Right, assignment))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static VariableDeclaratorSyntax? FindLocalDeclaratorInScope(
        SyntaxNode targetNode,
        SyntaxNode enclosingScope,
        string variableName)
    {
        for (var current = targetNode.Parent; current != null && current != enclosingScope.Parent; current = current.Parent)
        {
            if (current is BlockSyntax block)
            {
                var declarator = block.Statements
                    .Where(s => s.SpanStart < targetNode.SpanStart)
                    .OfType<LocalDeclarationStatementSyntax>()
                    .SelectMany(s => s.Declaration.Variables)
                    .LastOrDefault(v => v.Identifier.Text == variableName);

                if (declarator != null)
                {
                    return declarator;
                }
            }
        }

        return enclosingScope.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(d => d.Identifier.Text == variableName && d.SpanStart < targetNode.SpanStart)
            .LastOrDefault();
    }

    private static bool IsCompileTimeConstant(ExpressionSyntax expression, SyntaxNode contextNode)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return true;

            case InterpolatedStringExpressionSyntax interpolated:
                return !interpolated.Contents.OfType<InterpolationSyntax>().Any();

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                return IsCompileTimeConstant(binary.Left, contextNode) &&
                       IsCompileTimeConstant(binary.Right, contextNode);

            case ParenthesizedExpressionSyntax paren:
                return IsCompileTimeConstant(paren.Expression, contextNode);

            case IdentifierNameSyntax identifier:
                return IsConstantIdentifier(identifier, contextNode);

            default:
                return false;
        }
    }

    private static bool IsConstantIdentifier(IdentifierNameSyntax identifier, SyntaxNode contextNode)
    {
        var variableName = identifier.Identifier.Text;
        var enclosingScope = FindEnclosingScope(contextNode);

        if (enclosingScope != null)
        {
            var declarator = FindLocalDeclaratorInScope(identifier, enclosingScope, variableName);
            if (declarator != null)
            {
                if (declarator.Parent is VariableDeclarationSyntax varDeclaration &&
                    varDeclaration.Parent is LocalDeclarationStatementSyntax localDeclaration &&
                    localDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword))
                {
                    return true;
                }

                if (declarator.Initializer?.Value is ExpressionSyntax initExpression)
                {
                    if (IsCompileTimeConstant(initExpression, declarator) &&
                        !HasReassignment(enclosingScope, variableName, declarator.SpanStart, identifier.SpanStart))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        var classDecl = contextNode.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (classDecl != null)
        {
            var field = classDecl.Members
                .OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == variableName));

            if (field != null)
            {
                if (field.Modifiers.Any(SyntaxKind.ConstKeyword))
                {
                    return true;
                }

                var fieldVar = field.Declaration.Variables.First(v => v.Identifier.Text == variableName);
                if (field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) && fieldVar.Initializer?.Value is ExpressionSyntax init)
                {
                    return IsCompileTimeConstant(init, fieldVar);
                }
            }
        }

        return false;
    }

    private static bool HasReassignment(
        SyntaxNode enclosingScope,
        string variableName,
        int startSpan,
        int endSpan)
    {
        return enclosingScope.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Any(a => a.SpanStart > startSpan &&
                      a.SpanStart < endSpan &&
                      a.Left is IdentifierNameSyntax leftId &&
                      leftId.Identifier.Text == variableName);
    }

    private static SyntaxNode? FindEnclosingScope(SyntaxNode node)
    {
        return node.Ancestors().FirstOrDefault(ancestor =>
            ancestor is MethodDeclarationSyntax or
                        LocalFunctionStatementSyntax or
                        AccessorDeclarationSyntax or
                        ParenthesizedLambdaExpressionSyntax or
                        AnonymousMethodExpressionSyntax or
                        SimpleLambdaExpressionSyntax or
                        ConstructorDeclarationSyntax);
    }
}
