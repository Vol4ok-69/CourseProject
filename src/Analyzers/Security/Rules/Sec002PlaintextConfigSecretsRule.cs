using Application.Analyzers;
using Domain.Enums;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SecurityAnalyzer.Rules;

/// <summary>
/// SEC002: Поиск открытых секретов и паролей в файлах конфигурации (appsettings*.json).
/// Использует Utf8JsonReader со стеком контекстов JSON для точного определения номеров строк каждого свойства,
/// надёжного сохранения иерархии ключей при разборе комбинаций объектов и массивов, а также предотвращения коллизий.
/// Значения обнаруженных секретов скрываются во избежание утечки через отчёты анализатора.
/// </summary>
public sealed class Sec002PlaintextConfigSecretsRule : IConfigSecurityRule
{
    private static readonly Regex ConnectionStringPasswordRegex = new(
        @"(?:(?:Host|Server|Data\s+Source|Database)\s*=.*?;.*?\b(?:Password|Pwd)\s*=\s*(?<pwd>[^;""'\s]+)|\b(?:Password|Pwd)\s*=\s*(?<pwd>[^;""'\s]+)\s*;.*?\b(?:Host|Server|Data\s+Source|Database)\s*=)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SensitiveKeyRegex = new(
        @"(?i)(password|passwd|pwd|secret|api_?key|jwt_?secret|private_?key|client_?secret|token_?key)",
        RegexOptions.Compiled);

    private static readonly Regex ExcludedKeySuffixRegex = new(
        @"(?i)(hash|salt|hasher|regex|pattern|length|policy|expiry|expiration|format|claim|type|header|name|field|param|attribute|column|keyname|validator|audience|issuer|validissuer|validaudience)$",
        RegexOptions.Compiled);

