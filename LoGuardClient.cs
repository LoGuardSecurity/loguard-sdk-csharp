using System.Text.Json;

namespace LoGuard.Sdk;

/// <summary>
/// Main LoGuard C# client. Idiomatic .NET style: an explicit constructor
/// (not a global singleton like the Python/Node SDKs) -- matches the DI
/// patterns .NET developers already expect.
/// </summary>
public class LoGuardClient
{
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _env;
    private readonly TimeSpan _timeout;
    private readonly int _retries;
    private readonly string? _defaultService;

    public LoGuardClient(string apiKey, string baseUrl = "https://loguard.org",
        string env = "production", int timeoutMs = 10000, int retries = 3, string? service = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new LoGuardAuthException("apiKey is required");
        _apiKey = apiKey.Trim();
        _baseUrl = baseUrl.TrimEnd('/');
        _env = env;
        _timeout = TimeSpan.FromMilliseconds(timeoutMs);
        _retries = retries;

        // Falls back to whatever OpenTelemetry already settled on
        // (OTEL_SERVICE_NAME) so that a deployment with N microservices
        // sets this once in its infra (Helm/docker-compose/ECS task def)
        // instead of once per service in code. Deliberately not falling
        // back to hostname -- in Docker/k8s that's usually a container or
        // pod ID with a random suffix that changes on every restart or
        // replica, which would break grouping "one service, many
        // instances" into anything meaningful.
        _defaultService = service
            ?? Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
            ?? Environment.GetEnvironmentVariable("SERVICE_NAME");
    }

    private string IngestUrl => $"{_baseUrl}/v1/ingest";

    /// <summary>Sends a single security event.</summary>
    public async Task<Dictionary<string, JsonElement>?> EventAsync(EventInput input, CancellationToken ct = default)
    {
        var ev = EventBuilder.Build(input, _env, _defaultService);
        var payload = new { events = new[] { ev } };
        return await Transport.SendSignedAsync(IngestUrl, payload, _apiKey, _timeout, _retries, ct);
    }
}
