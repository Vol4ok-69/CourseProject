using Application.Analyzers;
using Domain.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace SqlAnalyzer.Rules;

public sealed class Sql002SelectStarRule : ISqlRule
{
    private static readonly HashSet<string> TargetMethodNames = new(StringComparer.Ordinal)
    {
        "FromSqlRaw",
        "FromSqlInterpolated",
        "SqlQueryRaw",
        "SqlQuery"
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

            var sqlText = ExtractSqlString(firstArgument.Expression, invocation);
            if (string.IsNullOrWhiteSpace(sqlText))
            {
                continue;
            }

            if (!ContainsSelectStar(sqlText))
            {
                continue;
            }

            var lineSpan = invocation.GetLocation().GetLineSpan();
            var lineNumber = lineSpan.StartLinePosition.Line + 1;

            findings.Add(new AnalyzerFinding
            {
                FilePath = filePath,
                LineNumber = lineNumber,
                Rule = "SQL002",
                Message = "Запрос содержит конструкцию 'SELECT *'. Рекомендуется явно указывать список извлекаемых столбцов.",
                Severity = Severity.Info,
                Recommendation = "Замените символ '*' на явный перечень необходимых столбцов. Это улучшит читаемость кода, позволит СУБД использовать покрывающие индексы и предотвратит избыточную передачу неиспользуемых данных."
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

    public static bool ContainsSelectStar(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        // 1. Удаляем многострочные и однострочные комментарии SQL
        var cleanSql = Regex.Replace(sql, @"/\*[\s\S]*?\*/", " ");
        cleanSql = Regex.Replace(cleanSql, @"--[^\r\n]*", " ");

        // 2. Маскируем строковые литералы в одинарных кавычках ('...'), чтобы слова SELECT, FROM или * внутри строк не вызывали ложных срабатываний
        cleanSql = Regex.Replace(cleanSql, @"'([^']|'')*'", "'_STR_'");

        // 3. Маскируем агрегатные функции с аргументом * (COUNT(*), COUNT(DISTINCT *) и аналогичные)
        cleanSql = Regex.Replace(cleanSql, @"(?i)\bCOUNT\s*\(\s*(?:DISTINCT\s+)?\*\s*\)", "COUNT(_AGGREGATE_STAR_)");

        // 4. Находим все конструкции SELECT (включая основные запросы и подзапросы)
        var selectMatches = Regex.Matches(cleanSql, @"(?i)\bSELECT\b");

        foreach (Match selectMatch in selectMatches)
        {
            var startIndex = selectMatch.Index + selectMatch.Length;
            var columnsClause = ExtractColumnsClause(cleanSql, startIndex);
            if (string.IsNullOrWhiteSpace(columnsClause))
            {
                continue;
            }

            var columnItems = SplitColumnsClause(columnsClause);

            foreach (var item in columnItems)
            {
                var trimmed = item.Trim();

                // Одиночный wildcard: *
                if (trimmed == "*")
                {
                    return true;
                }

                // Квалифицированный wildcard таблицы: alias.*, [alias].*, "alias".*
                if (Regex.IsMatch(trimmed, @"^(?:\[?\w+\]?|\""?\w+\""?)\.\*$"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ExtractColumnsClause(string sql, int startIndex)
    {
        var remaining = sql.AsSpan(startIndex);
        var trimmedLeading = remaining.TrimStart();
        var offset = startIndex + (remaining.Length - trimmedLeading.Length);

        // Пропускаем необязательное ключевое слово DISTINCT
        if (offset < sql.Length && sql.AsSpan(offset).StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase))
        {
            var afterDistinct = offset + "DISTINCT".Length;
            if (afterDistinct < sql.Length && char.IsWhiteSpace(sql[afterDistinct]))
            {
                offset = afterDistinct;
            }
        }

        var parenthesisDepth = 0;
        var current = offset;

        while (current < sql.Length)
        {
            var ch = sql[current];

            if (ch == '(')
            {
                parenthesisDepth++;
                current++;
                continue;
            }

            if (ch == ')')
            {
                if (parenthesisDepth == 0)
                {
                    // Закрывающая скобка внешнего подзапроса
                    break;
                }
                parenthesisDepth--;
                current++;
                continue;
            }

            if (parenthesisDepth == 0)
            {
                // Ключевые слова, завершающие список столбцов SELECT на верхнем уровне данного подзапроса
                if (IsWordAt(sql, current, "FROM") ||
                    IsWordAt(sql, current, "WHERE") ||
                    IsWordAt(sql, current, "UNION") ||
                    IsWordAt(sql, current, "INTO") ||
                    IsWordAt(sql, current, "EXCEPT") ||
                    IsWordAt(sql, current, "INTERSECT"))
                {
                    break;
                }

                if (ch == ';')
                {
                    break;
                }
            }

            current++;
        }

        return sql.Substring(offset, current - offset);
    }

    private static bool IsWordAt(string text, int index, string word)
    {
        if (index + word.Length > text.Length)
        {
            return false;
        }

        if (!text.AsSpan(index, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Перед словом должен быть пробельный символ, закрывающая скобка или начало строки
        if (index > 0 && !char.IsWhiteSpace(text[index - 1]) && text[index - 1] != ')')
        {
            return false;
        }

        // После слова должен быть пробельный символ, открывающая скобка, точка с запятой или конец строки
        var nextIndex = index + word.Length;
        if (nextIndex < text.Length && !char.IsWhiteSpace(text[nextIndex]) && text[nextIndex] != '(' && text[nextIndex] != ';')
        {
            return false;
        }

        return true;
    }

    private static List<string> SplitColumnsClause(string clause)
    {
        var items = new List<string>();
        var currentStart = 0;
        var parenthesisDepth = 0;

        for (var i = 0; i < clause.Length; i++)
        {
            var ch = clause[i];

            if (ch == '(')
            {
                parenthesisDepth++;
            }
            else if (ch == ')')
            {
                if (parenthesisDepth > 0)
                {
                    parenthesisDepth--;
                }
            }
            else if (ch == ',' && parenthesisDepth == 0)
            {
                items.Add(clause.Substring(currentStart, i - currentStart));
                currentStart = i + 1;
            }
        }

        if (currentStart < clause.Length)
        {
            items.Add(clause.Substring(currentStart));
        }

        return items;
    }

    private static string? ExtractSqlString(ExpressionSyntax expression, SyntaxNode contextNode)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return literal.Token.ValueText;

            case InterpolatedStringExpressionSyntax interpolated:
                return ExtractFromInterpolatedString(interpolated);

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                var left = ExtractSqlString(binary.Left, contextNode);
                var right = ExtractSqlString(binary.Right, contextNode);
                return left != null || right != null ? $"{left ?? string.Empty} {right ?? string.Empty}" : null;

            case IdentifierNameSyntax identifier:
                return ExtractFromIdentifier(identifier, contextNode);

            case InvocationExpressionSyntax invocation:
                return ExtractFromInvocation(invocation, contextNode);

            case ParenthesizedExpressionSyntax paren:
                return ExtractSqlString(paren.Expression, contextNode);

            default:
                return null;
        }
    }

    private static string ExtractFromInterpolatedString(InterpolatedStringExpressionSyntax interpolated)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var content in interpolated.Contents)
        {
            if (content is InterpolatedStringTextSyntax text)
            {
                sb.Append(text.TextToken.ValueText);
            }
            else if (content is InterpolationSyntax)
            {
                sb.Append(" ? ");
            }
        }

        return sb.ToString();
    }

    private static string? ExtractFromInvocation(InvocationExpressionSyntax invocation, SyntaxNode contextNode)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var caller = memberAccess.Expression.ToString();
            var name = memberAccess.Name.Identifier.Text;

            if (caller is "string" or "String")
            {
                var args = invocation.ArgumentList.Arguments;
                if (name == "Format" && args.Count > 0)
                {
                    return ExtractSqlString(args[0].Expression, contextNode);
                }

                if (name == "Concat")
                {
                    var parts = args.Select(a => ExtractSqlString(a.Expression, contextNode))
                                    .Where(s => !string.IsNullOrEmpty(s));
                    return string.Join(" ", parts);
                }
            }
        }

        return null;
    }

    private static string? ExtractFromIdentifier(IdentifierNameSyntax identifier, SyntaxNode contextNode)
    {
        var variableName = identifier.Identifier.Text;
        var enclosingScope = contextNode.Ancestors().FirstOrDefault(ancestor =>
            ancestor is MethodDeclarationSyntax or
                        LocalFunctionStatementSyntax or
                        AccessorDeclarationSyntax or
                        ParenthesizedLambdaExpressionSyntax or
                        AnonymousMethodExpressionSyntax or
                        SimpleLambdaExpressionSyntax or
                        ConstructorDeclarationSyntax);

        if (enclosingScope != null)
        {
            var declarator = enclosingScope.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .Where(d => d.Identifier.Text == variableName && d.SpanStart < identifier.SpanStart)
                .LastOrDefault();

            if (declarator?.Initializer?.Value is ExpressionSyntax initExpression)
            {
                var sql = ExtractSqlString(initExpression, declarator);

                var assignments = enclosingScope.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(a => a.SpanStart > declarator.SpanStart &&
                                a.SpanStart < identifier.SpanStart &&
                                a.Left is IdentifierNameSyntax leftId &&
                                leftId.Identifier.Text == variableName);

                foreach (var assignment in assignments)
                {
                    var assignedSql = ExtractSqlString(assignment.Right, assignment);
                    if (!string.IsNullOrEmpty(assignedSql))
                    {
                        sql = $"{sql} {assignedSql}";
                    }
                }

                return sql;
            }
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
                return ExtractSqlString(fieldInit, fieldDeclarator);
            }
        }

        return null;
    }
}
