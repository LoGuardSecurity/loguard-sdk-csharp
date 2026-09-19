using System.Text;
using System.Text.Json;

namespace LoGuard.Sdk;

internal static class Transport
{
    private static readonly HashSet<int> RetryStatuses = [500, 502, 503, 504];

    public static Task<Dictionary<string, JsonElement>?> SendSignedAsync<T>(
        string url, T payload, string apiKey, TimeSpan timeout, int retries,
        CancellationToken ct = default)
        => ExecuteWithRetryAsync(() => BuildSignedRequest(HttpMethod.Post, url, payload, apiKey), timeout, retries, ct);

    public static Task<Dictionary<string, JsonElement>?> SendNoBodyAsync(
        string url, string apiKey, HttpMethod method, TimeSpan timeout, int retries, CancellationToken ct = default)
        => ExecuteWithRetryAsync(() => BuildPlainRequest(method, url, apiKey), timeout, retries, ct);

    public static Task<Dictionary<string, JsonElement>?> SendSignedMethodAsync<T>(
        string url, T payload, string apiKey, HttpMethod method, TimeSpan timeout, int retries, CancellationToken ct = default)
        => ExecuteWithRetryAsync(() => BuildSignedRequest(method, url, payload, apiKey), timeout, retries, ct);

    /// <summary>
    /// The one retry loop every Send*Async method above delegates to.
    /// <paramref name="buildRequest"/> is invoked fresh on every attempt
    /// (an <see cref="HttpRequestMessage"/> can only be sent once, and a
    /// signed request needs a fresh timestamp/signature per attempt
    /// anyway).
    /// </summary>
    private static async Task<Dictionary<string, JsonElement>?> ExecuteWithRetryAsync(
        Func<HttpRequestMessage> buildRequest, TimeSpan timeout, int retries, CancellationToken ct)
    {
        Exception lastErr = new LoGuardConnectionException("unknown error");

        for (int attempt = 1; attempt <= retries; attempt++)
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = timeout };
            try
            {
                using var req = buildRequest();
                using var resp = await client.SendAsync(req, ct);
                if (!RetryStatuses.Contains((int)resp.StatusCode))
                {
                    return await RaiseForStatusAsync(resp);
                }
                lastErr = new LoGuardConnectionException($"server error {(int)resp.StatusCode}");
            }
            catch (LoGuardException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                lastErr = new LoGuardConnectionException($"connection error: {e.Message}");
            }

            if (attempt < retries)
                await Task.Delay(400 * attempt, ct);
        }
        throw lastErr;
    }

    private static HttpRequestMessage BuildSignedRequest<T>(HttpMethod method, string url, T payload, string apiKey)
    {
        byte[] bodyBytes = Signing.BuildBody(payload);
        var headers = Signing.Sign(apiKey, bodyBytes);

        var content = new ByteArrayContent(bodyBytes);
        content.Headers.Add("Content-Type", "application/json");
        var req = new HttpRequestMessage(method, url) { Content = content };
        foreach (var (k, v) in headers)
        {
            if (k == "Content-Type") continue;
            req.Headers.TryAddWithoutValidation(k, v);
        }
        return req;
    }

    private static HttpRequestMessage BuildPlainRequest(HttpMethod method, string url, string apiKey)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Api-Key", apiKey);
        req.Headers.Add("User-Agent", "loguard-csharp-sdk/0.1.0");
        return req;
    }

    private static async Task<Dictionary<string, JsonElement>?> RaiseForStatusAsync(HttpResponseMessage resp)
    {
        int status = (int)resp.StatusCode;
        string text = await resp.Content.ReadAsStringAsync();
        Dictionary<string, JsonElement>? data = null;
        try { data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text); } catch { /* not json */ }

        switch (status)
        {
            case 401: throw new LoGuardAuthException("Invalid API key");
            case 402: throw new LoGuardAuthException("Subscription expired — renew at loguard.org");
            case 403: throw new LoGuardAuthException($"Access denied: {text}");
            case 404: throw new LoGuardNotFoundException($"Not found: {text}");
            case 409: throw new LoGuardConflictException($"Conflict: {text}");
            case 422: throw new LoGuardValidationException("Validation error");
            case 429:
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
            }
        }
        if (status >= 500) throw new LoGuardConnectionException($"Server error ({status})");
        return data;
    }
}
