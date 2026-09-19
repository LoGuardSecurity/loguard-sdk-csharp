using System.Security.Cryptography;
using System.Text;
using LoGuard.Sdk;
using Xunit;

namespace LoGuard.Tests;

/// <summary>
/// Direct tests of the internal Signing class (requires InternalsVisibleTo
/// -- see LoGuard.Sdk.csproj). Verifies the actual HMAC-SHA256 scheme as
/// implemented today: sha256("{unixTimestamp}." + bodyBytes), keyed on the
/// raw API key.
/// </summary>
public class SigningTests
{
    [Fact]
    public void Sign_ProducesAllRequiredHeaders()
    {
        byte[] body = Signing.BuildBody(new { events = Array.Empty<object>() });
        var headers = Signing.Sign("test-key", body);

        Assert.True(headers.ContainsKey("X-Api-Key"));
        Assert.True(headers.ContainsKey("X-LoGuard-Timestamp"));
        Assert.True(headers.ContainsKey("X-LoGuard-Signature"));
        Assert.True(headers.ContainsKey("X-Request-ID"));
        Assert.True(headers.ContainsKey("User-Agent"));
        Assert.Equal("test-key", headers["X-Api-Key"]);
        Assert.StartsWith("loguard-csharp-sdk/", headers["User-Agent"]);
    }

    [Fact]
    public void Sign_SignatureMatchesHmacSha256OfTimestampDotBody()
    {
        byte[] body = Signing.BuildBody(new { a = 1, b = "x" });
        var headers = Signing.Sign("super-secret-key", body);

        string timestamp = headers["X-LoGuard-Timestamp"];
        byte[] prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        byte[] signedPayload = [.. prefix, .. body];

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("super-secret-key"));
        string expected = "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(signedPayload));

        Assert.Equal(expected, headers["X-LoGuard-Signature"]);
    }

    [Fact]
    public void Sign_WrongKeyProducesADifferentSignature()
    {
        byte[] body = Signing.BuildBody(new { a = 1 });
        var s1 = Signing.Sign("key-one", body);
        var s2 = Signing.Sign("key-two", body);

        Assert.NotEqual(s1["X-LoGuard-Signature"], s2["X-LoGuard-Signature"]);
    }

    [Fact]
    public void Sign_ModifiedBodyProducesADifferentSignature()
    {
        byte[] bodyA = Signing.BuildBody(new { a = 1 });
        byte[] bodyB = Signing.BuildBody(new { a = 2 });

        // Signed independently, so timestamps may legitimately differ by a
        // second under CI load -- compare the raw hash, not the header
        // dictionaries wholesale.
        var sA = Signing.Sign("key", bodyA);
        var sB = Signing.Sign("key", bodyB);

        Assert.NotEqual(sA["X-LoGuard-Signature"], sB["X-LoGuard-Signature"]);
    }

    [Fact]
    public void Sign_EachCallUsesAFreshRequestId()
    {
        byte[] body = Signing.BuildBody(new { a = 1 });
        var s1 = Signing.Sign("key", body);
        var s2 = Signing.Sign("key", body);

        Assert.NotEqual(s1["X-Request-ID"], s2["X-Request-ID"]);
        Assert.True(Guid.TryParse(s1["X-Request-ID"], out _));
    }

    [Fact]
    public void BuildBody_IsDeterministicForTheSamePayloadShape()
    {
        byte[] a = Signing.BuildBody(new { x = 1, y = "z" });
        byte[] b = Signing.BuildBody(new { x = 1, y = "z" });

        Assert.Equal(a, b);
    }

    [Fact]
    public void Sign_TimestampHeaderIsCloseToNow()
    {
        byte[] body = Signing.BuildBody(new { a = 1 });
        var headers = Signing.Sign("key", body);

        long ts = long.Parse(headers["X-LoGuard-Timestamp"]);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.InRange(ts, now - 5, now + 5);
    }
}
