using LoGuard.Sdk;
using LoGuard.Tests.TestSupport;
using Xunit;

namespace LoGuard.Tests;

public class AiErrorPrivacyTests
{
    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task ErrorResponseDoesNotExposeInputContent(int status)
    {
        using var server = new MockHttpServer {
            Handler = _ => new ServedResponse(status,
                "{\"detail\":{\"err\":\"usage_limit_exceeded\",\"plan\":\"SYNTHETIC_PRIVATE_MARKER\",\"input\":\"SYNTHETIC_PRIVATE_MARKER\"}}")
        };
        var client = new LoGuardClient("synthetic-key", baseUrl: server.BaseUrl,
            retries: 1, allowInsecureTransport: true);
        var error = await Assert.ThrowsAnyAsync<LoGuardException>(() => client.EventAsync(
            new EventInput("ai_llm_request", "203.0.113.10", "/ai/test", 200,
                Meta: new Dictionary<string, object> { ["prompt"] = "synthetic" })));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_MARKER", error.Message);
    }
}
