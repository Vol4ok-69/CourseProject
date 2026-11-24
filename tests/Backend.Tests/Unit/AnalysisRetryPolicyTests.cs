using System.Net;
using Application.Exceptions;
using Application.Services;
using Npgsql;
using Xunit;

namespace Backend.Tests.Unit;

public sealed class AnalysisRetryPolicyTests
{
    private static AnalysisRetryPolicy CreateDeterministicPolicy(
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        TimeSpan? maxRateLimitWait = null)
    {
        // Детерминированный jitter = 0 для точных ассертов в тестах
        return new AnalysisRetryPolicy(
            baseDelay: baseDelay ?? TimeSpan.FromSeconds(10),
            maxDelay: maxDelay ?? TimeSpan.FromSeconds(300),
            maxRateLimitWait: maxRateLimitWait ?? TimeSpan.FromMinutes(15),
            jitterProvider: d => d);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    public void Evaluate_WhenAttemptCountReachesMax_ReturnsPoisonPill(int currentAttempt, int maxAttempts)
    {
        var policy = CreateDeterministicPolicy();
        var ex = new HttpRequestException("Network failure");

        var decision = policy.Evaluate(ex, currentAttempt, maxAttempts);

        Assert.False(decision.ShouldRetry);
        Assert.True(decision.IsPoisonPill);
        Assert.Null(decision.Delay);
        Assert.Contains("Poison Pill", decision.Reason);
    }

    [Fact]
    public void Evaluate_ArchiveLimitExceeded_ReturnsNonRetryable()
    {
        var policy = CreateDeterministicPolicy();
        var ex = new ArchiveLimitExceededException("Zip bomb detected");

        var decision = policy.Evaluate(ex, currentAttemptCount: 1, maxAttempts: 3);

        Assert.False(decision.ShouldRetry);
        Assert.False(decision.IsPoisonPill);
        Assert.Null(decision.Delay);
        Assert.Contains("Превышен лимит", decision.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void Evaluate_GitHubClientError_ReturnsNonRetryable(HttpStatusCode statusCode)
    {
        var policy = CreateDeterministicPolicy();
        var ex = new GitHubIntegrationException("Client error", statusCode);

        var decision = policy.Evaluate(ex, currentAttemptCount: 1, maxAttempts: 3);

        Assert.False(decision.ShouldRetry);
        Assert.False(decision.IsPoisonPill);
        Assert.Null(decision.Delay);
        Assert.Contains($"HTTP {(int)statusCode}", decision.Reason);
    }

    [Fact]
    public void Evaluate_GitHubRateLimit_ValidDelayUnderMaxWait_ReturnsRetryWithExactDelay()
    {
        var policy = CreateDeterministicPolicy();
        var ex = new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Rate limited")
        {
            StatusCode = HttpStatusCode.Forbidden,
            RetryAfterDelay = TimeSpan.FromSeconds(120),
            HasInvalidHeaders = false
        };

        var decision = policy.Evaluate(ex, currentAttemptCount: 1, maxAttempts: 3);

        Assert.True(decision.ShouldRetry);
        Assert.False(decision.IsPoisonPill);
        Assert.Equal(TimeSpan.FromSeconds(120), decision.Delay);
    }

    [Fact]
    public void Evaluate_GitHubRateLimit_ExceedingMaxWait_ReturnsNonRetryable()
    {
        var policy = CreateDeterministicPolicy(maxRateLimitWait: TimeSpan.FromMinutes(15));
        var ex = new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Rate limited")
        {
            StatusCode = HttpStatusCode.Forbidden,
            RetryAfterDelay = TimeSpan.FromMinutes(20),
            HasInvalidHeaders = false
        };

        var decision = policy.Evaluate(ex, currentAttemptCount: 1, maxAttempts: 3);

        Assert.False(decision.ShouldRetry);
        Assert.False(decision.IsPoisonPill);
        Assert.Null(decision.Delay);
        Assert.Contains("превышает допустимый максимум", decision.Reason);
    }

    [Fact]
    public void Evaluate_GitHubRateLimit_WithInvalidHeaders_ReturnsNonRetryable()
    {
        var policy = CreateDeterministicPolicy();
        var ex = new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Rate limited with bad headers")
        {
            StatusCode = (HttpStatusCode)429,
            HasInvalidHeaders = true
        };

        var decision = policy.Evaluate(ex, currentAttemptCount: 1, maxAttempts: 3);

        Assert.False(decision.ShouldRetry);
        Assert.False(decision.IsPoisonPill);
        Assert.Null(decision.Delay);
        Assert.Contains("некорректные или повреждённые заголовки", decision.Reason);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    public void Evaluate_GitHubSecondaryRateLimit_WithoutHeaders_AppliesExponentialStep(int attempt, int expectedSeconds)
    {
        var policy = CreateDeterministicPolicy();
        var ex = new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Secondary limit triggered")
        {
            StatusCode = (HttpStatusCode)429,
            RateLimitCategory = GitHubRateLimitCategory.SecondaryAbuseTriggered,
            RetryAfterDelay = null,
            HasInvalidHeaders = false
        };

        var decision = policy.Evaluate(ex, currentAttemptCount: attempt, maxAttempts: 5);

        Assert.True(decision.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), decision.Delay);
    }

    [Fact]
    public void Evaluate_HttpRequestExceptionTransient_CalculatesExponentialBackoff()
    {
        var policy = CreateDeterministicPolicy(baseDelay: TimeSpan.FromSeconds(10));
        var ex = new HttpRequestException("Connection refused");

        var d1 = policy.Evaluate(ex, currentAttemptCount: 1, maxAttempts: 5);
        Assert.True(d1.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(10), d1.Delay);

        var d2 = policy.Evaluate(ex, currentAttemptCount: 2, maxAttempts: 5);
        Assert.True(d2.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(20), d2.Delay);

        var d3 = policy.Evaluate(ex, currentAttemptCount: 3, maxAttempts: 5);
        Assert.True(d3.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(40), d3.Delay);
    }

    [Fact]
    public void Evaluate_ExponentialBackoff_CappedAtMaxDelay()
    {
        var policy = CreateDeterministicPolicy(baseDelay: TimeSpan.FromSeconds(10), maxDelay: TimeSpan.FromSeconds(50));
        var ex = new TimeoutException("Operation timed out");

        var d5 = policy.Evaluate(ex, currentAttemptCount: 5, maxAttempts: 10);
        // 10 * 2^4 = 160 > 50 -> capped at 50
        Assert.True(d5.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(50), d5.Delay);
    }

    [Theory]
    [InlineData("40001", true)]  // serialization failure
    [InlineData("40P01", true)]  // deadlock
    [InlineData("08006", true)]  // connection failure
    [InlineData("57P01", true)]  // admin shutdown
    [InlineData("23505", false)] // unique constraint violation
    [InlineData("42P01", false)] // undefined table
    public void Evaluate_PostgresException_DistinguishesTransientFromFatal(string sqlState, bool expectedRetry)
    {
        var policy = CreateDeterministicPolicy();
        var ex = new PostgresException("DB message", "Severity", "InvariantSeverity", sqlState);

        var decision = policy.Evaluate(ex, currentAttemptCount: 1, maxAttempts: 3);

        Assert.Equal(expectedRetry, decision.ShouldRetry);
    }
}
