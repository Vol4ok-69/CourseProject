using System.Net;
using Application.Exceptions;
using Application.Services;
using Xunit;

namespace Backend.Tests.Unit;

public sealed class GitHubRateLimitParserTests
{
    private static readonly DateTimeOffset FixedReceptionTime = new(2026, 11, 22, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TS01_DeltaSeconds_CalculatedFromReceptionTime()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.Add("Retry-After", "120");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.Equal(GitHubErrorKind.RateLimited, ex.Kind);
        Assert.Equal(GitHubRateLimitCategory.SecondaryAbuseTriggered, ex.RateLimitCategory);
        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfterDelay);
        Assert.False(ex.HasInvalidHeaders);
    }

    [Fact]
    public void TS02_HttpDate_ImfFixDate_CalculatedRelativeToDateHeader()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("Retry-After", "Sun, 22 Nov 2026 14:05:00 GMT");
        response.Headers.Add("Date", "Sun, 22 Nov 2026 14:00:00 GMT");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.Equal(GitHubRateLimitCategory.SecondaryAbuseTriggered, ex.RateLimitCategory);
        Assert.Equal(TimeSpan.FromMinutes(5), ex.RetryAfterDelay);
        Assert.False(ex.HasInvalidHeaders);
    }

    [Fact]
    public void TS03_HttpDate_Rfc850_CalculatedCorrectly()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("Retry-After", "Sunday, 22-Nov-26 14:05:00 GMT");
        response.Headers.Add("Date", "Sun, 22 Nov 2026 14:00:00 GMT");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.Equal(GitHubRateLimitCategory.SecondaryAbuseTriggered, ex.RateLimitCategory);
        Assert.Equal(TimeSpan.FromMinutes(5), ex.RetryAfterDelay);
        Assert.False(ex.HasInvalidHeaders);
    }

    [Fact]
    public void TS04_HttpDate_Asctime_CalculatedCorrectly()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("Retry-After", "Sun Nov 22 14:05:00 2026");
        response.Headers.Add("Date", "Sun, 22 Nov 2026 14:00:00 GMT");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.Equal(GitHubRateLimitCategory.SecondaryAbuseTriggered, ex.RateLimitCategory);
        Assert.Equal(TimeSpan.FromMinutes(5), ex.RetryAfterDelay);
        Assert.False(ex.HasInvalidHeaders);
    }

    [Fact]
    public void TS05_PrimaryRateLimit_RemainingZero_UsesResetEpoch()
    {
        var resetEpoch = FixedReceptionTime.AddMinutes(10).ToUnixTimeSeconds();
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", resetEpoch.ToString());
        response.Headers.Add("Date", "Sun, 22 Nov 2026 14:00:00 GMT");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.Equal(GitHubRateLimitCategory.PrimaryQuotaExceeded, ex.RateLimitCategory);
        Assert.Equal(TimeSpan.FromMinutes(10), ex.RetryAfterDelay);
        Assert.False(ex.HasInvalidHeaders);
    }

    [Fact]
    public void TS06_Collision_PrimaryAndSecondary_AppliesMax()
    {
        var resetEpoch = FixedReceptionTime.AddSeconds(900).ToUnixTimeSeconds();
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", resetEpoch.ToString());
        response.Headers.Add("Retry-After", "60");
        response.Headers.Add("Date", "Sun, 22 Nov 2026 14:00:00 GMT");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.Equal(GitHubRateLimitCategory.PrimaryQuotaExceeded, ex.RateLimitCategory);
        Assert.Equal(TimeSpan.FromSeconds(900), ex.RetryAfterDelay);
    }

    [Fact]
    public void TS07_SecondaryLimit_WhenQuotaNotExhausted_DoesNotApplyMax()
    {
        var resetEpoch = FixedReceptionTime.AddSeconds(1800).ToUnixTimeSeconds();
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "100");
        response.Headers.Add("X-RateLimit-Reset", resetEpoch.ToString());
        response.Headers.Add("Retry-After", "60");
        response.Headers.Add("Date", "Sun, 22 Nov 2026 14:00:00 GMT");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.Equal(GitHubRateLimitCategory.SecondaryAbuseTriggered, ex.RateLimitCategory);
        Assert.Equal(TimeSpan.FromSeconds(60), ex.RetryAfterDelay);
    }

    [Fact]
    public void TS08_SecondaryLimit_WhenRetryAfterAbsent_AppliesGitHubSixtySecondFallback()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)429);

        var exFirst = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime, 1);
        Assert.Equal(TimeSpan.FromSeconds(60), exFirst.RetryAfterDelay);

        var exSecond = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime, 2);
        Assert.Equal(TimeSpan.FromSeconds(120), exSecond.RetryAfterDelay);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TS09_RetryAfterEmpty_MarksInvalidHeaders_AndNullDelay(string emptyValue)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.TryAddWithoutValidation("Retry-After", emptyValue);

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.True(ex.HasInvalidHeaders);
        Assert.Null(ex.RetryAfterDelay);
    }

    [Fact]
    public void TS10_RetryAfterCorrupted_MarksInvalidHeaders_AndNullDelay()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.TryAddWithoutValidation("Retry-After", "corrupted_text_value");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.True(ex.HasInvalidHeaders);
        Assert.Null(ex.RetryAfterDelay);
    }

    [Fact]
    public void TS11_NegativeRetryAfter_MarksInvalidHeaders_AndNullDelay()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.TryAddWithoutValidation("Retry-After", "-30");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.True(ex.HasInvalidHeaders);
        Assert.Null(ex.RetryAfterDelay);
    }

    [Fact]
    public void TS12_HttpDateInPast_YieldsZeroDelay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("Retry-After", "Sun, 22 Nov 2026 13:50:00 GMT");
        response.Headers.Add("Date", "Sun, 22 Nov 2026 14:00:00 GMT");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.Equal(TimeSpan.Zero, ex.RetryAfterDelay);
        Assert.False(ex.HasInvalidHeaders);
    }

    [Fact]
    public void TS13_ResetEpochOverflow_MarksInvalidHeaders()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", "999999999999999999");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.True(ex.HasInvalidHeaders);
        Assert.Null(ex.RetryAfterDelay);
    }

    [Fact]
    public void Security_HeaderDiagnostics_PreservesAllowList_AndExcludesSensitiveHeaders()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.Add("Retry-After", "60");
        response.Headers.Add("X-RateLimit-Limit", "5000");
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("Date", "Sun, 22 Nov 2026 14:00:00 GMT");
        response.Headers.Add("Server", "GitHub.com");
        response.Headers.Add("Set-Cookie", "secret_session=12345");

        var ex = GitHubRateLimitParser.ParseRateLimitException(response, string.Empty, FixedReceptionTime);

        Assert.NotNull(ex.HeaderDiagnostics);
        Assert.True(ex.HeaderDiagnostics.ContainsKey("Retry-After"));
        Assert.True(ex.HeaderDiagnostics.ContainsKey("X-RateLimit-Remaining"));
        Assert.True(ex.HeaderDiagnostics.ContainsKey("Date"));
        Assert.False(ex.HeaderDiagnostics.ContainsKey("Set-Cookie"));
        Assert.False(ex.HeaderDiagnostics.ContainsKey("Server"));
        Assert.False(ex.HeaderDiagnostics.ContainsKey("Authorization"));
    }
}
