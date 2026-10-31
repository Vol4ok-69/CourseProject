using Application.Exceptions;
using Application.Interfaces;
using Infrastructure.GitHub.Models;
using System.Net;
using System.Text.Json;

namespace Infrastructure.GitHub;

public class GitHubService(HttpClient httpClient) : IGitHubService
{
    public async Task<IReadOnlyList<GitHubCommitInfo>> GetCommitsAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        var repository = GitHubRepositoryReference.Parse(repositoryUrl);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync($"repos/{repository.Owner}/{repository.Name}/commits", cancellationToken);
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

    private static async Task HandleErrorResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var statusCode = response.StatusCode;

        // 404: Not Found (отсутствующий или приватный репозиторий)
        if (statusCode == HttpStatusCode.NotFound)
        {
            throw new GitHubIntegrationException(
                GitHubErrorKind.RepoNotFound,
                "Репозиторий GitHub не найден или является приватным.");
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
                "Доступ к репозиторию GitHub запрещен.");
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
            GitHubErrorKind.AccessDenied,
            $"GitHub API вернул статус {(int)statusCode} {statusCode}.");
    }
}
