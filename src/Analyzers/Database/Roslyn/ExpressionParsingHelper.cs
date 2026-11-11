using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DatabaseAnalyzer.Roslyn;

internal static class ExpressionParsingHelper
{
    public static bool TryGetPropertyName(ExpressionSyntax expression, out string? propertyName)
    {
        propertyName = null;

        if (expression is null)
        {
            return false;
        }

        switch (expression)
        {
            case IdentifierNameSyntax id:
                propertyName = id.Identifier.ValueText;
                return true;

            case MemberAccessExpressionSyntax memberAccess:
                propertyName = memberAccess.Name.Identifier.ValueText;
                return true;

            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                propertyName = literal.Token.ValueText;
                return true;

            case InvocationExpressionSyntax invocation when IsNameOfInvocation(invocation):
                return TryExtractNameOfTarget(invocation, out propertyName);

            default:
                return false;
        }
    }

    public static IReadOnlyList<string> ExtractPropertyNames(ExpressionSyntax expression)
    {
        var result = new List<string>();

        if (expression is null)
        {
            return result;
        }

        if (expression is LambdaExpressionSyntax lambda)
        {
            var body = lambda.Body;

            switch (body)
            {
                case MemberAccessExpressionSyntax memberAccess:
                    result.Add(memberAccess.Name.Identifier.ValueText);
                    break;

                case IdentifierNameSyntax id:
                    result.Add(id.Identifier.ValueText);
                    break;

                case AnonymousObjectCreationExpressionSyntax anonymous:
                    foreach (var initializer in anonymous.Initializers)
                    {
                        if (initializer.NameEquals is not null)
                        {
                            result.Add(initializer.NameEquals.Name.Identifier.ValueText);
                        }
                        else if (initializer.Expression is MemberAccessExpressionSyntax m)
                        {
                            result.Add(m.Name.Identifier.ValueText);
                        }
                        else if (initializer.Expression is IdentifierNameSyntax i)
                        {
                            result.Add(i.Identifier.ValueText);
                        }
                    }
                    break;

                case TupleExpressionSyntax tuple:
                    foreach (var argument in tuple.Arguments)
                    {
                        if (TryGetPropertyName(argument.Expression, out var propName) && propName is not null)
                        {
                            result.Add(propName);
                        }
                    }
                    break;
            }

            return result;
        }

        if (expression is ArrayCreationExpressionSyntax arrayCreation && arrayCreation.Initializer is not null)
        {
            foreach (var item in arrayCreation.Initializer.Expressions)
            {
                if (TryGetStringLiteralOrNameof(item, out var prop) && prop is not null)
                {
                    result.Add(prop);
                }
            }

            return result;
        }

        if (expression is ImplicitArrayCreationExpressionSyntax implicitArray)
        {
            foreach (var item in implicitArray.Initializer.Expressions)
            {
                if (TryGetStringLiteralOrNameof(item, out var prop) && prop is not null)
                {
                    result.Add(prop);
                }
            }

            return result;
        }

        if (TryGetStringLiteralOrNameof(expression, out var singleProp) && singleProp is not null)
        {
            result.Add(singleProp);
        }

        return result;
    }

    public static bool TryGetStringLiteralOrNameof(ExpressionSyntax expression, out string? value)
    {
        value = null;

        if (expression is null)
        {
            return false;
        }

        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            value = literal.Token.ValueText;
            return true;
        }

        if (expression is InvocationExpressionSyntax invocation && IsNameOfInvocation(invocation))
        {
            return TryExtractNameOfTarget(invocation, out value);
        }

        return false;
    }

    public static bool TryGetBooleanLiteral(ExpressionSyntax expression, out bool value)
    {
        value = false;

        if (expression is LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
            {
                value = true;
                return true;
            }

            if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
            {
                value = false;
                return true;
            }
        }

        return false;
    }

    private static bool IsNameOfInvocation(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is IdentifierNameSyntax id &&
               string.Equals(id.Identifier.ValueText, "nameof", StringComparison.Ordinal) &&
               invocation.ArgumentList.Arguments.Count == 1;
    }

    private static bool TryExtractNameOfTarget(InvocationExpressionSyntax invocation, out string? name)
    {
        name = null;
        var argument = invocation.ArgumentList.Arguments.FirstOrDefault();

        if (argument is null)
        {
            return false;
        }

        var expr = argument.Expression;

        switch (expr)
        {
            case IdentifierNameSyntax id:
                name = id.Identifier.ValueText;
                return true;

            case MemberAccessExpressionSyntax memberAccess:
                name = memberAccess.Name.Identifier.ValueText;
                return true;

            default:
                return false;
        }
    }
}