    // Только явные шаблоны-заполнители. Распространённые слабые пароли намеренно НЕ входят сюда, так как являются реальными уязвимостями.
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "TODO",
        "CHANGEME",
        "CHANGE_ME",
        "REPLACE_ME",
        "YOUR_PASSWORD",
        "YOUR_SECRET",
        "YOUR_KEY",
        "YOUR_API_KEY",
        "PLACEHOLDER",
        "***"
    };

    private enum JsonScopeType
    {
        Object,
        Array
    }

    private sealed class JsonScope
    {
        public JsonScopeType Type { get; }
        public string? PropertyName { get; }

        public JsonScope(JsonScopeType type, string? propertyName)
        {
            Type = type;
            PropertyName = propertyName;
        }
    }

    public IReadOnlyList<AnalyzerFinding> Analyze(string jsonContent, string filePath)
    {
        var findings = new List<AnalyzerFinding>();

        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            return findings;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(jsonContent);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            var scopeStack = new Stack<JsonScope>();
            string? currentPropName = null;
            long currentPropOffset = 0;

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        currentPropName = reader.GetString();
                        currentPropOffset = reader.TokenStartIndex;
                        break;

                    case JsonTokenType.StartObject:
                        scopeStack.Push(new JsonScope(JsonScopeType.Object, currentPropName));
                        currentPropName = null;
                        break;

                    case JsonTokenType.StartArray:
                        scopeStack.Push(new JsonScope(JsonScopeType.Array, currentPropName));
                        currentPropName = null;
                        break;

                    case JsonTokenType.EndObject:
                        if (scopeStack.Count > 0)
                        {
                            scopeStack.Pop();
                        }
                        currentPropName = null;
                        break;

                    case JsonTokenType.EndArray:
                        if (scopeStack.Count > 0)
                        {
                            scopeStack.Pop();
                        }
                        currentPropName = null;
                        break;

                    case JsonTokenType.String:
                        var stringValue = reader.GetString() ?? string.Empty;
                        var propName = currentPropName ?? GetNearestPropertyName(scopeStack);
                        var fullPath = BuildFullPath(scopeStack, currentPropName);
                        var offset = currentPropName != null ? currentPropOffset : reader.TokenStartIndex;

                        CheckStringValue(fullPath, propName, stringValue, bytes, offset, filePath, findings);
                        currentPropName = null;
                        break;

                    default:
                        currentPropName = null;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Повреждённый JSON не прерывает анализ остальных файлов проекта
        }

        return findings;
    }

    private static void CheckStringValue(
        string fullPath,
        string propName,
        string stringValue,
        byte[] bytes,
        long propOffset,
        string filePath,
        List<AnalyzerFinding> findings)
    {
        // 1. Проверка строки подключения
        if (fullPath.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase) ||
            propName.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase))
        {
            var match = ConnectionStringPasswordRegex.Match(stringValue);
            if (match.Success)
            {
                var pwd = match.Groups["pwd"].Value.Trim();
                if (IsRealSecretValue(pwd))
                {
                    var line = CalculateLineNumber(bytes, propOffset);
                    findings.Add(new AnalyzerFinding
                    {
                        FilePath = filePath,
                        LineNumber = line,
                        Rule = "SEC002",
                        Message = $"В файле конфигурации обнаружена строка подключения с открытым паролем в секции '{fullPath}' (пароль скрыт).",
                        Severity = Severity.Warning,
                        Recommendation = "Не храните пароли в файлах конфигурации репозитория. Используйте переменные окружения, User Secrets в среде разработки или сервис управления секретами."
                    });
                }
            }
        }
        // 2. Проверка чувствительных ключей
        else if (IsSensitiveKey(propName) && IsRealSecretValue(stringValue))
        {
            var line = CalculateLineNumber(bytes, propOffset);
            findings.Add(new AnalyzerFinding
            {
                FilePath = filePath,
                LineNumber = line,
                Rule = "SEC002",
                Message = $"В файле конфигурации обнаружен открытый секрет или пароль по пути '{fullPath}' (значение скрыто).",
                Severity = Severity.Warning,
                Recommendation = "Удалите открытое значение секрета из файла конфигурации. Задавайте его через переменные окружения, User Secrets или Secret Manager."
            });
        }
    }

    private static string GetNearestPropertyName(IEnumerable<JsonScope> scopeStack)
    {
        foreach (var scope in scopeStack)
        {
            if (!string.IsNullOrEmpty(scope.PropertyName))
            {
                return scope.PropertyName;
            }
        }
        return string.Empty;
    }

    private static string BuildFullPath(Stack<JsonScope> scopeStack, string? currentProp)
    {
        var segments = new List<string>();
        var scopes = scopeStack.ToArray();
        for (var i = scopes.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrEmpty(scopes[i].PropertyName))
            {
                segments.Add(scopes[i].PropertyName!);
            }
        }

        if (!string.IsNullOrEmpty(currentProp))
        {
            segments.Add(currentProp);
        }

        return string.Join(":", segments);
    }

    private static bool IsSensitiveKey(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return false;
        }

        if (ExcludedKeySuffixRegex.IsMatch(keyName))
        {
            return false;
        }

        return SensitiveKeyRegex.IsMatch(keyName);
    }

    private static bool IsRealSecretValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        // Переменные окружения или шаблоны: ${VAR}, #{VAR}#, %VAR%, <PLACEHOLDER>, {PLACEHOLDER}
        if ((trimmed.StartsWith("${") && trimmed.EndsWith("}")) ||
            (trimmed.StartsWith("#{") && trimmed.EndsWith("}#")) ||
            (trimmed.StartsWith("%") && trimmed.EndsWith("%")) ||
            (trimmed.StartsWith("<") && trimmed.EndsWith(">")) ||
            (trimmed.StartsWith("{") && trimmed.EndsWith("}")))
        {
            return false;
        }

        if (PlaceholderValues.Contains(trimmed))
        {
            return false;
        }

        return true;
    }

    private static int CalculateLineNumber(byte[] bytes, long byteOffset)
    {
        var limit = Math.Min(byteOffset, (long)bytes.Length);
        var line = 1;
        for (var i = 0; i < limit; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                line++;
            }
        }
        return line;
    }
}
