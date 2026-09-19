using LoGuard.Sdk;
using LoGuard.Tests.TestSupport;
using Xunit;

namespace LoGuard.Tests;

/// <summary>
/// Verifies HTTP status -> exception mapping against the actual current
/// Transport.RaiseForStatusAsync, over a real local HTTP server -- not
/// assumptions about what the mapping "should" be.
/// </summary>
public class ErrorMappingTests
{
    private static LoGuardClient ClientFor(MockHttpServer server, int retries = 1) =>
        new("test-key", baseUrl: server.BaseUrl, retries: retries, allowInsecureTransport: true);

    private static Task<Dictionary<string, System.Text.Json.JsonElement>?> SendOneEvent(LoGuardClient client) =>
        client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

    [Fact]
    public async Task Status401_MapsToAuthException()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(401, """{"detail":"invalid api key"}""") };
        var client = ClientFor(server);
        await Assert.ThrowsAsync<LoGuardAuthException>(() => SendOneEvent(client));
    }

    [Fact]
    public async Task Status402_MapsToAuthException()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(402, "{}") };
        var client = ClientFor(server);
        await Assert.ThrowsAsync<LoGuardAuthException>(() => SendOneEvent(client));
    }

    [Fact]
    public async Task Status403_MapsToAuthException()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(403, """{"detail":"forbidden"}""") };
        var client = ClientFor(server);
        await Assert.ThrowsAsync<LoGuardAuthException>(() => SendOneEvent(client));
    }

    [Fact]
    public async Task Status404_MapsToNotFoundException()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(404, """{"detail":"not found"}""") };
        var client = ClientFor(server);
        await Assert.ThrowsAsync<LoGuardNotFoundException>(() => SendOneEvent(client));
    }

    [Fact]
    public async Task Status409_MapsToConflictException()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(409, """{"detail":"duplicate"}""") };
        var client = ClientFor(server);
        await Assert.ThrowsAsync<LoGuardConflictException>(() => SendOneEvent(client));
    }

    [Fact]
    public async Task Status422_MapsToValidationException()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(422, "{}") };
        var client = ClientFor(server);
        await Assert.ThrowsAsync<LoGuardValidationException>(() => SendOneEvent(client));
    }

    /// <summary>
    /// FIXED (was: Status429_CurrentlyMapsToConnectionExceptionNotQuotaException).
    /// Transport.RaiseForStatusAsync now inspects detail.err on a 429 and
    /// throws LoGuardQuotaException when it's "usage_limit_exceeded",
    /// matching every other LoGuard SDK.
    /// </summary>
    [Fact]
    public async Task Status429_WithUsageLimitExceeded_MapsToQuotaException()
    {
        using var server = new MockHttpServer
        {
            Handler = _ => new ServedResponse(429, """{"detail":{"err":"usage_limit_exceeded","used":1000,"limit":1000,"plan":"free"}}""")
        };
        var client = ClientFor(server, retries: 3);

        var ex = await Assert.ThrowsAsync<LoGuardQuotaException>(() => SendOneEvent(client));
        Assert.Contains("quota", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Still non-retryable -- exactly one attempt.
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Status429_WithoutUsageLimitDetail_StillMapsToPlainRateLimitConnectionException()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(429, "{}") };
        var client = ClientFor(server, retries: 3);

        var ex = await Assert.ThrowsAsync<LoGuardConnectionException>(() => SendOneEvent(client));
        Assert.Equal("Rate limited", ex.Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Status500Exhausted_MapsToConnectionException()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(500, """{"detail":"internal error"}""") };
        var client = ClientFor(server, retries: 2);

        await Assert.ThrowsAsync<LoGuardConnectionException>(() => SendOneEvent(client));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task MalformedJsonBodyWith200Status_DoesNotThrow_ReturnsNull()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(200, "{not valid json!!") };
        var client = ClientFor(server);

        var result = await SendOneEvent(client);
        Assert.Null(result);
    }
}
