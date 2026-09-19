using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoGuard.Sdk;
using LoGuard.Tests.TestSupport;
using Xunit;

namespace LoGuard.Tests;

/// <summary>
/// Verifies the actual wire request LoGuardClient sends, end-to-end
/// through the real HttpClient inside Transport -- not just what the
/// internal Signing class computes in isolation.
/// </summary>
public class TransportTests
{
    [Fact]
    public async Task Event_SendsPostToV1IngestPath()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);

        await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

        var req = Assert.Single(server.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/v1/ingest", req.Path);
    }

    [Fact]
    public async Task Event_TrailingSlashOnBaseUrlDoesNotProduceDoubleSlash()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl + "/", retries: 1, allowInsecureTransport: true);

        await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

        Assert.Equal("/v1/ingest", server.Requests[0].Path);
    }

    [Fact]
    public async Task Event_BodyContainsExactlyOneWellFormedEvent()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);

        await client.EventAsync(new EventInput("login_failed", "1.2.3.4", "/login", 401, UserId: "user_123"));

        using var doc = JsonDocument.Parse(server.Requests[0].Body);
        var events = doc.RootElement.GetProperty("events");
        Assert.Equal(1, events.GetArrayLength());
        var ev = events[0];
        Assert.Equal("login_failed", ev.GetProperty("type").GetString());
        Assert.Equal("1.2.3.4", ev.GetProperty("ip").GetString());
        Assert.Equal("/login", ev.GetProperty("path").GetString());
        Assert.Equal(401, ev.GetProperty("status_code").GetInt32());
        Assert.Equal("user_123", ev.GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task Event_SignatureOnTheWireVerifiesAgainstTheCapturedTimestampAndBody()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("verify-me-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);

        await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

        var req = server.Requests[0];
        string timestamp = req.Headers["X-LoGuard-Timestamp"];
        string bodyOnWire = req.Body;

        byte[] prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        byte[] bodyBytes = Encoding.UTF8.GetBytes(bodyOnWire);
        byte[] signedPayload = [.. prefix, .. bodyBytes];

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("verify-me-key"));
        string expected = "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(signedPayload));

        Assert.Equal(expected, req.Headers["X-LoGuard-Signature"]);
    }

    [Fact]
    public async Task Event_ApiKeyIsNeverSentInTheRequestBody()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("must-not-leak-into-body", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);

        await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

        Assert.DoesNotContain("must-not-leak-into-body", server.Requests[0].Body);
        Assert.Equal("must-not-leak-into-body", server.Requests[0].Headers["X-Api-Key"]);
    }

    /// <summary>
    /// SECURITY FINDING (documented, not silently patched): Transport
    /// constructs a plain <c>new HttpClient()</c> with no
    /// <c>HttpClientHandler { AllowAutoRedirect = false }</c>, so the
    /// .NET default (follow redirects automatically) applies. This test
    /// demonstrates that a 3xx from the configured host is currently
    /// followed to a second host, carrying the same signed, API-key-bearing
    /// request with it -- every other LoGuard SDK explicitly disables this.
    /// See the accompanying report for the one-line fix
    /// (<c>new HttpClientHandler { AllowAutoRedirect = false }</c> passed
    /// into each <c>new HttpClient(handler)</c> call in Transport.cs).
    /// </summary>
    [Fact]
    public async Task Event_DoesNotFollowRedirectsToADifferentHost()
    {
        using var target = new MockHttpServer();
        using var redirector = new MockHttpServer
        {
            Handler = _ => new ServedResponse(302, "", RedirectLocation: $"{target.BaseUrl}/v1/ingest")
        };

        var client = new LoGuardClient("sensitive-key", baseUrl: redirector.BaseUrl, retries: 1, allowInsecureTransport: true);

        // A bare 302 isn't in RetryStatuses and isn't mapped in
        // RaiseForStatusAsync's switch, so it falls through and returns
        // the (empty) decoded body without throwing -- the real assertion
        // is that the redirect was never followed to `target`.
        await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

        Assert.Empty(target.Requests);
        Assert.Single(redirector.Requests);
    }
}
