#!/usr/bin/env bash
set -euo pipefail

SDK_DIR="$HOME/PythonProjects/final-bundle/loguard-csharp-sdk"
TESTS_DIR="$SDK_DIR/tests/LoGuard.Tests"

python3 - "$SDK_DIR" "$TESTS_DIR" << 'PYEOF'
import sys, pathlib

sdk_dir = pathlib.Path(sys.argv[1])
tests_dir = pathlib.Path(sys.argv[2])

p = sdk_dir / "Transport.cs"
text = p.read_text()

old_client = "            using var client = new HttpClient { Timeout = timeout };"
new_client = (
    "            using var handler = new HttpClientHandler { AllowAutoRedirect = false };\n"
    "            using var client = new HttpClient(handler) { Timeout = timeout };"
)
count = text.count(old_client)
assert count == 3, f"expected 3 occurrences of HttpClient construction, found {count}"
text = text.replace(old_client, new_client)
print(f"Fix 1 (no auto-redirect): {count} occurrences patched")

old_multiline = (
    "            catch (LoGuardException) { throw; }\n"
    "            catch (Exception e)\n"
    "            {\n"
    "                lastErr = new LoGuardConnectionException($\"connection error: {e.Message}\");\n"
    "            }"
)
new_multiline = (
    "            catch (LoGuardException) { throw; }\n"
    "            catch (OperationCanceledException) { throw; }\n"
    "            catch (Exception e)\n"
    "            {\n"
    "                lastErr = new LoGuardConnectionException($\"connection error: {e.Message}\");\n"
    "            }"
)
count_ml = text.count(old_multiline)
assert count_ml == 1, f"expected 1 multi-line catch block, found {count_ml}"
text = text.replace(old_multiline, new_multiline)

old_oneline = (
    "            catch (LoGuardException) { throw; }\n"
    "            catch (Exception e) { lastErr = new LoGuardConnectionException($\"connection error: {e.Message}\"); }"
)
new_oneline = (
    "            catch (LoGuardException) { throw; }\n"
    "            catch (OperationCanceledException) { throw; }\n"
    "            catch (Exception e) { lastErr = new LoGuardConnectionException($\"connection error: {e.Message}\"); }"
)
count_ol = text.count(old_oneline)
assert count_ol == 2, f"expected 2 single-line catch blocks, found {count_ol}"
text = text.replace(old_oneline, new_oneline)
print(f"Fix 2 (propagate cancellation): {count_ml + count_ol} catch blocks patched")

old_429 = '            case 429: throw new LoGuardConnectionException("Rate limited");'
new_429 = '''            case 429:
            {
                if (data is not null && data.TryGetValue("detail", out var detail429)
                    && detail429.ValueKind == JsonValueKind.Object
                    && detail429.TryGetProperty("err", out var errProp)
                    && errProp.GetString() == "usage_limit_exceeded")
                {
                    string used = detail429.TryGetProperty("used", out var usedProp) ? usedProp.ToString() : "?";
                    string limitVal = detail429.TryGetProperty("limit", out var limitProp) ? limitProp.ToString() : "?";
                    string plan429 = detail429.TryGetProperty("plan", out var planProp) && planProp.ValueKind == JsonValueKind.String
                        ? planProp.GetString()!
                        : "?";
                    throw new LoGuardQuotaException($"Monthly quota exceeded: {used}/{limitVal} events on plan {plan429}. Upgrade at loguard.org");
                }
                throw new LoGuardConnectionException("Rate limited");
            }'''
count_429 = text.count(old_429)
assert count_429 == 1, f"expected 1 occurrence of the 429 case, found {count_429}"
text = text.replace(old_429, new_429)
print("Fix 3 (429 -> LoGuardQuotaException when applicable): patched")

p.write_text(text)

p = tests_dir / "ErrorMappingTests.cs"
text = p.read_text()
old_test = '''    /// <summary>
    /// REGRESSION / CONTRACT GAP (documented, not silently "fixed"):
    /// every other LoGuard SDK maps 429 with detail.err ==
    /// "usage_limit_exceeded" to a quota-specific exception
    /// (LoGuardQuotaException exists in Errors.cs for exactly this), but
    /// the current Transport.RaiseForStatusAsync always throws
    /// LoGuardConnectionException("Rate limited") for 429, regardless of
    /// body content -- LoGuardQuotaException is never thrown anywhere in
    /// this SDK. This test pins down actual current behavior so it can't
    /// silently regress further; see the accompanying report for the
    /// recommended fix.
    /// </summary>
    [Fact]
    public async Task Status429_CurrentlyMapsToConnectionExceptionNotQuotaException()
    {
        using var server = new MockHttpServer
        {
            Handler = _ => new ServedResponse(429, """{"detail":{"err":"usage_limit_exceeded","used":1000,"limit":1000,"plan":"free"}}""")
        };
        var client = ClientFor(server, retries: 3);

        var ex = await Assert.ThrowsAsync<LoGuardConnectionException>(() => SendOneEvent(client));
        Assert.Equal("Rate limited", ex.Message);

        // Also confirms 429 is non-retryable in the current implementation
        // (it's not in Transport.RetryStatuses) -- exactly one attempt.
        Assert.Single(server.Requests);
    }'''
