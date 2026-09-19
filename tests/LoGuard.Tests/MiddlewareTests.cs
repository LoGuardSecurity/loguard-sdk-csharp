using System.Net;
using LoGuard.Sdk;
using LoGuard.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LoGuard.Tests;

/// <summary>
/// Direct tests of the internal IpHelper.GetRealIp (requires
/// InternalsVisibleTo), using a bare DefaultHttpContext -- no real HTTP
/// server needed for these, same technique ASP.NET Core's own test suite
/// uses.
/// </summary>
public class IpHelperTests
{
    private static HttpContext ContextWith(string remoteIp, string? xForwardedFor = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (xForwardedFor is not null)
        {
            ctx.Request.Headers["X-Forwarded-For"] = xForwardedFor;
        }
        return ctx;
    }

    [Fact]
    public void UntrustedPeer_IgnoresXForwardedFor()
    {
        var ctx = ContextWith("203.0.113.9", xForwardedFor: "9.9.9.9");
        string ip = IpHelper.GetRealIp(ctx, trustedProxies: []);
        Assert.Equal("203.0.113.9", ip);
    }

    [Fact]
    public void TrustedPeer_UsesFirstXForwardedForEntry()
    {
        var ctx = ContextWith("10.0.0.1", xForwardedFor: "198.51.100.7, 10.0.0.1");
        string ip = IpHelper.GetRealIp(ctx, trustedProxies: ["10.0.0.1"]);
        Assert.Equal("198.51.100.7", ip);
    }

    [Fact]
    public void TrustedPeer_MalformedXForwardedFor_FallsBackToPeer()
    {
        var ctx = ContextWith("10.0.0.1", xForwardedFor: "not-an-ip");
        string ip = IpHelper.GetRealIp(ctx, trustedProxies: ["10.0.0.1"]);
        Assert.Equal("10.0.0.1", ip);
    }

    [Fact]
    public void NoForwardedHeaderAtAll_ReturnsPeerRegardlessOfTrustList()
    {
        var ctx = ContextWith("198.51.100.66");
        string ip = IpHelper.GetRealIp(ctx, trustedProxies: ["198.51.100.66"]);
        Assert.Equal("198.51.100.66", ip);
    }

    [Fact]
    public void SpoofAttemptFromUntrustedPeer_IsIgnored()
    {
        // Attacker connects directly and sets X-Forwarded-For to claim a
        // different IP -- must not be believed since 198.51.100.66 isn't
        // in the trusted proxy list.
        var ctx = ContextWith("198.51.100.66", xForwardedFor: "1.2.3.4");
        string ip = IpHelper.GetRealIp(ctx, trustedProxies: ["10.0.0.1"]);
        Assert.Equal("198.51.100.66", ip);
    }

    [Fact]
    public void NoRemoteIpAddress_ReturnsUnknown()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = null;
        string ip = IpHelper.GetRealIp(ctx, trustedProxies: []);
        Assert.Equal("unknown", ip);
    }
}

/// <summary>
/// End-to-end middleware tests: real ASP.NET Core middleware pipeline
/// piece (InvokeAsync), a real LoGuardClient pointed at a real local
/// server. The middleware fires its event with a discarded task
/// (`_ = _client.EventAsync(...)` in Middleware.cs) so these poll for the
/// request to land rather than awaiting anything directly -- see
/// TestSupport/Poll.cs for why.
/// </summary>
public class MiddlewareEndToEndTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task TrackedStatus_EventuallyFiresAnEvent()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);
        var middleware = new LoGuardMiddleware(
            next: ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; },
            client,
            new LoGuardMiddlewareOptions());

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.Request.Path = "/missing";

        await middleware.InvokeAsync(ctx);

        bool arrived = await Poll.UntilAsync(() => server.Requests.Count > 0, PollTimeout);
        Assert.True(arrived, "middleware's fire-and-forget event never reached the mock server within timeout");

        using var doc = System.Text.Json.JsonDocument.Parse(server.Requests[0].Body);
        var ev = doc.RootElement.GetProperty("events")[0];
        Assert.Equal("http_error", ev.GetProperty("type").GetString());
        Assert.Equal(404, ev.GetProperty("status_code").GetInt32());
    }

    [Fact]
    public async Task Status401_UsesLoginFailedEventType()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);
        var middleware = new LoGuardMiddleware(
            next: ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; },
            client,
            new LoGuardMiddlewareOptions());

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.Request.Path = "/login";

        await middleware.InvokeAsync(ctx);
        bool arrived = await Poll.UntilAsync(() => server.Requests.Count > 0, PollTimeout);
        Assert.True(arrived, "event never reached the mock server within timeout");

        using var doc = System.Text.Json.JsonDocument.Parse(server.Requests[0].Body);
        Assert.Equal("login_failed", doc.RootElement.GetProperty("events")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task UntrackedStatus_NeverFiresAnEvent()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);
        var middleware = new LoGuardMiddleware(
            next: ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; },
            client,
            new LoGuardMiddlewareOptions());

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");

        await middleware.InvokeAsync(ctx);

        // Give it a real chance to (wrongly) fire before asserting it didn't.
        await Task.Delay(300);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ForbiddenHeaders_AreNeverForwardedEvenWhenExplicitlyConfigured()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);
        var opts = new LoGuardMiddlewareOptions
        {
            // Authorization is forbidden (see ForbiddenTrackHeaders in
            // Middleware.cs) even though it's explicitly requested here.
            TrackHeaders = ["Authorization", "X-Custom-Debug"],
        };
        var middleware = new LoGuardMiddleware(
            next: ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; },
            client,
            opts);

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.Request.Path = "/protected";
        ctx.Request.Headers["Authorization"] = "Bearer super-secret-token";
        ctx.Request.Headers["X-Custom-Debug"] = "trace-id-123";

        await middleware.InvokeAsync(ctx);
        bool arrived = await Poll.UntilAsync(() => server.Requests.Count > 0, PollTimeout);
        Assert.True(arrived, "event never reached the mock server within timeout");

        string body = server.Requests[0].Body;
        Assert.DoesNotContain("super-secret-token", body);
        Assert.Contains("trace-id-123", body);
    }

    [Fact]
    public async Task NoTrackHeadersConfigured_MetaHasNoHeadersKeyAtAll()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);
        var middleware = new LoGuardMiddleware(
            next: ctx => { ctx.Response.StatusCode = 500; return Task.CompletedTask; },
            client,
            new LoGuardMiddlewareOptions()); // TrackHeaders defaults to []

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.Request.Path = "/x";
        ctx.Request.Headers["User-Agent"] = "curl/8.0";

        await middleware.InvokeAsync(ctx);
        bool arrived = await Poll.UntilAsync(() => server.Requests.Count > 0, PollTimeout);
        Assert.True(arrived, "event never reached the mock server within timeout");

        using var doc = System.Text.Json.JsonDocument.Parse(server.Requests[0].Body);
        var meta = doc.RootElement.GetProperty("events")[0].GetProperty("meta");
        Assert.False(meta.TryGetProperty("headers", out _),
            "no headers should be collected by default -- nothing beyond what's needed is sent unless explicitly opted in");
    }
}
