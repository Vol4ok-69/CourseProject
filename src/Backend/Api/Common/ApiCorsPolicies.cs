namespace Api.Common;

/// <summary>
/// Содержит константы и параметры конфигурации политики CORS приложения.
/// </summary>
public static class ApiCorsPolicies
{
    /// <summary>
    /// Имя политики CORS для локальной веб-разработки.
    /// </summary>
    public const string FrontendDevPolicy = "AllowFrontendDev";

    /// <summary>
    /// Список строго доверенных источников для локальной веб-разработки.
    /// </summary>
    public static readonly string[] AllowedOrigins =
    [
        "http://localhost:5173",
        "http://localhost:3000"
    ];

    /// <summary>
    /// Список разрешенных HTTP-методов.
    /// </summary>
    public static readonly string[] AllowedMethods =
    [
        "GET",
        "POST",
        "PUT",
        "DELETE",
        "OPTIONS"
    ];

    /// <summary>
    /// Список разрешенных HTTP-заголовков.
    /// </summary>
    public static readonly string[] AllowedHeaders =
    [
        "Authorization",
        "Content-Type",
        "Accept"
    ];
}
