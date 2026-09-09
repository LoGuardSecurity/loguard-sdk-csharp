using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LoGuard.Sdk;

/// <summary>
/// Request signing for the LoGuard C# SDK. Same protocol as the
/// Python/Node/Go/Rust SDKs -- HMAC-SHA256 with a timestamp to guard
/// against replay attacks.
/// </summary>
internal static class Signing
{
    private const string SdkVersion = "0.1.0";

    /// <summary>
    /// Serializes the payload to JSON bytes. Key order doesn't need to
    /// match byte-for-byte with the other language SDKs -- what matters is
    /// internal consistency: whatever gets signed is exactly what gets sent.
    /// </summary>
    public static byte[] BuildBody<T>(T payload)
    {
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    /// <summary>Signs the request body and returns the headers to send.</summary>
    public static Dictionary<string, string> Sign(string apiKey, byte[] bodyBytes)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        byte[] signedPayload = new byte[prefix.Length + bodyBytes.Length];
        Buffer.BlockCopy(prefix, 0, signedPayload, 0, prefix.Length);
        Buffer.BlockCopy(bodyBytes, 0, signedPayload, prefix.Length, bodyBytes.Length);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        byte[] hash = hmac.ComputeHash(signedPayload);
        string signature = Convert.ToHexStringLower(hash);

        return new Dictionary<string, string>
        {
            ["X-Api-Key"] = apiKey,
            ["X-LoGuard-Timestamp"] = timestamp.ToString(),
            ["X-LoGuard-Signature"] = $"sha256={signature}",
            ["X-Request-ID"] = Guid.NewGuid().ToString(),
            ["User-Agent"] = $"loguard-csharp-sdk/{SdkVersion}",
        };
    }
}
