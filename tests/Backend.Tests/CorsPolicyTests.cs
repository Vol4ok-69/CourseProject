using Api.Common;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backend.Tests;

public sealed class CorsPolicyTests
{
    private sealed class TestResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _callbacks = [];

        public override void OnStarting(Func<object, Task> callback, object state)
        {
            _callbacks.Add((callback, state));
        }

        public async Task FireOnStartingAsync()
        {
            for (var i = _callbacks.Count - 1; i >= 0; i--)
            {
                await _callbacks[i].Callback(_callbacks[i].State);
            }
        }
    }

    private static (CorsMiddleware Middleware, ICorsPolicyProvider PolicyProvider, ICorsService CorsService) CreateCorsPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors(options =>
        {
            options.AddPolicy(ApiCorsPolicies.FrontendDevPolicy, policy =>
            {
                policy.WithOrigins(ApiCorsPolicies.AllowedOrigins)
                      .WithMethods(ApiCorsPolicies.AllowedMethods)
                      .WithHeaders(ApiCorsPolicies.AllowedHeaders);
            });
        });

        var provider = services.BuildServiceProvider();
        var corsService = provider.GetRequiredService<ICorsService>();
        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var middleware = new CorsMiddleware(
            next: context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            corsService: corsService,
            loggerFactory: loggerFactory,
            policyName: ApiCorsPolicies.FrontendDevPolicy);

        return (middleware, policyProvider, corsService);
    }

    private static (DefaultHttpContext Context, TestResponseFeature Feature) CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        var feature = new TestResponseFeature();
        context.Features.Set<IHttpResponseFeature>(feature);

        return (context, feature);
    }

    [Fact]
    public void CorsPolicy_ConfigurationConstants_AreStrictAndDoNotContainWildcardOrTauri()
    {
        Assert.Equal("AllowFrontendDev", ApiCorsPolicies.FrontendDevPolicy);

        Assert.Equal(2, ApiCorsPolicies.AllowedOrigins.Length);
        Assert.Contains("http://localhost:5173", ApiCorsPolicies.AllowedOrigins);
        Assert.Contains("http://localhost:3000", ApiCorsPolicies.AllowedOrigins);

        // Гарантия отсутствия wildcard и преждевременного tauri://localhost
        Assert.DoesNotContain("*", ApiCorsPolicies.AllowedOrigins);
        Assert.DoesNotContain("tauri://localhost", ApiCorsPolicies.AllowedOrigins);

        Assert.Contains("GET", ApiCorsPolicies.AllowedMethods);
        Assert.Contains("POST", ApiCorsPolicies.AllowedMethods);
        Assert.Contains("PUT", ApiCorsPolicies.AllowedMethods);
        Assert.Contains("DELETE", ApiCorsPolicies.AllowedMethods);
        Assert.Contains("OPTIONS", ApiCorsPolicies.AllowedMethods);

        Assert.Contains("Authorization", ApiCorsPolicies.AllowedHeaders);
        Assert.Contains("Content-Type", ApiCorsPolicies.AllowedHeaders);
        Assert.Contains("Accept", ApiCorsPolicies.AllowedHeaders);
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://localhost:3000")]
    public async Task PreflightOptions_TrustedOrigins_ReturnsSuccessWithExpectedCorsHeaders(string origin)
    {
        var (middleware, policyProvider, _) = CreateCorsPipeline();
        var (context, feature) = CreateHttpContext();

        context.Request.Method = "OPTIONS";
        context.Request.Headers["Origin"] = origin;
        context.Request.Headers["Access-Control-Request-Method"] = "POST";
        context.Request.Headers["Access-Control-Request-Headers"] = "Authorization,Content-Type";

        await middleware.Invoke(context, policyProvider);
        await feature.FireOnStartingAsync();

        Assert.True(context.Response.StatusCode == StatusCodes.Status200OK ||
                    context.Response.StatusCode == StatusCodes.Status204NoContent);

        Assert.Equal(origin, context.Response.Headers.AccessControlAllowOrigin.ToString());

        var allowMethods = context.Response.Headers.AccessControlAllowMethods.ToString();
        Assert.Contains("POST", allowMethods);
        Assert.Contains("GET", allowMethods);

        var allowHeaders = context.Response.Headers.AccessControlAllowHeaders.ToString();
        Assert.Contains("Authorization", allowHeaders);
        Assert.Contains("Content-Type", allowHeaders);
    }

    [Theory]
    [InlineData("http://localhost:5173", "GET")]
    [InlineData("http://localhost:5173", "POST")]
    [InlineData("http://localhost:3000", "GET")]
    [InlineData("http://localhost:3000", "POST")]
    public async Task ActualRequest_TrustedOrigins_ReturnsAccessControlAllowOrigin(string origin, string method)
    {
        var (middleware, policyProvider, _) = CreateCorsPipeline();
        var (context, feature) = CreateHttpContext();

        context.Request.Method = method;
        context.Request.Headers["Origin"] = origin;

        await middleware.Invoke(context, policyProvider);
        await feature.FireOnStartingAsync();

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(origin, context.Response.Headers.AccessControlAllowOrigin.ToString());
    }

    [Theory]
    [InlineData("http://malicious-site.com")]
    [InlineData("http://evil-hacker.org")]
    [InlineData("https://localhost:5173")] // разный протокол https vs http
    [InlineData("http://localhost:8080")]  // несогласованный порт
    [InlineData("tauri://localhost")]      // tauri отложен до клиента
    public async Task Request_UntrustedOrigins_DoesNotReceiveAccessControlAllowOrigin(string untrustedOrigin)
    {
        var (middleware, policyProvider, _) = CreateCorsPipeline();
        var (context, feature) = CreateHttpContext();

        context.Request.Method = "GET";
        context.Request.Headers["Origin"] = untrustedOrigin;

        await middleware.Invoke(context, policyProvider);
        await feature.FireOnStartingAsync();

        Assert.False(context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Request_WithAuthorizationBearerHeader_PreservesAuthorizationHeader()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors(options =>
        {
            options.AddPolicy(ApiCorsPolicies.FrontendDevPolicy, policy =>
            {
                policy.WithOrigins(ApiCorsPolicies.AllowedOrigins)
                      .WithMethods(ApiCorsPolicies.AllowedMethods)
                      .WithHeaders(ApiCorsPolicies.AllowedHeaders);
            });
        });

        var provider = services.BuildServiceProvider();
        var corsService = provider.GetRequiredService<ICorsService>();
        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();

        var (context, feature) = CreateHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers["Origin"] = "http://localhost:5173";
        context.Request.Headers["Authorization"] = "Bearer test_token_xyz";

        var executedNext = false;
        var customMiddleware = new CorsMiddleware(
            next: ctx =>
            {
                executedNext = true;
                Assert.Equal("Bearer test_token_xyz", ctx.Request.Headers.Authorization.ToString());
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            corsService: corsService,
            loggerFactory: NullLoggerFactory.Instance,
            policyName: ApiCorsPolicies.FrontendDevPolicy);

        await customMiddleware.Invoke(context, policyProvider);
        await feature.FireOnStartingAsync();

        Assert.True(executedNext);
        Assert.Equal("http://localhost:5173", context.Response.Headers.AccessControlAllowOrigin.ToString());
    }

    [Fact]
    public async Task CorsService_DirectPolicyEvaluation_AllowsTrustedOriginsAndRejectsOthers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors(options =>
        {
            options.AddPolicy(ApiCorsPolicies.FrontendDevPolicy, policy =>
            {
                policy.WithOrigins(ApiCorsPolicies.AllowedOrigins)
                      .WithMethods(ApiCorsPolicies.AllowedMethods)
                      .WithHeaders(ApiCorsPolicies.AllowedHeaders);
            });
        });

        var provider = services.BuildServiceProvider();
        var corsService = provider.GetRequiredService<ICorsService>();
        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();

        var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), ApiCorsPolicies.FrontendDevPolicy);
        Assert.NotNull(policy);

        // Проверка доверенного origin
        var trustedContext = new DefaultHttpContext();
        trustedContext.Request.Headers["Origin"] = "http://localhost:5173";
        var trustedResult = corsService.EvaluatePolicy(trustedContext, policy);
        Assert.True(trustedResult.IsOriginAllowed);
        Assert.Equal("http://localhost:5173", trustedResult.AllowedOrigin);

        // Проверка недоверенного origin
        var untrustedContext = new DefaultHttpContext();
        untrustedContext.Request.Headers["Origin"] = "http://malicious-site.com";
        var untrustedResult = corsService.EvaluatePolicy(untrustedContext, policy);
        Assert.False(untrustedResult.IsOriginAllowed);
    }
}
