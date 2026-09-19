using LoGuard.Sdk;
using LoGuard.Tests.TestSupport;
using Xunit;

namespace LoGuard.Tests;

public class ConcurrencyCancellationTests
{
    [Fact]
    public async Task ManyConcurrentCalls_EachGetsItsOwnIndependentRequest_NoCrossTalk()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);

        var tasks = Enumerable.Range(0, 25)
            .Select(i => client.EventAsync(new EventInput("http_request", $"1.2.3.{i}", "/x", 200)))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(25, server.Requests.Count);

        // Every request must have carried its own X-Request-ID and its
        // own IP -- a shared-mutable-state bug would show up as
        // duplicates here.
        var requestIds = server.Requests.Select(r => r.Headers["X-Request-ID"]).ToHashSet();
        Assert.Equal(25, requestIds.Count);

        var ips = server.Requests.Select(r =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.Body);
            return doc.RootElement.GetProperty("events")[0].GetProperty("ip").GetString();
        }).ToHashSet();
        Assert.Equal(25, ips.Count);
    }

    [Fact]
    public async Task SameClientInstance_CanBeUsedRepeatedlyAcrossCalls()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);

        for (int i = 0; i < 5; i++)
        {
            await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));
        }

        Assert.Equal(5, server.Requests.Count);
    }

    /// <summary>
    /// REGRESSION / DESIGN GAP (documented, not silently "fixed"):
    /// Transport's retry loop wraps every non-LoGuardException in
    /// `catch (Exception e) { lastErr = new LoGuardConnectionException(...); }`,
    /// which also catches `OperationCanceledException`/`TaskCanceledException`
    /// thrown by `HttpClient.SendAsync` when the token is already
    /// cancelled. With `retries: 1` (so the loop can't reach the
    /// `Task.Delay(..., ct)` line, which -- separately -- would throw a
    /// proper cancellation exception), the *observed* result of passing
    /// an already-cancelled token is a plain LoGuardConnectionException,
    /// not the OperationCanceledException a .NET caller would normally
    /// `catch` for cancellation. This is surprising and worth fixing, but
    /// this test exists to pin down what actually happens today.
    /// </summary>
    [Fact]
    public async Task PreCancelledToken_SurfacesAsOperationCanceled()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200), cts.Token));
    }
}
