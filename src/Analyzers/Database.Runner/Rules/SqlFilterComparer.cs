using System.Text;

namespace NetScope.DatabaseAnalyzer.Runner.Rules;

/// <summary>
/// Безопасный и консервативный компаратор SQL-фильтров индексов.
/// Сохраняет строковые литералы (включая пробелы, регистр и экранированные кавычки),
/// различает чувствительность к регистру идентификаторов в кавычках (PostgreSQL),
/// различает различные диалекты экранирования ("col", [col], `col`),
/// не считает сравнения с NULL (= NULL, != NULL) эквивалентными IS NULL / IS NOT NULL,
/// безопасно снимает только сбалансированные внешние скобки.
/// При синтаксической неоднозначности консервативно признаёт фильтры неэквивалентными.
/// </summary>
public static class SqlFilterComparer
{
    public static bool AreFiltersEquivalent(string? filter1, string? filter2, out bool isAmbiguous)
    {
        isAmbiguous = false;

        // Оба фильтра отсутствуют -> эквивалентны
        if (string.IsNullOrWhiteSpace(filter1) && string.IsNullOrWhiteSpace(filter2))
        {
            return true;
        }

        // Один фильтр есть, другой отсутствует -> не эквивалентны
        if (string.IsNullOrWhiteSpace(filter1) || string.IsNullOrWhiteSpace(filter2))
        {
            return false;
        }

        var unwrapped1 = StripOuterParentheses(filter1);
        var unwrapped2 = StripOuterParentheses(filter2);

        // Точное посимвольное совпадение после снятия внешних скобок
        if (string.Equals(unwrapped1, unwrapped2, StringComparison.Ordinal))
        {
            return true;
        }

        // Токенизация выражений
        var tokens1 = Tokenize(unwrapped1, out var error1);
        var tokens2 = Tokenize(unwrapped2, out var error2);

        if (error1 || error2)
        {
            // Ошибка синтаксического разбора (например, незакрытая кавычка или неизвестная конструкция)
            // Консервативно признаём сравнение неоднозначным
            isAmbiguous = true;
            return false;
        }

        if (tokens1.Count != tokens2.Count)
        {
            return false;
        }

        for (var i = 0; i < tokens1.Count; i++)
        {
            if (!tokens1[i].Equals(tokens2[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static string StripOuterParentheses(string filter)
    {
        var s = filter.Trim();

        while (s.StartsWith('(') && s.EndsWith(')'))
        {
            var depth = 0;
            var matchesEnd = false;

            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\'')
                {
                    // Пропуск строкового литерала
                    i++;
                    while (i < s.Length)
                    {
                        if (s[i] == '\'')
                        {
                            if (i + 1 < s.Length && s[i + 1] == '\'')
                            {
                                i += 2; // Экранированная одинарная кавычка ''
                                continue;
                            }
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                if (s[i] == '"')
                {
                    // Пропуск экранированного идентификатора в двойных кавычках
                    i++;
                    while (i < s.Length && s[i] != '"')
                    {
                        i++;
                    }
                    continue;
                }

                if (s[i] == '[')
                {
                    // Пропуск идентификатора в квадратных скобках
                    i++;
                    while (i < s.Length && s[i] != ']')
                    {
                        i++;
                    }
                    continue;
                }

                if (s[i] == '`')
                {
                    // Пропуск идентификатора в обратных кавычках
                    i++;
                    while (i < s.Length && s[i] != '`')
                    {
                        i++;
                    }
                    continue;
                }

                if (s[i] == '(')
                {
                    depth++;
                }
                else if (s[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        matchesEnd = (i == s.Length - 1);
                        break;
                    }
                }
            }

            if (matchesEnd)
            {
                s = s[1..^1].Trim();
            }
            else
            {
                break;
            }
        }

        return s;
    }

    private static List<FilterToken> Tokenize(string sql, out bool hasError)
    {
        hasError = false;
        var tokens = new List<FilterToken>();
        var span = sql.AsSpan();
        var i = 0;

        while (i < span.Length)
        {
            // Пропуск пробелов между токенами (не внутри литералов!)
            if (char.IsWhiteSpace(span[i]))
            {
                i++;
                continue;
            }

            // Строковый литерал '...' с сохранением экранированных кавычек и всех пробелов
            if (span[i] == '\'')
            {
                var sb = new StringBuilder();
                i++; // Пропускаем открывающую кавычку
                var closed = false;

                while (i < span.Length)
                {
                    if (span[i] == '\'')
                    {
                        if (i + 1 < span.Length && span[i + 1] == '\'')
                        {
                            sb.Append("''");
                            i += 2;
                            continue;
                        }
                        closed = true;
                        i++; // Пропускаем закрывающую кавычку
                        break;
                    }

                    sb.Append(span[i]);
                    i++;
                }

                if (!closed)
                {
                    hasError = true;
                    return tokens;
                }

                tokens.Add(new FilterToken(TokenType.StringLiteral, sb.ToString(), QuoteStyle.None));
                continue;
            }

            // Идентификатор в квадратных скобках [col] (T-SQL)
            if (span[i] == '[')
            {
                var closeIdx = sql.IndexOf(']', i);
                if (closeIdx == -1)
                {
                    hasError = true;
                    return tokens;
                }

                var id = sql[(i + 1)..closeIdx];
                tokens.Add(new FilterToken(TokenType.QuotedIdentifier, id, QuoteStyle.SquareBracket));
                i = closeIdx + 1;
                continue;
            }

            // Идентификатор в двойных кавычках "col" (PostgreSQL / ANSI SQL, чувствителен к регистру)
            if (span[i] == '"')
            {
                var closeIdx = sql.IndexOf('"', i + 1);
                if (closeIdx == -1)
                {
                    hasError = true;
                    return tokens;
                }

                var id = sql[(i + 1)..closeIdx];
                tokens.Add(new FilterToken(TokenType.QuotedIdentifier, id, QuoteStyle.DoubleQuote));
                i = closeIdx + 1;
                continue;
            }

            // Идентификатор в обратных кавычках `col` (MySQL)
            if (span[i] == '`')
            {
                var closeIdx = sql.IndexOf('`', i + 1);
                if (closeIdx == -1)
                {
                    hasError = true;
                    return tokens;
                }

                var id = sql[(i + 1)..closeIdx];
                tokens.Add(new FilterToken(TokenType.QuotedIdentifier, id, QuoteStyle.Backtick));
                i = closeIdx + 1;
                continue;
            }

            // Скобки
            if (span[i] == '(')
            {
                tokens.Add(new FilterToken(TokenType.OpenParen, "(", QuoteStyle.None));
                i++;
                continue;
            }
            if (span[i] == ')')
            {
                tokens.Add(new FilterToken(TokenType.CloseParen, ")", QuoteStyle.None));
                i++;
                continue;
            }

            // Операторы: =, <>, !=, <=, >=, <, >
            if (span[i] == '<' && i + 1 < span.Length && (span[i + 1] == '>' || span[i + 1] == '='))
            {
                tokens.Add(new FilterToken(TokenType.Operator, span[i + 1] == '>' ? "<>" : "<=", QuoteStyle.None));
                i += 2;
                continue;
            }
            if (span[i] == '!' && i + 1 < span.Length && span[i + 1] == '=')
            {
                tokens.Add(new FilterToken(TokenType.Operator, "<>", QuoteStyle.None)); // != эквивалентен <> в SQL
                i += 2;
                continue;
            }
            if (span[i] == '>' && i + 1 < span.Length && span[i + 1] == '=')
            {
                tokens.Add(new FilterToken(TokenType.Operator, ">=", QuoteStyle.None));
                i += 2;
                continue;
            }
            if (span[i] == '=')
            {
                tokens.Add(new FilterToken(TokenType.Operator, "=", QuoteStyle.None));
                i++;
                continue;
            }
            if (span[i] == '<')
            {
                tokens.Add(new FilterToken(TokenType.Operator, "<", QuoteStyle.None));
                i++;
                continue;
            }
            if (span[i] == '>')
            {
                tokens.Add(new FilterToken(TokenType.Operator, ">", QuoteStyle.None));
                i++;
                continue;
            }

            // Идентификаторы, ключевые слова (IS, NOT, NULL, AND, OR, TRUE, FALSE) или числа
            var start = i;
            while (i < span.Length && !char.IsWhiteSpace(span[i]) &&
                   span[i] != '(' && span[i] != ')' && span[i] != '\'' &&
                   span[i] != '[' && span[i] != ']' && span[i] != '"' && span[i] != '`' &&
                   span[i] != '=' && span[i] != '<' && span[i] != '>' && span[i] != '!')
            {
                i++;
            }

            var text = sql[start..i];

            if (long.TryParse(text, out _) || double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                tokens.Add(new FilterToken(TokenType.Number, text, QuoteStyle.None));
            }
            else if (IsKeyword(text))
            {
                tokens.Add(new FilterToken(TokenType.Keyword, text.ToUpperInvariant(), QuoteStyle.None));
            }
            else
            {
                tokens.Add(new FilterToken(TokenType.UnquotedIdentifier, text, QuoteStyle.None));
            }
        }

        return tokens;
    }

    private static bool IsKeyword(string word)
    {
        return word.Equals("IS", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("NOT", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("NULL", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("OR", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("LIKE", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("IN", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
    }

    private enum TokenType
    {
        UnquotedIdentifier,
        QuotedIdentifier,
        StringLiteral,
        Number,
        Operator,
        Keyword,
        OpenParen,
        CloseParen
    }

    private enum QuoteStyle
    {
        None,
        DoubleQuote,
        SquareBracket,
        Backtick
    }

    private sealed record FilterToken(TokenType Type, string Value, QuoteStyle Quote)
    {
        public bool Equals(FilterToken? other)
        {
            if (other is null || Type != other.Type) return false;

            // Строковые литералы сравниваются строго посимвольно (Ordinal) с учётом регистра и пробелов
            if (Type == TokenType.StringLiteral)
            {
                return string.Equals(Value, other.Value, StringComparison.Ordinal);
            }

            // Quoted-идентификаторы в двойных кавычках ("Col") в PostgreSQL регистрозависимы.
            // Разные стили квотирования ([Col] vs "Col" vs `Col`) представляют разные диалекты и не эквивалентны.
            if (Type == TokenType.QuotedIdentifier)
            {
                if (Quote != other.Quote) return false;
                // Для "Col" (PostgreSQL / ANSI) регистр значим
                if (Quote == QuoteStyle.DoubleQuote)
                {
                    return string.Equals(Value, other.Value, StringComparison.Ordinal);
                }
                // Для [Col] (T-SQL) по умолчанию регистронезависимо, но сохраняем стиль квотирования
                return string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
            }

            // Неквотированные идентификаторы в стандартном SQL нечувствительны к регистру
            if (Type == TokenType.UnquotedIdentifier)
            {
                return string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
            }

            // Ключевые слова, операторы, числа
            return string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
        }

        public override int GetHashCode()
        {
            if (Type == TokenType.StringLiteral || (Type == TokenType.QuotedIdentifier && Quote == QuoteStyle.DoubleQuote))
            {
                return HashCode.Combine(Type, Value, Quote);
            }
            return HashCode.Combine(Type, Value.ToLowerInvariant(), Quote);
        }
    }
}