new_test = '''    /// <summary>
    /// FIXED (was: Status429_CurrentlyMapsToConnectionExceptionNotQuotaException).
    /// Transport.RaiseForStatusAsync now inspects detail.err on a 429 and
    /// throws LoGuardQuotaException when it's "usage_limit_exceeded",
    /// matching every other LoGuard SDK.
    /// </summary>
    [Fact]
    public async Task Status429_WithUsageLimitExceeded_MapsToQuotaException()
    {
        using var server = new MockHttpServer
        {
            Handler = _ => new ServedResponse(429, """{"detail":{"err":"usage_limit_exceeded","used":1000,"limit":1000,"plan":"free"}}""")
        };
        var client = ClientFor(server, retries: 3);

        var ex = await Assert.ThrowsAsync<LoGuardQuotaException>(() => SendOneEvent(client));
        Assert.Contains("quota", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Still non-retryable -- exactly one attempt.
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Status429_WithoutUsageLimitDetail_StillMapsToPlainRateLimitConnectionException()
    {
        using var server = new MockHttpServer { Handler = _ => new ServedResponse(429, "{}") };
        var client = ClientFor(server, retries: 3);

        var ex = await Assert.ThrowsAsync<LoGuardConnectionException>(() => SendOneEvent(client));
        Assert.Equal("Rate limited", ex.Message);
        Assert.Single(server.Requests);
    }'''
assert old_test in text, "ErrorMappingTests: 429 test block not found"
text = text.replace(old_test, new_test)
p.write_text(text)
print("Flipped: ErrorMappingTests 429 test (+ added a plain-rate-limit companion test)")

p = tests_dir / "TransportTests.cs"
text = p.read_text()
old_test = '''    /// <summary>
    /// SECURITY FINDING (documented, not silently patched): Transport
    /// constructs a plain <c>new HttpClient()</c> with no
    /// <c>HttpClientHandler { AllowAutoRedirect = false }</c>, so the
    /// .NET default (follow redirects automatically) applies. This test
    /// demonstrates that a 3xx from the configured host is currently
    /// followed to a second host, carrying the same signed, API-key-bearing
    /// request with it -- every other LoGuard SDK explicitly disables this.
    /// See the accompanying report for the one-line fix
    /// (<c>new HttpClientHandler { AllowAutoRedirect = false }</c> passed
    /// into each <c>new HttpClient(handler)</c> call in Transport.cs).
    /// </summary>
    [Fact]
    public async Task Event_CurrentlyFollowsRedirectsToADifferentHost_SecurityGap()
    {
        using var target = new MockHttpServer();
        using var redirector = new MockHttpServer
        {
            Handler = _ => new ServedResponse(302, "", RedirectLocation: $"{target.BaseUrl}/v1/ingest")
        };

        var client = new LoGuardClient("sensitive-key", baseUrl: redirector.BaseUrl, retries: 1);
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
new_test = '''    /// <summary>
    /// FIXED (was: Event_CurrentlyFollowsRedirectsToADifferentHost_SecurityGap).
    /// Transport now constructs HttpClient with
    /// HttpClientHandler { AllowAutoRedirect = false }, so a 3xx from the
    /// configured host is never followed to a second, unconfigured host.
    /// </summary>
    [Fact]
    public async Task Event_DoesNotFollowRedirectsToADifferentHost()
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
assert old_test in text, "TransportTests: redirect test block not found"
text = text.replace(old_test, new_test)
p.write_text(text)
print("Flipped: TransportTests redirect test")

p = tests_dir / "ConcurrencyCancellationTests.cs"
text = p.read_text()
old_test = '''    /// <summary>
    /// REGRESSION / DESIGN GAP (documented, not silently "fixed"):
    /// Transport's retry loop wraps every non-LoGuardException in
    /// `catch (Exception e) { lastErr = new LoGuardConnectionException(...); }`,
    /// which also catches `OperationCanceledException`/`TaskCanceledException`
    /// thrown by `HttpClient.SendAsync` when the token is already
    /// cancelled. With `retries: 1` (so the loop can't reach the
    /// `Task.Delay(..., ct)` line, which -- separately -- would throw a
    /// proper cancellation exception), the *observed* result of passing
    /// an already-cancelled token is a plain LoGuardConnectionException,
    /// not the OperationCanceledException a .NET caller would normally
    /// `catch` for cancellation. This is surprising and worth fixing, but
    /// this test exists to pin down what actually happens today.
    /// </summary>
    [Fact]
    public async Task PreCancelledToken_CurrentlySurfacesAsConnectionException_NotOperationCanceled()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(
            () => client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200), cts.Token));

        Assert.IsType<LoGuardConnectionException>(ex);
        Assert.IsNotType<OperationCanceledException>(ex);
    }'''
new_test = '''    /// <summary>
    /// FIXED (was: PreCancelledToken_CurrentlySurfacesAsConnectionException_NotOperationCanceled).
    /// Transport's retry loop now catches OperationCanceledException
    /// before the generic Exception handler and rethrows it immediately,
    /// so cancellation surfaces the way .NET callers expect.
    /// </summary>
    [Fact]
    public async Task PreCancelledToken_SurfacesAsOperationCanceled()
    {
        using var server = new MockHttpServer();
        var client = new LoGuardClient("test-key", baseUrl: server.BaseUrl, retries: 1, allowInsecureTransport: true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.EventAsync(new EventInput("http_request", "1.2.3.4", "/x", 200), cts.Token));
    }'''
assert old_test in text, "ConcurrencyCancellationTests: cancellation test block not found"
text = text.replace(old_test, new_test)
p.write_text(text)
print("Flipped: ConcurrencyCancellationTests cancellation test")

print("\nAll fixes and test flips applied.")
PYEOF
