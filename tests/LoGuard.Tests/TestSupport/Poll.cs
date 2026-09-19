namespace LoGuard.Tests.TestSupport;

/// <summary>
/// LoGuardMiddleware fires its event with <c>_ = _client.EventAsync(...)</c>
/// -- a genuinely discarded task (see Middleware.cs). There is no await to
/// hook into from a test. Rather than a flaky fixed <c>Thread.Sleep</c>,
/// poll for the observable effect (a request landing on the mock server)
/// with a generous bounded timeout -- deterministic pass/fail, no arbitrary
/// wait baked in for the happy path.
/// </summary>
public static class Poll
{
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }
}
