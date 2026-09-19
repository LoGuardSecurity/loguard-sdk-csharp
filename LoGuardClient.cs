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

    /// <summary>Creates a new LoGuard client.</summary>
    /// <param name="apiKey">Your LoGuard API key. Required.</param>
    /// <param name="baseUrl">LoGuard API base URL. Must be https:// unless <paramref name="allowInsecureTransport"/> is true.</param>
    /// <param name="env">Environment name attached to every event, e.g. "production", "staging".</param>
    /// <param name="timeoutMs">Per-attempt request timeout, in milliseconds.</param>
    /// <param name="retries">Maximum attempts for a request before giving up.</param>
    /// <param name="service">Service name attached to every event; falls back to OTEL_SERVICE_NAME/SERVICE_NAME if omitted.</param>
    /// <param name="allowInsecureTransport">Allows a plain http:// <paramref name="baseUrl"/> for local development. Never use this against a real deployment.</param>
    /// <exception cref="LoGuardAuthException">Thrown when <paramref name="apiKey"/> is empty.</exception>
    /// <exception cref="LoGuardValidationException">Thrown when <paramref name="baseUrl"/> isn't https:// and <paramref name="allowInsecureTransport"/> is false.</exception>
    public LoGuardClient(string apiKey, string baseUrl = "https://loguard.org",
        string env = "production", int timeoutMs = 10000, int retries = 3, string? service = null,
        bool allowInsecureTransport = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new LoGuardAuthException("apiKey is required");
        _apiKey = apiKey.Trim();
        _baseUrl = baseUrl.TrimEnd('/');
        _env = env;
        _timeout = TimeSpan.FromMilliseconds(timeoutMs);
        _retries = retries;

        // Matches the Python/Node/Go SDKs: refuse plaintext http:// unless
        // the caller explicitly opts in, since apiKey and request
        // signatures would otherwise go out in the clear on every request.
        if (!_baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowInsecureTransport)
            {
                throw new LoGuardValidationException(
                    "baseUrl must use https:// -- your apiKey is sent on every request and " +
                    "must not travel over plaintext HTTP. If you really need HTTP (e.g. a " +
                    "local proxy on a trusted network during development), pass " +
                    "allowInsecureTransport: true explicitly.");
            }
            Console.Error.WriteLine(
                "LoGuard SDK initialized with allowInsecureTransport=true -- apiKey and " +
                "request signatures are being sent over plaintext. Use this ONLY for local " +
                "development on a trusted network, never against a real deployment.");
        }

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

    /// <summary>Sends a single security event, signed and retried per configuration.</summary>
    /// <param name="input">The event to send.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>The decoded response body, or null if the server returned no parseable JSON.</returns>
    /// <exception cref="LoGuardValidationException">Thrown when <paramref name="input"/> fails validation.</exception>
    /// <exception cref="LoGuardAuthException">Thrown when the API key is rejected.</exception>
    /// <exception cref="LoGuardQuotaException">Thrown when the monthly event quota has been exceeded.</exception>
    /// <exception cref="LoGuardConnectionException">Thrown when the server is unreachable or retries are exhausted.</exception>
    public async Task<Dictionary<string, JsonElement>?> EventAsync(EventInput input, CancellationToken ct = default)
    {
        var ev = EventBuilder.Build(input, _env, _defaultService);
        var payload = new { events = new[] { ev } };
        return await Transport.SendSignedAsync(IngestUrl, payload, _apiKey, _timeout, _retries, ct);
    }
}
