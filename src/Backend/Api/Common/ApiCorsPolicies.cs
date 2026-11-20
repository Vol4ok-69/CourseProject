using System.Globalization;
using System.Net;
using System.Net.Sockets;

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
    /// Список строго доверенных источников по умолчанию (для справки и тестов).
    /// Внимание: в runtime источником конфигурации служит только Cors:AllowedOrigins.
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

    /// <summary>
    /// Выполняет строгий разбор, валидацию и нормализацию списка разрешённых источников CORS (RFC 6454).
    /// </summary>
    /// <param name="rawConfig">Необработанная строка конфигурации из Cors:AllowedOrigins.</param>
    /// <returns>Массив нормализованных уникальных источников CORS.</returns>
    /// <exception cref="InvalidOperationException">Выбрасывается при отсутствии или некорректном формате конфигурации.</exception>
    public static string[] ParseAndValidateOrigins(string? rawConfig)
    {
        if (string.IsNullOrWhiteSpace(rawConfig))
        {
            throw new InvalidOperationException("Конфигурация разрешённых источников CORS 'Cors:AllowedOrigins' не задана или пуста.");
        }

        var tokens = rawConfig.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            throw new InvalidOperationException("Список разрешённых источников CORS пуст.");
        }

        var normalizedOrigins = new List<string>(tokens.Length);

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Список разрешённых источников CORS содержит пустой элемент.");
            }

            if (token.Contains('*'))
            {
                throw new InvalidOperationException($"Источник CORS '{token}' содержит символ подстановки '*', что запрещено политикой безопасности.");
            }

            if (token.Contains('?') || token.Contains('#'))
            {
                throw new InvalidOperationException($"Источник CORS '{token}' содержит query-параметры ('?') или фрагмент ('#'), что запрещено.");
            }

            if (token.Contains('\\'))
            {
                throw new InvalidOperationException($"Источник CORS '{token}' содержит символы обратного слэша '\\', что недопустимо.");
            }

            if (token.Contains('@'))
            {
                throw new InvalidOperationException($"Источник CORS '{token}' содержит информацию о пользователе (UserInfo), что запрещено.");
            }

            var schemeIndex = token.IndexOf("://", StringComparison.Ordinal);
            if (schemeIndex <= 0)
            {
                throw new InvalidOperationException($"Источник CORS '{token}' имеет некорректный формат схемы. Разрешены только абсолютные URI со схемами HTTP и HTTPS.");
            }

            var schemePart = token[..schemeIndex];
            if (!string.Equals(schemePart, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(schemePart, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Источник CORS '{token}' использует недопустимую схему '{schemePart}'. Разрешены только схемы HTTP и HTTPS.");
            }

            var afterScheme = token[(schemeIndex + 3)..];
            if (string.IsNullOrWhiteSpace(afterScheme))
            {
                throw new InvalidOperationException($"Источник CORS '{token}' не содержит имени хоста.");
            }

            var slashIndex = afterScheme.IndexOf('/');
            if (slashIndex >= 0)
            {
                if (slashIndex != afterScheme.Length - 1)
                {
                    throw new InvalidOperationException($"Источник CORS '{token}' содержит путь или сегменты маршрута, что запрещено.");
                }
            }

            var authority = slashIndex >= 0 ? afterScheme[..slashIndex] : afterScheme;
            if (string.IsNullOrWhiteSpace(authority))
            {
                throw new InvalidOperationException($"Источник CORS '{token}' не содержит имени хоста.");
            }

            string hostPart;
            string? portPartStr = null;

            if (authority.StartsWith('['))
            {
                var closingBracket = authority.IndexOf(']');
                if (closingBracket < 0)
                {
                    throw new InvalidOperationException($"Источник CORS '{token}' содержит некорректный формат IPv6-хоста: отсутствует закрывающая скобка ']'.");
                }

                var ipv6Content = authority[1..closingBracket];
                if (string.IsNullOrWhiteSpace(ipv6Content) ||
                    !IPAddress.TryParse(ipv6Content, out var parsedIp) ||
                    parsedIp.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    throw new InvalidOperationException($"Источник CORS '{token}' содержит некорректный IPv6-адрес '[{ipv6Content}]'.");
                }

                hostPart = authority[..(closingBracket + 1)];
                var remainder = authority[(closingBracket + 1)..];

                if (remainder.Length > 0)
                {
                    if (!remainder.StartsWith(':'))
                    {
                        throw new InvalidOperationException($"Источник CORS '{token}' содержит недопустимые символы после закрывающей скобки IPv6-хоста.");
                    }

                    portPartStr = remainder[1..];
                }
            }
            else
            {
                var colonIndex = authority.IndexOf(':');
                if (colonIndex >= 0)
                {
                    if (authority.IndexOf(':', colonIndex + 1) >= 0)
                    {
                        throw new InvalidOperationException($"Источник CORS '{token}' содержит несколько символов двоеточия вне формата IPv6. IPv6-адреса должны быть заключены в квадратные скобки '[...]'.");
                    }

                    hostPart = authority[..colonIndex];
                    portPartStr = authority[(colonIndex + 1)..];
                }
                else
                {
                    hostPart = authority;
                }
            }

            if (string.IsNullOrWhiteSpace(hostPart))
            {
                throw new InvalidOperationException($"Источник CORS '{token}' не содержит имени хоста.");
            }

            if (portPartStr is not null)
            {
                if (string.IsNullOrEmpty(portPartStr))
                {
                    throw new InvalidOperationException($"Источник CORS '{token}' содержит двоеточие без указания номера порта.");
                }

                if (!int.TryParse(portPartStr, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort))
                {
                    throw new InvalidOperationException($"Источник CORS '{token}' содержит некорректное нечисловое значение порта '{portPartStr}'.");
                }

                if (portPartStr.Length > 1 && portPartStr[0] == '0')
                {
                    throw new InvalidOperationException($"Источник CORS '{token}' содержит номер порта '{portPartStr}' с ведущими нулями, что недопустимо.");
                }

                if (parsedPort < 1 || parsedPort > 65535)
                {
                    throw new InvalidOperationException($"Источник CORS '{token}' содержит недопустимый номер порта {parsedPort}. Порт должен находиться в диапазоне от 1 до 65535.");
                }
            }

            if (!Uri.TryCreate(token, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException($"Источник CORS '{token}' не является корректным абсолютным URI.");
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException($"Источник CORS '{token}' использует схему '{uri.Scheme}', отличную от HTTP/HTTPS.");
            }

            if (string.IsNullOrWhiteSpace(uri.Host) || uri.HostNameType == UriHostNameType.Unknown)
            {
                throw new InvalidOperationException($"Источник CORS '{token}' не содержит допустимого имени хоста.");
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException($"Источник CORS '{token}' содержит информацию о пользователе (UserInfo), что запрещено.");
            }

            if (uri.AbsolutePath != "/")
            {
                throw new InvalidOperationException($"Источник CORS '{token}' содержит относительный путь '{uri.AbsolutePath}', что запрещено.");
            }

            if (uri.Port <= 0 || uri.Port > 65535)
            {
                throw new InvalidOperationException($"Источник CORS '{token}' содержит недопустимый номер порта {uri.Port}. Порт должен находиться в диапазоне от 1 до 65535.");
            }

            var normalizedScheme = uri.Scheme.ToLowerInvariant();
            string normalizedHost;
            if (uri.HostNameType == UriHostNameType.IPv6 && IPAddress.TryParse(uri.DnsSafeHost, out var ip))
            {
                normalizedHost = $"[{ip}]";
            }
            else
            {
                normalizedHost = uri.Host.ToLowerInvariant();
            }

            var portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            var normalizedOrigin = $"{normalizedScheme}://{normalizedHost}{portPart}";

            if (!normalizedOrigins.Contains(normalizedOrigin, StringComparer.Ordinal))
            {
                normalizedOrigins.Add(normalizedOrigin);
            }
        }

        if (normalizedOrigins.Count == 0)
        {
            throw new InvalidOperationException("Список разрешённых источников CORS не содержит валидных элементов.");
        }

        return [.. normalizedOrigins];
    }
}
