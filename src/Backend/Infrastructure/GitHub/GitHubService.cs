using Application.Exceptions;
using Application.Interfaces;
using Infrastructure.GitHub.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

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

        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{repository.Owner}/{repository.Name}/commits");
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

            try
            {
                var commits = JsonSerializer.Deserialize<List<GitHubCommitResponse>>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                return commits?.Select(commit => new GitHubCommitInfo(
                    commit.Sha,
                    commit.Commit?.Author?.Date
                )).ToList() ?? [];
            }
            catch (Exception ex)
            {
                throw new GitHubIntegrationException(
                    GitHubErrorKind.ServiceUnavailable,
                    "Ошибка обработки ответа GitHub API.",
                    ex);
            }
        }
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
}
