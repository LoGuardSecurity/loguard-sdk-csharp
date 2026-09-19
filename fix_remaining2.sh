#!/usr/bin/env bash
set -euo pipefail

TESTS_DIR="$HOME/PythonProjects/final-bundle/loguard-csharp-sdk/tests/LoGuard.Tests"

python3 - "$TESTS_DIR" << 'PYEOF'
import sys, pathlib

tests_dir = pathlib.Path(sys.argv[1])

# --- TransportTests.cs ---
p = tests_dir / "TransportTests.cs"
text = p.read_text()
old_test = '''    public async Task Event_CurrentlyFollowsRedirectsToADifferentHost_SecurityGap()
    {
        using var target = new MockHttpServer();
        using var redirector = new MockHttpServer
        {
            Handler = _ => new ServedResponse(302, "", RedirectLocation: $"{target.BaseUrl}/v1/ingest")
        };

        var client = new LoGuardClient("sensitive-key", baseUrl: redirector.BaseUrl, retries: 1, allowInsecureTransport: true);
        await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

        // Current (undesired) behavior: the redirect is followed and a
        // request reaches the second server at all -- that's the core
        // finding (the SDK talks to a host it wasn't configured with).
        // If/when AllowAutoRedirect=false is added, this assertion should
        // flip to Assert.Empty(target.Requests).
        //
        // NOTE: .NET's default redirect handling rewrites POST->GET on a
        // 302 (matching RFC 7231's recommendation), so the request that
        // lands on `target` may arrive as GET with no body -- run this
        // once locally and check target.Requests[0].Method /
        // .Headers.ContainsKey("X-Api-Key") to see exactly what survives
        // the hop on your actual HttpClient version before relying on the
        // second assertion below; loosen it to just Assert.Single(...) if
        // the header doesn't carry over.
        Assert.Single(target.Requests);
        Assert.True(target.Requests[0].Headers.ContainsKey("X-Api-Key"),
            "if this fails, .NET stripped the header on the POST->GET redirect hop -- " +
            "still means the request reached an unintended host, just without the key riding along");
    }'''
new_test = '''    public async Task Event_DoesNotFollowRedirectsToADifferentHost()
    {
        using var target = new MockHttpServer();
        using var redirector = new MockHttpServer
        {
            Handler = _ => new ServedResponse(302, "", RedirectLocation: $"{target.BaseUrl}/v1/ingest")
        };

        var client = new LoGuardClient("sensitive-key", baseUrl: redirector.BaseUrl, retries: 1, allowInsecureTransport: true);

        // A bare 302 isn't in RetryStatuses and isn't mapped in
        // RaiseForStatusAsync's switch, so it falls through and returns
        // the (empty) decoded body without throwing -- the real assertion
        // is that the redirect was never followed to `target`.
        await client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200));

        Assert.Empty(target.Requests);
        Assert.Single(redirector.Requests);
    }'''
assert old_test in text, "TransportTests: pattern still not found -- paste the file content and I'll build an exact patch"
text = text.replace(old_test, new_test)

# Also fix the attribute/summary line above it and the method name reference
text = text.replace(
    "public async Task Event_DoesNotFollowRedirectsToADifferentHost()",
    "public async Task Event_DoesNotFollowRedirectsToADifferentHost()"
)  # no-op placeholder, name already fixed above

p.write_text(text)
print("Fixed: TransportTests.cs redirect test")

# --- ConcurrencyCancellationTests.cs ---
p = tests_dir / "ConcurrencyCancellationTests.cs"
text = p.read_text()
old_test = '''    public async Task PreCancelledToken_CurrentlySurfacesAsConnectionException_NotOperationCanceled()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(
            () => client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200), cts.Token));

        Assert.IsType<LoGuardConnectionException>(ex);
        Assert.IsNotType<OperationCanceledException>(ex);
    }'''
new_test = '''    public async Task PreCancelledToken_SurfacesAsOperationCanceled()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200), cts.Token));
    }'''
assert old_test in text, "ConcurrencyCancellationTests: pattern not found -- paste the file content and I'll build an exact patch"
text = text.replace(old_test, new_test)
p.write_text(text)
print("Fixed: ConcurrencyCancellationTests.cs cancellation test")

print("\nDone.")
PYEOF
