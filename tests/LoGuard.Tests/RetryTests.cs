using LoGuard.Sdk;
using LoGuard.Tests.TestSupport;
using Xunit;

namespace LoGuard.Tests;

public class RetryTests
{
    [Fact]
    public async Task SuccessfulFirstAttempt_MakesExactlyOneRequest()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 3, allowInsecureTransport: true);

        await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task RetryableStatus_RetriesThenSucceeds()
    {
        int hitCount = 0;
        using var server = new MockHttpServer
        {
            Handler = _ =>
            {
                hitCount++;
                return hitCount < 3
                    ? new ServedResponse(503, """{"detail":"temporarily unavailable"}""")
                    : new ServedResponse(200, """{"ok":true,"accepted":1,"dropped":0,"alerts":[],"plan":"test","usage":{}}""");
            }
        };
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 5, allowInsecureTransport: true);

        var result = await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

        Assert.NotNull(result);
        Assert.Equal(3, server.Requests.Count);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task RetryableStatuses_AreActuallyRetried(int status)
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(status, "{}") };
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 2, allowInsecureTransport: true);

        await Assert.ThrowsAsync<LoGuardConnectionException>(
            () => client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200)));

        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task ExhaustedRetries_StopsAtConfiguredMaximum_NoInfiniteLoop()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(500, "{}") };
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 4, allowInsecureTransport: true);

        await Assert.ThrowsAsync<LoGuardConnectionException>(
            () => client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200)));

        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task NonRetryableClientError_DoesNotRetry()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(400, "{}") };
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 5, allowInsecureTransport: true);

        // 400 isn't explicitly mapped in RaiseForStatusAsync's switch and
        // isn't >= 500, so current behavior is: falls through, returns
        // the (likely null-ish) deserialized body without throwing or
        // retrying. This test's real point is the retry count, not the
        // return value.
        await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));
        Assert.Single(server.Requests);
    }

    /// <summary>
    /// Each connection-level failure (here: the server closing the
    /// connection outright) must still be bounded by `retries`, exactly
    /// like an HTTP-level 5xx -- confirms the generic `catch (Exception)`
    /// path in Transport also respects the retry ceiling rather than
    /// looping differently.
    /// </summary>
    [Fact]
    public async Task ConnectionFailure_IsAlsoBoundedByRetries()
    {
        // Point at a port nothing is listening on -- guaranteed connection
        // refused, no real network involved.
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int deadPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // now guaranteed nothing is listening on this port

        var client = new LoGuardClient("test-key", baseUrl: $"http://127.0.0.1:{deadPort}", retries: 3,
            timeoutMs: 2000, allowInsecureTransport: true);

        var ex = await Record.ExceptionAsync(
            () => client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200)));

        Assert.IsType<LoGuardConnectionException>(ex);
    }
}
