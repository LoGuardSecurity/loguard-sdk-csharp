using LoGuard.Sdk;
using Xunit;

namespace LoGuard.Tests;

/// <summary>
/// Tests the internal EventBuilder.Build directly (requires
/// InternalsVisibleTo) -- fast, precise, and doesn't need a mock server
/// just to check that a status code out of range gets rejected.
/// </summary>
public class EventValidationTests
{
    private static EventInput Valid(string? type = "login_failed", string? ip = "1.2.3.4",
        string? path = "/login", int statusCode = 401) => new(type!, ip!, path!, statusCode);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_RejectsMissingType(string? type)
    {
        Assert.Throws<LoGuardValidationException>(() => EventBuilder.Build(Valid(type: type), "production"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_RejectsMissingIp(string? ip)
    {
        Assert.Throws<LoGuardValidationException>(() => EventBuilder.Build(Valid(ip: ip), "production"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_RejectsMissingPath(string? path)
    {
        Assert.Throws<LoGuardValidationException>(() => EventBuilder.Build(Valid(path: path), "production"));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(600)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10000)]
    public void Build_RejectsStatusCodeOutOfRange(int statusCode)
    {
        Assert.Throws<LoGuardValidationException>(() => EventBuilder.Build(Valid(statusCode: statusCode), "production"));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(599)]
    [InlineData(200)]
    [InlineData(404)]
    public void Build_AcceptsStatusCodeAtOrWithinBoundaries(int statusCode)
    {
        var ev = EventBuilder.Build(Valid(statusCode: statusCode), "production");
        Assert.Equal(statusCode, ev.StatusCode);
    }

    [Fact]
    public void Build_AddsLeadingSlashToPath()
    {
        var ev = EventBuilder.Build(Valid(path: "no-leading-slash"), "production");
        Assert.Equal("/no-leading-slash", ev.Path);
    }

    [Fact]
    public void Build_TruncatesPathTo1024Characters()
    {
        string longPath = "/" + new string('a', 2000);
        var ev = EventBuilder.Build(Valid(path: longPath), "production");
        Assert.Equal(1024, ev.Path.Length);
    }

    [Fact]
    public void Build_LowercasesAndTruncatesType()
    {
        string longType = string.Concat(Enumerable.Repeat("AB", 100));
        var ev = EventBuilder.Build(Valid(type: longType), "production");
        Assert.Equal(64, ev.Type.Length);
        Assert.Equal(ev.Type.ToLowerInvariant(), ev.Type);
    }

    [Fact]
    public void Build_TruncatesIpTo64Characters()
    {
        string longIp = new string('9', 200);
        var ev = EventBuilder.Build(Valid(ip: longIp), "production");
        Assert.Equal(64, ev.Ip.Length);
    }

    [Fact]
    public void Build_TruncatesUserIdTo128Characters()
    {
        var input = Valid() with { UserId = new string('u', 500) };
        var ev = EventBuilder.Build(input, "production");
        Assert.Equal(128, ev.UserId!.Length);
    }

    [Fact]
    public void Build_AlwaysInjectsEnvIntoMeta()
    {
        var ev = EventBuilder.Build(Valid(), "staging");
        Assert.Equal("staging", ev.Meta["env"]);
    }

    [Fact]
    public void Build_PreservesCallerSuppliedMetaAlongsideEnv()
    {
        var input = Valid() with { Meta = new Dictionary<string, object> { ["custom"] = "value" } };
        var ev = EventBuilder.Build(input, "production");
        Assert.Equal("value", ev.Meta["custom"]);
        Assert.Equal("production", ev.Meta["env"]);
    }

    [Fact]
    public void Build_ExplicitServiceOverridesClientDefault()
    {
        var input = Valid() with { Service = "checkout-api" };
        var ev = EventBuilder.Build(input, "production", defaultService: "fallback-service");
        Assert.Equal("checkout-api", ev.Service);
    }

    [Fact]
    public void Build_FallsBackToClientDefaultServiceWhenNotSpecified()
    {
        var ev = EventBuilder.Build(Valid(), "production", defaultService: "fallback-service");
        Assert.Equal("fallback-service", ev.Service);
    }

    [Fact]
    public void Build_ServiceIsNullWhenNeitherExplicitNorDefaultIsGiven()
    {
        var ev = EventBuilder.Build(Valid(), "production", defaultService: null);
        Assert.Null(ev.Service);
    }

    [Fact]
    public void Build_ServiceIsLowercased()
    {
        var input = Valid() with { Service = "Checkout-API" };
        var ev = EventBuilder.Build(input, "production");
        Assert.Equal("checkout-api", ev.Service);
    }

    [Fact]
    public void Build_UsesProvidedTimestampWhenGiven()
    {
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var input = Valid() with { Ts = ts };
        var ev = EventBuilder.Build(input, "production");
        Assert.Equal(ts.ToString("O"), ev.Ts);
    }

    [Fact]
    public void Build_DefaultsTimestampToNowWhenNotGiven()
    {
        var before = DateTimeOffset.UtcNow;
        var ev = EventBuilder.Build(Valid(), "production");
        var after = DateTimeOffset.UtcNow;

        var parsed = DateTimeOffset.Parse(ev.Ts);
        Assert.InRange(parsed, before.AddSeconds(-1), after.AddSeconds(1));
    }
}
