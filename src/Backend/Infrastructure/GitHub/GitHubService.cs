using Application.Exceptions;
using Application.Interfaces;
using Infrastructure.GitHub.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Infrastructure.GitHub;

public class GitHubService(HttpClient httpClient) : IGitHubService
{
    public async Task<IReadOnlyList<GitHubCommitInfo>> GetCommitsAsync(
        string repositoryUrl,
        string personalAccessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(personalAccessToken))
            throw new ArgumentException("Не указан персональный токен доступа GitHub (PAT).", nameof(personalAccessToken));

        var repository = GitHubRepositoryReference.Parse(repositoryUrl);

        var allCommits = new List<GitHubCommitInfo>();
        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var currentUrl = $"repos/{repository.Owner}/{repository.Name}/commits?per_page=100";

        while (!string.IsNullOrEmpty(currentUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personalAccessToken);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new GitHubIntegrationException(
                    GitHubErrorKind.ServiceUnavailable,
                    "Сервис GitHub недоступен или превышено время ожидания ответа.",
                    ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    await HandleErrorResponseAsync(response, cancellationToken);
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                List<GitHubCommitResponse>? pageCommits;
                try
                {
                    pageCommits = JsonSerializer.Deserialize<List<GitHubCommitResponse>>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                }
                catch (Exception ex)
                {
                    throw new GitHubIntegrationException(
                        GitHubErrorKind.ServiceUnavailable,
                        "Ошибка обработки ответа GitHub API.",
                        ex);
                }

                if (pageCommits is not null && pageCommits.Count > 0)
                {
                    foreach (var commit in pageCommits)
                    {
                        allCommits.Add(new GitHubCommitInfo(
                            commit.Sha,
                            commit.Commit?.Author?.Date
                        ));
                    }
                }

                var nextUrl = GetNextPageUrl(response.Headers);
                if (string.IsNullOrEmpty(nextUrl))
                {
                    break;
                }

                ValidateNextPageUrl(nextUrl, repository);

                if (!visitedUrls.Add(nextUrl))
                {
                    break;
                }

                currentUrl = nextUrl;
            }
        }

        return allCommits;
    }

    public async Task<string> VerifyTokenAndGetUsernameAsync(
        string personalAccessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(personalAccessToken))
            throw new ArgumentException("Не указан персональный токен доступа GitHub (PAT).", nameof(personalAccessToken));

        using var request = new HttpRequestMessage(HttpMethod.Get, "user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personalAccessToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.ServiceUnavailable,
                "Сервис GitHub недоступен или превышено время ожидания ответа при проверке токена.",
                ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                await HandleErrorResponseAsync(response, cancellationToken);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("login", out var loginProp))
                {
                    var login = loginProp.GetString();
                    if (!string.IsNullOrWhiteSpace(login))
                    {
                        return login;
                    }
                }

                throw new GitHubIntegrationException(
                    GitHubErrorKind.ServiceUnavailable,
                    "Не удалось определить имя пользователя GitHub из ответа API.");
            }
            catch (JsonException ex)
            {
                throw new GitHubIntegrationException(
                    GitHubErrorKind.ServiceUnavailable,
                    "Ошибка обработки ответа GitHub API при проверке токена.",
                    ex);
            }
        }
    }

    public Task<IReadOnlyList<GitHubCommitInfo>> GetCommitsAsync(
        string repositoryUrl,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Для выполнения запроса к GitHub API требуется персональный токен доступа (PAT).");
    }

    private static async Task HandleErrorResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var statusCode = response.StatusCode;

        // 401: Unauthorized (недействительный или отозванный токен)
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.TokenExpired,
                "Персональный токен GitHub недействителен или отозван.");
        }

        // 404: Not Found (отсутствующий или приватный репозиторий / токен не имеет доступа)
        if (statusCode == HttpStatusCode.NotFound)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.RepoNotFound,
                "Репозиторий GitHub не найден или токен не имеет к нему доступа.");
        }

        // 429: Too Many Requests (Rate limit)
        if ((int)statusCode == 429)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.RateLimited,
                "Превышен лимит запросов к GitHub API (Rate Limit).");
        }

        // 403: Forbidden (может быть Rate Limit, а может быть Access Denied / SSO)
        if (statusCode == HttpStatusCode.Forbidden)
        {
            var isRateLimited = false;

            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) &&
                int.TryParse(remainingValues.FirstOrDefault(), out var remaining) &&
                remaining == 0)
            {
                isRateLimited = true;
            }

            string body = string.Empty;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch
            {
            }

            if (!isRateLimited && !string.IsNullOrEmpty(body) &&
                (body.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase)))
            {
                isRateLimited = true;
            }

            if (isRateLimited)
            {
                throw new GitHubIntegrationException(
                    GitHubErrorKind.RateLimited,
                    "Превышен лимит запросов к GitHub API (Rate Limit).");
            }

            throw new GitHubIntegrationException(
                GitHubErrorKind.AccessDenied,
                "Доступ к ресурсу GitHub запрещен (недостаточно прав или ограничения организации).");
        }

        // 5xx: Ошибки самого GitHub
        if ((int)statusCode >= 500)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.ServiceUnavailable,
                "Сервис GitHub временно недоступен.");
        }

        // Любые прочие непредвиденные HTTP-статусы
        throw new GitHubIntegrationException(
            GitHubErrorKind.ServiceUnavailable,
            $"GitHub API вернул статус {(int)statusCode} {statusCode}.");
    }

    private static string? GetNextPageUrl(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var linkValues))
        {
            return null;
        }

        foreach (var linkHeader in linkValues)
        {
            var parts = linkHeader.Split(',');
            foreach (var part in parts)
            {
                var segments = part.Split(';');
                if (segments.Length < 2)
                {
                    continue;
                }

                var linkMatch = Regex.Match(segments[0].Trim(), @"^<([^>]+)>$");
                if (!linkMatch.Success)
                {
                    continue;
                }

                for (int i = 1; i < segments.Length; i++)
                {
                    var relMatch = Regex.Match(segments[i].Trim(), @"^rel\s*=\s*(?:""([^""]+)""|([^;,\s]+))$", RegexOptions.IgnoreCase);
                    if (relMatch.Success)
                    {
                        var relValue = relMatch.Groups[1].Success ? relMatch.Groups[1].Value : relMatch.Groups[2].Value;
                        if (string.Equals(relValue, "next", StringComparison.OrdinalIgnoreCase))
                        {
                            return linkMatch.Groups[1].Value;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static void ValidateNextPageUrl(string nextUrl, GitHubRepositoryReference repository)
    {
        if (!Uri.TryCreate(nextUrl, UriKind.Absolute, out var nextUri))
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.ServiceUnavailable,
                "Обнаружен некорректный URL следующей страницы в заголовке Link GitHub API.");
        }

        if (!string.Equals(nextUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nextUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.ServiceUnavailable,
                "URL следующей страницы в заголовке Link GitHub API указывает на недопустимый хост или протокол.");
        }

        var expectedRepoPath = $"/repos/{repository.Owner}/{repository.Name}/commits";
        var isStandardPath = nextUri.AbsolutePath.StartsWith(expectedRepoPath, StringComparison.OrdinalIgnoreCase);
        var isRepositoriesIdPath = Regex.IsMatch(nextUri.AbsolutePath, @"^/repositories/\d+/commits(?:/)?$", RegexOptions.IgnoreCase);

        if (!isStandardPath && !isRepositoriesIdPath)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.ServiceUnavailable,
                "URL следующей страницы в заголовке Link GitHub API указывает на недопустимый путь ресурса.");
        }
    }
}
