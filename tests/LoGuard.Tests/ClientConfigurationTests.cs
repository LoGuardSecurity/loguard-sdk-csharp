using LoGuard.Sdk;
using LoGuard.Tests.TestSupport;
using Xunit;

namespace LoGuard.Tests;

public class ClientConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsMissingApiKey(string? apiKey)
    {
        Assert.Throws<LoGuardAuthException>(() => new LoGuardClient(apiKey!, allowInsecureTransport: true));
    }

    [Fact]
    public void Constructor_RejectsPlainHttpByDefault()
    {
        Assert.Throws<LoGuardValidationException>(() =>
            new LoGuardClient("lg_live_x", baseUrl: "http://loguard.internal"));
    }

    [Fact]
    public void Constructor_AllowsPlainHttpWhenExplicitlyOptedIn()
    {
        // Should not throw -- construction alone makes no network call.
        var client = new LoGuardClient("lg_live_x", baseUrl: "http://loguard.internal", allowInsecureTransport: true);
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_AcceptsHttpsBaseUrl()
    {
        var client = new LoGuardClient("lg_live_x", baseUrl: "https://loguard.org", allowInsecureTransport: true);
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_DoesNotThrowWithOnlyApiKeyProvided()
    {
        // Defaults (https://loguard.org, production, 10s timeout, 3
        // retries) should all be valid on their own.
        var client = new LoGuardClient("lg_live_x", allowInsecureTransport: true);
        Assert.NotNull(client);
    }
}

/// <summary>
/// Environment-variable-dependent tests (OTEL_SERVICE_NAME /
/// SERVICE_NAME auto-detection, see LoGuardClient's constructor comment).
/// Isolated into its own non-parallel collection because they mutate
/// process-wide environment state -- running them alongside other tests
/// that also touch these variables would be a real source of flakiness,
/// not a hypothetical one.
/// </summary>
[Collection("EnvironmentVariables")]
public class ServiceAutoDetectionTests : IDisposable
{
    private readonly string? _savedOtel = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
    private readonly string? _savedService = Environment.GetEnvironmentVariable("SERVICE_NAME");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", _savedOtel);
        Environment.SetEnvironmentVariable("SERVICE_NAME", _savedService);
    }

    [Fact]
    public async Task ExplicitServiceParameterWinsOverEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "from-env");
        Environment.SetEnvironmentVariable("SERVICE_NAME", null);

        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, service: "explicit-service", retries: 1, allowInsecureTransport: true);

        await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

        var body = server.Requests[0].Body;
        Assert.Contains("\"service\":\"explicit-service\"", body);
    }

    [Fact]
    public async Task FallsBackToOtelServiceNameWhenServiceNotSpecified()
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "otel-service");
        Environment.SetEnvironmentVariable("SERVICE_NAME", null);

        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);

        await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

        var body = server.Requests[0].Body;
        Assert.Contains("\"service\":\"otel-service\"", body);
    }
}

[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public class EnvironmentVariablesCollection;
