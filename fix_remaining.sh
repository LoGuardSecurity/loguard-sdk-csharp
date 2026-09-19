#!/usr/bin/env bash
set -euo pipefail

TESTS_DIR="$HOME/PythonProjects/final-bundle/loguard-csharp-sdk/tests/LoGuard.Tests"

python3 - "$TESTS_DIR" << 'PYEOF'
import sys, pathlib

tests_dir = pathlib.Path(sys.argv[1])

# ── 1. Revert the auto-fix on the one test that must NOT have it ──────────
p = tests_dir / "ClientConfigurationTests.cs"
text = p.read_text()
old = '''    public void Constructor_RejectsPlainHttpByDefault()
    {
        Assert.Throws<LoGuardValidationException>(() =>
            new LoGuardClient("lg_live_x", baseUrl: "http://loguard.internal", allowInsecureTransport: true));
    }'''
new = '''    public void Constructor_RejectsPlainHttpByDefault()
    {
        Assert.Throws<LoGuardValidationException>(() =>
            new LoGuardClient("lg_live_x", baseUrl: "http://loguard.internal"));
    }'''
assert old in text, "ClientConfigurationTests: pattern not found, inspect manually"
p.write_text(text.replace(old, new))
print("Fixed: Constructor_RejectsPlainHttpByDefault (reverted)")

# ── 2. Fix the target-typed `new(...)` in ErrorMappingTests.ClientFor ─────
p = tests_dir / "ErrorMappingTests.cs"
text = p.read_text()
old = '''    private static LoGuardClient ClientFor(MockHttpServer server, int retries = 1) =>
        new("test-key", baseUrl: server.BaseUrl, retries: retries);'''
new = '''    private static LoGuardClient ClientFor(MockHttpServer server, int retries = 1) =>
        new("test-key", baseUrl: server.BaseUrl, retries: retries, allowInsecureTransport: true);'''
assert old in text, "ErrorMappingTests: pattern not found, inspect manually"
p.write_text(text.replace(old, new))
print("Fixed: ErrorMappingTests.ClientFor")

# ── 3. Add missing ctx.Request.Path in three MiddlewareTests, and assert
#       on the poll result instead of indexing blindly ──────────────────
p = tests_dir / "MiddlewareTests.cs"
text = p.read_text()

replacements = [
    (
        '''        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");

        await middleware.InvokeAsync(ctx);
        await Poll.UntilAsync(() => server.Requests.Count > 0, PollTimeout);

        using var doc = System.Text.Json.JsonDocument.Parse(server.Requests[0].Body);
        Assert.Equal("login_failed", doc.RootElement.GetProperty("events")[0].GetProperty("type").GetString());''',
        '''        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.Request.Path = "/login";

        await middleware.InvokeAsync(ctx);
        bool arrived = await Poll.UntilAsync(() => server.Requests.Count > 0, PollTimeout);
        Assert.True(arrived, "event never reached the mock server within timeout");

        using var doc = System.Text.Json.JsonDocument.Parse(server.Requests[0].Body);
        Assert.Equal("login_failed", doc.RootElement.GetProperty("events")[0].GetProperty("type").GetString());'''
    ),
    (
        '''        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.Request.Headers["Authorization"] = "Bearer super-secret-token";
        ctx.Request.Headers["X-Custom-Debug"] = "trace-id-123";

        await middleware.InvokeAsync(ctx);
        await Poll.UntilAsync(() => server.Requests.Count > 0, PollTimeout);

        string body = server.Requests[0].Body;''',
        '''        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.Request.Path = "/protected";
        ctx.Request.Headers["Authorization"] = "Bearer super-secret-token";
        ctx.Request.Headers["X-Custom-Debug"] = "trace-id-123";

        await middleware.InvokeAsync(ctx);
        bool arrived = await Poll.UntilAsync(() => server.Requests.Count > 0, PollTimeout);
        Assert.True(arrived, "event never reached the mock server within timeout");

        string body = server.Requests[0].Body;'''
    ),
    (
        '''        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.Request.Headers["User-Agent"] = "curl/8.0";

        await middleware.InvokeAsync(ctx);
        await Poll.UntilAsync(() => server.Requests.Count > 0, PollTimeout);

        using var doc = System.Text.Json.JsonDocument.Parse(server.Requests[0].Body);
        var meta = doc.RootElement.GetProperty("events")[0].GetProperty("meta");''',
        '''        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.Request.Path = "/x";
        ctx.Request.Headers["User-Agent"] = "curl/8.0";

        await middleware.InvokeAsync(ctx);
        bool arrived = await Poll.UntilAsync(() => server.Requests.Count > 0, PollTimeout);
        Assert.True(arrived, "event never reached the mock server within timeout");

        using var doc = System.Text.Json.JsonDocument.Parse(server.Requests[0].Body);
        var meta = doc.RootElement.GetProperty("events")[0].GetProperty("meta");'''
    ),
]

count = 0
for old, new in replacements:
    assert old in text, f"MiddlewareTests: a pattern was not found (block starting: {old[:60]!r}...)"
    text = text.replace(old, new)
    count += 1

p.write_text(text)
print(f"Fixed: {count} MiddlewareTests blocks (added ctx.Request.Path + poll assertion)")
PYEOF
