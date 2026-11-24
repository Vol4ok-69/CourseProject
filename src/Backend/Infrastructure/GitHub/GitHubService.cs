using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Infrastructure.GitHub.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Infrastructure.GitHub;

public class GitHubService(HttpClient httpClient) : IGitHubService
{
    public async Task<GitHubRepositoryInfoDto> GetRepositoryInfoAsync(
        string repositoryUrl,
        string personalAccessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(personalAccessToken))
            throw new ArgumentException("Не указан персональный токен доступа GitHub (PAT).", nameof(personalAccessToken));

        var repository = GitHubRepositoryReference.Parse(repositoryUrl);

        // 1. Получаем метаданные репозитория (ID, default_branch)
        using var repoRequest = new HttpRequestMessage(HttpMethod.Get, $"repos/{repository.Owner}/{repository.Name}");
        repoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personalAccessToken);

        HttpResponseMessage repoResponse;
        try
        {
            repoResponse = await httpClient.SendAsync(repoRequest, cancellationToken);
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

        string defaultBranch;
        string repositoryId;
        var apiHost = httpClient.BaseAddress?.Host ?? "api.github.com";

        using (repoResponse)
        {
            if (!repoResponse.IsSuccessStatusCode)
            {
                await HandleErrorResponseAsync(repoResponse, cancellationToken);
            }

            var json = await repoResponse.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                repositoryId = root.GetProperty("id").GetInt64().ToString();
                defaultBranch = root.TryGetProperty("default_branch", out var branchProp)
                    ? branchProp.GetString() ?? "main"
                    : "main";
            }
            catch (Exception ex)
            {
                throw new GitHubIntegrationException(
                    GitHubErrorKind.ServiceUnavailable,
                    "Ошибка обработки ответа GitHub API при получении информации о репозитории.",
                    ex);
            }
        }

        // 2. Получаем актуальный HEAD-коммит ветки по умолчанию
        using var branchRequest = new HttpRequestMessage(HttpMethod.Get, $"repos/{repository.Owner}/{repository.Name}/commits/{Uri.EscapeDataString(defaultBranch)}");
        branchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personalAccessToken);

        HttpResponseMessage branchResponse;
        try
        {
            branchResponse = await httpClient.SendAsync(branchRequest, cancellationToken);
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

        string headSha;
        using (branchResponse)
        {
            if (!branchResponse.IsSuccessStatusCode)
            {
                await HandleErrorResponseAsync(branchResponse, cancellationToken);
            }

            var json = await branchResponse.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var doc = JsonDocument.Parse(json);
                headSha = doc.RootElement.GetProperty("sha").GetString()!;
                if (string.IsNullOrWhiteSpace(headSha))
                {
                    throw new InvalidOperationException("HEAD SHA репозитория пуст.");
                }
            }
            catch (Exception ex)
            {
                throw new GitHubIntegrationException(
                    GitHubErrorKind.ServiceUnavailable,
                    "Ошибка обработки ответа GitHub API при получении HEAD коммита ветки.",
                    ex);
            }
        }

        return new GitHubRepositoryInfoDto(apiHost, repositoryId, defaultBranch, headSha);
    }

    public async IAsyncEnumerable<IReadOnlyList<GitHubCommitInfo>> GetCommitPagesAsync(
        string repositoryUrl,
        string? sha,
        string personalAccessToken,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(personalAccessToken))
            throw new ArgumentException("Не указан персональный токен доступа GitHub (PAT).", nameof(personalAccessToken));

        var repository = GitHubRepositoryReference.Parse(repositoryUrl);
        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var currentUrl = string.IsNullOrWhiteSpace(sha)
            ? $"repos/{repository.Owner}/{repository.Name}/commits?per_page=100"
            : $"repos/{repository.Owner}/{repository.Name}/commits?sha={Uri.EscapeDataString(sha)}&per_page=100";

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

            List<GitHubCommitInfo> pageCommitsList;
            string? nextUrl;

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

                pageCommitsList = new List<GitHubCommitInfo>();
                if (pageCommits is not null && pageCommits.Count > 0)
                {
                    foreach (var commit in pageCommits)
                    {
                        var parentHashes = commit.Parents?
                            .Select(p => p.Sha)
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

                        pageCommitsList.Add(new GitHubCommitInfo(
                            commit.Sha,
                            commit.Commit?.Author?.Date,
                            parentHashes
                        ));
                    }
                }

                nextUrl = GetNextPageUrl(response.Headers);
            }

            yield return pageCommitsList;

            if (string.IsNullOrEmpty(nextUrl))
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(sha))
            {
                nextUrl = EnsureAndValidateShaParameter(nextUrl, sha);
            }

            ValidateNextPageUrl(nextUrl, repository);

            if (!visitedUrls.Add(nextUrl))
            {
                break;
            }

            currentUrl = nextUrl;
        }
    }

    public async Task<IReadOnlyList<GitHubCommitInfo>> GetCommitsAsync(
        string repositoryUrl,
        string personalAccessToken,
        CancellationToken cancellationToken = default)
    {
        var allCommits = new List<GitHubCommitInfo>();

        await foreach (var page in GetCommitPagesAsync(repositoryUrl, null, personalAccessToken, cancellationToken))
        {
            allCommits.AddRange(page);
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
        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
        }

        if ((int)statusCode == 429)
        {
            throw Application.Services.GitHubRateLimitParser.ParseRateLimitException(response, body, DateTimeOffset.UtcNow);
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

            if (!isRateLimited && !string.IsNullOrEmpty(body) &&
                (body.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)))
            {
                isRateLimited = true;
            }

            if (isRateLimited)
            {
                throw Application.Services.GitHubRateLimitParser.ParseRateLimitException(response, body, DateTimeOffset.UtcNow);
            }

            throw new GitHubIntegrationException(
                GitHubErrorKind.AccessDenied,
                "Доступ к ресурсу GitHub запрещен (недостаточно прав или ограничения организации).")
            {
                StatusCode = statusCode
            };
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

    internal static string EnsureAndValidateShaParameter(string url, string expectedSha)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL не может быть пустым.", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(expectedSha))
        {
            throw new ArgumentException("Ожидаемый SHA не может быть пустым.", nameof(expectedSha));
        }

        var qIndex = url.IndexOf('?');
        if (qIndex < 0)
        {
            return $"{url}?sha={Uri.EscapeDataString(expectedSha)}";
        }

        var baseUrl = url[..qIndex];
        var queryString = url[(qIndex + 1)..];

        var queryParts = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var shaValues = new List<string>();

        foreach (var part in queryParts)
        {
            var kv = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]);
            if (string.Equals(key, "sha", StringComparison.OrdinalIgnoreCase))
            {
                var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
                shaValues.Add(val);
            }
        }

        if (shaValues.Count > 1)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.ServiceUnavailable,
                "URL следующей страницы содержит повторяющийся параметр sha.");
        }

        if (shaValues.Count == 1)
        {
            if (!string.Equals(shaValues[0], expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new GitHubIntegrationException(
                    GitHubErrorKind.ServiceUnavailable,
                    $"URL следующей страницы ссылается на несовпадающий commit SHA: ожидался '{expectedSha}', получен '{shaValues[0]}'.");
            }

            return url;
        }

        return $"{url}&sha={Uri.EscapeDataString(expectedSha)}";
    }

    private void ValidateNextPageUrl(string nextUrl, GitHubRepositoryReference repository)
    {
        var expectedHost = httpClient.BaseAddress?.Host ?? "api.github.com";
        ValidateNextPageUrl(nextUrl, repository, expectedHost);
    }

    internal static void ValidateNextPageUrl(string nextUrl, GitHubRepositoryReference repository, string expectedHost)
    {
        if (string.IsNullOrWhiteSpace(expectedHost))
        {
            expectedHost = "api.github.com";
        }

        if (!Uri.TryCreate(nextUrl, UriKind.Absolute, out var nextUri))
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.ServiceUnavailable,
                "Обнаружен некорректный URL следующей страницы в заголовке Link GitHub API.");
        }

        if (!string.Equals(nextUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nextUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.ServiceUnavailable,
                "URL следующей страницы в заголовке Link GitHub API указывает на недопустимый хост или протокол.");
        }

        var expectedRepoPath = $"/repos/{repository.Owner}/{repository.Name}/commits";
        var isStandardPath = string.Equals(nextUri.AbsolutePath, expectedRepoPath, StringComparison.OrdinalIgnoreCase)
            || nextUri.AbsolutePath.StartsWith(expectedRepoPath + "/", StringComparison.OrdinalIgnoreCase);
        var isRepositoriesIdPath = Regex.IsMatch(nextUri.AbsolutePath, @"^/repositories/\d+/commits(?:/.*)?$", RegexOptions.IgnoreCase);

        if (!isStandardPath && !isRepositoriesIdPath)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.ServiceUnavailable,
                "URL следующей страницы в заголовке Link GitHub API указывает на недопустимый путь ресурса.");
        }
    }
}
