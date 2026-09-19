namespace LoGuard.Sdk;

/// <summary>Input for a single security event sent to LoGuard.</summary>
/// <param name="Type">Event type, e.g. "login_failed", "http_error".</param>
/// <param name="Ip">Source IP address of the request.</param>
/// <param name="Path">Request path.</param>
/// <param name="StatusCode">HTTP status code, must be in the range 100-599.</param>
/// <param name="UserId">Authenticated user ID, if known.</param>
/// <param name="Service">Service name; falls back to OTEL_SERVICE_NAME/SERVICE_NAME if not set here or on the client.</param>
/// <param name="Meta">Arbitrary additional metadata to attach to the event.</param>
/// <param name="Ts">Event timestamp; defaults to now if omitted.</param>
public record EventInput(
    string Type,
    string Ip,
    string Path,
    int StatusCode,
    string? UserId = null,
    string? Service = null,
    Dictionary<string, object>? Meta = null,
    DateTimeOffset? Ts = null
);

internal record Event(
    [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type,
    [property: System.Text.Json.Serialization.JsonPropertyName("ip")] string Ip,
    [property: System.Text.Json.Serialization.JsonPropertyName("path")] string Path,
    [property: System.Text.Json.Serialization.JsonPropertyName("status_code")] int StatusCode,
    [property: System.Text.Json.Serialization.JsonPropertyName("user_id")] string? UserId,
    [property: System.Text.Json.Serialization.JsonPropertyName("service")] string? Service,
    [property: System.Text.Json.Serialization.JsonPropertyName("meta")] Dictionary<string, object> Meta,
    [property: System.Text.Json.Serialization.JsonPropertyName("ts")] string Ts
);

internal static class EventBuilder
{
    public static Event Build(EventInput input, string env, string? defaultService = null)
    {
        if (string.IsNullOrWhiteSpace(input.Type))
            throw new LoGuardValidationException("event type is required");
        if (string.IsNullOrWhiteSpace(input.Ip))
            throw new LoGuardValidationException("event ip is required");
        if (string.IsNullOrWhiteSpace(input.Path))
            throw new LoGuardValidationException("event path is required");
        if (input.StatusCode is < 100 or > 599)
            throw new LoGuardValidationException("statusCode must be in range 100..599");

        string p = input.Path.Trim();
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1024) p = p[..1024];

        var meta = input.Meta is not null ? new Dictionary<string, object>(input.Meta) : new Dictionary<string, object>();
        meta["env"] = env;

        string type = input.Type.Trim().ToLowerInvariant();
        if (type.Length > 64) type = type[..64];
        string ip = input.Ip.Trim();
        if (ip.Length > 64) ip = ip[..64];

        string? userId = input.UserId;
        if (userId is { Length: > 128 }) userId = userId[..128];

        // An explicitly passed service takes priority over the client-level
        // default (see LoGuardClient's constructor for the
        // OTEL_SERVICE_NAME/SERVICE_NAME auto-detection).
        string? service = input.Service ?? defaultService;
        if (!string.IsNullOrWhiteSpace(service))
        {
            service = service.Trim().ToLowerInvariant();
            if (service.Length > 64) service = service[..64];
        }
        else
        {
            service = null;
        }

        var ts = input.Ts ?? DateTimeOffset.UtcNow;

        return new Event(type, ip, p, input.StatusCode, userId, service, meta, ts.ToString("O"));
    }
}
