namespace Api.Common;

/// <summary>
/// Содержит имена политик ограничения частоты запросов (Rate Limiting).
/// </summary>
public static class RateLimitingPolicies
{
    /// <summary>
    /// Имя политики ограничения частоты запросов для операций аутентификации.
    /// </summary>
    public const string AuthPolicy = "AuthPolicy";
}
