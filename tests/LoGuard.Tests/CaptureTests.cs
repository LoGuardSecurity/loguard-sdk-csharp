using System.Net;
using System.Text;
using System.Text.Json;
using LoGuard.Sdk;
using LoGuard.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LoGuard.Tests;

public class CaptureTests
{
    [Fact]
    public void NestedSecretsAreRemovedAndUtf8IsBounded()
    {
        var result = new HttpCapturePolicy(32768, 8).Body(Encoding.UTF8.GetBytes("""{"q":"test","password":"secret","nested":{"token":"secret"}}"""), "application/json");
        string json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("secret", json);
        Assert.Contains("test", json);
        Assert.Equal("é", HttpCapturePolicy.Truncate("éé", 3));
    }

    [Theory]
    [InlineData("{", "application/json", 32768, 8, "body_parse_error")]
    [InlineData("123456", "application/json", 4, 8, "body_too_large")]
    [InlineData("file", "multipart/form-data", 32768, 8, "body_unsupported_content_type")]
    [InlineData("{\"a\":{\"b\":{}}}", "application/json", 32768, 2, "body_depth_exceeded")]
    public void RejectionReasons(string raw, string media, int max, int depth, string reason)
        => Assert.Equal(true, new HttpCapturePolicy(max, depth).Body(Encoding.UTF8.GetBytes(raw), media)[reason]);

    [Fact]
    public void FormAndQuerySecretsAreRemoved()
    {
        var policy = new HttpCapturePolicy(32768, 8);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(policy.Query("q=test&token=secret")));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(policy.Body(Encoding.UTF8.GetBytes("q=test&password=secret"), "application/x-www-form-urlencoded")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpCapturePolicy(0, 8));
    }

    [Fact]
    public async Task MiddlewarePreservesBodyAndQueuesSanitizedSnapshot()
    {
        using var server = new MockHttpServer();
        await using var client = new LoGuardClient("test", baseUrl: server.BaseUrl, allowInsecureTransport: true, retries: 1);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        context.Request.Method = "POST";
        context.Request.Path = "/search";
        context.Request.QueryString = new QueryString("?q=test&token=secret");
        context.Request.ContentType = "application/json";
        byte[] bytes = Encoding.UTF8.GetBytes("""{"q":"test","password":"secret"}""");
        var original = new MemoryStream(bytes);
        context.Request.Body = original;
        context.Request.ContentLength = bytes.Length;
        var middleware = new LoGuardMiddleware(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            Assert.Contains("secret", await reader.ReadToEndAsync());
            ctx.Response.StatusCode = 200;
        }, client, new LoGuardMiddlewareOptions { SecurityCapture = true });
        await middleware.InvokeAsync(context);
        Assert.Same(original, context.Request.Body);
        await client.StopHttpDeliveryAsync();
        Assert.Single(server.Requests);
        Assert.DoesNotContain("secret", server.Requests[0].Body);
        Assert.Contains("http_request", server.Requests[0].Body);
    }

    [Fact]
    public async Task UnreadBodyIsNotConsumedAndExceptionIsPreserved()
    {
        using var server = new MockHttpServer();
        await using var client = new LoGuardClient("test", baseUrl: server.BaseUrl, allowInsecureTransport: true, retries: 1);
        var context = new DefaultHttpContext();
        context.Request.Path = "/search";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        var error = new InvalidOperationException("application failure");
        var middleware = new LoGuardMiddleware(ctx => throw error, client, new LoGuardMiddlewareOptions { SecurityCapture = true });
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context)));
        Assert.Equal(0, context.Request.Body.Position);
        await client.StopHttpDeliveryAsync();
        Assert.Contains("body_unavailable", server.Requests[0].Body);
        Assert.Contains("\"status_code\":500", server.Requests[0].Body);
    }

    [Fact]
    public void SpoofedPrefixIsIgnored()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.1, 192.0.2.1";
        Assert.Equal("192.0.2.1", IpHelper.GetRealIp(context, ["10.0.0.1"]));
    }

    [Fact]
    public void SharedConformanceVectors()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "http-capture-v1.json")));
        foreach (var vector in source.RootElement.GetProperty("cases").EnumerateArray())
        {
            var actual = new HttpCapturePolicy(32768, 8).Body(Encoding.UTF8.GetBytes(vector.GetProperty("body").GetString()!), vector.GetProperty("content_type").GetString());
            var expected = System.Text.Json.Nodes.JsonNode.Parse(vector.GetProperty("expected_meta").GetRawText());
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(actual))), vector.GetProperty("name").GetString());
        }
    }

    private sealed class AmbientProbe(AsyncLocal<string?> ambient)
    {
        public string? Value => ambient.Value;
    }

    [Fact]
    public async Task BackgroundWorkerDoesNotRetainRequestExecutionContext()
    {
        using var server = new MockHttpServer();
        await using var client = new LoGuardClient("test", baseUrl: server.BaseUrl, allowInsecureTransport: true, retries: 1);
        var ambient = new AsyncLocal<string?> { Value = "request-context-must-not-flow" };
        Assert.True(client.TryQueueHttp(new EventInput("http_request", "192.0.2.1", "/", 200,
            Meta: new Dictionary<string, object> { ["probe"] = new AmbientProbe(ambient) })));
        await client.StopHttpDeliveryAsync();
        Assert.Single(server.Requests);
        Assert.DoesNotContain("request-context-must-not-flow", server.Requests[0].Body);
    }
}
