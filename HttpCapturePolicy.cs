using System.Text;
using System.Text.Json;

namespace LoGuard.Sdk;

internal sealed class HttpCapturePolicy
{
    private static readonly string[] Forbidden = [
        "password", "passwd", "pwd", "secret", "token", "api_key", "apikey",
        "authorization", "cookie", "session", "csrf", "xsrf", "private_key",
        "ssn", "card_number", "card_num", "cvv", "cvc", "credit_card", "pin", "otp"
    ];
    public int MaxBodyBytes { get; }
    public int MaxJsonDepth { get; }

    public HttpCapturePolicy(int maxBodyBytes, int maxJsonDepth)
    {
        if (maxBodyBytes is < 1 or > 1048576 || maxJsonDepth is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(maxBodyBytes), "Invalid HTTP capture limits");
        MaxBodyBytes = maxBodyBytes;
        MaxJsonDepth = maxJsonDepth;
    }

    private static bool IsForbidden(string name) => Forbidden.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    internal static string Truncate(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes) return value;
        int end = maxBytes;
        while (end > 0 && (bytes[end] & 0xc0) == 0x80) end--;
        return Encoding.UTF8.GetString(bytes, 0, end);
    }

    private static Dictionary<string, object> Pairs(string raw)
    {
        var fields = new Dictionary<string, object>();
        foreach (var pair in raw.Split('&', 65).Take(64))
        {
            var parts = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            if (!IsForbidden(key) && key.Length > 0)
                fields[Truncate(key, 4096)] = Truncate(parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "", 4096);
        }
        return fields;
    }

    public Dictionary<string, object> Query(string raw)
    {
        if (Encoding.UTF8.GetByteCount(raw) > 32768) return new() { ["query_too_large"] = true };
        var fields = Pairs(raw);
        return fields.Count == 0 ? new() : new() { ["query"] = fields };
    }

    internal static bool Supports(string? contentType)
    {
        string media = (contentType ?? "").Split(';', 2)[0].Trim().ToLowerInvariant();
        return media is "application/json" or "application/x-www-form-urlencoded" || (media.StartsWith("application/") && media.EndsWith("+json"));
    }

    public Dictionary<string, object> Body(byte[] raw, string? contentType)
    {
        if (!Supports(contentType)) return new() { ["body_unsupported_content_type"] = true };
        if (raw.Length > MaxBodyBytes) return new() { ["body_too_large"] = true };
        if (raw.Length == 0) return new();
        try
        {
            string text = new UTF8Encoding(false, true).GetString(raw);
            string trimmed = text.TrimStart();
            if ((contentType ?? "").Split(';', 2)[0].Trim().Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            {
                var form = Pairs(text);
                return form.Count == 0 ? new() : new() { ["body"] = form };
            }
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 64 });
            if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return new() { ["body_parse_error"] = true };
            if (TooDeep(doc.RootElement, MaxJsonDepth)) return new() { ["body_depth_exceeded"] = true };
            int budget = 64;
            var fields = Clean(doc.RootElement, ref budget)!;
            if (fields is System.Collections.ICollection { Count: 0 }) return new();
            return new() { ["body"] = fields };
        }
        catch (DecoderFallbackException) { return new() { ["body_parse_error"] = true }; }
        catch (JsonException e)
        {
            return new() { [e.Message.Contains("maximum configured depth", StringComparison.OrdinalIgnoreCase) ? "body_depth_exceeded" : "body_parse_error"] = true };
        }
    }

    private static bool TooDeep(JsonElement value, int depth)
    {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return false;
        if (depth == 0) return true;
        return value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().Any(p => TooDeep(p.Value, depth - 1))
            : value.EnumerateArray().Any(v => TooDeep(v, depth - 1));
    }

    private static object? Clean(JsonElement value, ref int budget)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var fields = new Dictionary<string, object?>();
                foreach (var property in value.EnumerateObject())
                {
                    if (budget-- <= 0) break;
                    if (!IsForbidden(property.Name)) fields[Truncate(property.Name, 4096)] = Clean(property.Value, ref budget);
                }
                return fields;
            case JsonValueKind.Array:
                var items = new List<object?>();
                foreach (var item in value.EnumerateArray())
                {
                    if (budget-- <= 0) break;
                    items.Add(Clean(item, ref budget));
                }
                return items;
            case JsonValueKind.String:
                return Truncate(value.GetString()!, 4096);
            default:
                return value.Clone();
        }
    }
}
