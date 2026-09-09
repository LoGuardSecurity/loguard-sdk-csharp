using System.Text;
using System.Text.Json;

namespace LoGuard.Sdk;

internal static class Transport
{
    private static readonly HashSet<int> RetryStatuses = [500, 502, 503, 504];

    public static async Task<Dictionary<string, JsonElement>?> SendSignedAsync<T>(
        string url, T payload, string apiKey, TimeSpan timeout, int retries,
        CancellationToken ct = default)
    {
        Exception lastErr = new LoGuardConnectionException("unknown error");

        for (int attempt = 1; attempt <= retries; attempt++)
        {
            using var client = new HttpClient { Timeout = timeout };
            try
            {
                byte[] bodyBytes = Signing.BuildBody(payload);
                var headers = Signing.Sign(apiKey, bodyBytes);

                using var content = new ByteArrayContent(bodyBytes);
                content.Headers.Add("Content-Type", "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                foreach (var (k, v) in headers)
                {
                    if (k == "Content-Type") continue;
                    req.Headers.TryAddWithoutValidation(k, v);
                }

                using var resp = await client.SendAsync(req, ct);
                if (!RetryStatuses.Contains((int)resp.StatusCode))
                {
                    return await RaiseForStatusAsync(resp);
                }
                lastErr = new LoGuardConnectionException($"server error {(int)resp.StatusCode}");
            }
            catch (LoGuardException) { throw; }
            catch (Exception e)
            {
                lastErr = new LoGuardConnectionException($"connection error: {e.Message}");
            }

            if (attempt < retries)
                await Task.Delay(400 * attempt, ct);
        }
        throw lastErr;
    }

    public static async Task<Dictionary<string, JsonElement>?> SendNoBodyAsync(
        string url, string apiKey, HttpMethod method, TimeSpan timeout, int retries, CancellationToken ct = default)
    {
        Exception lastErr = new LoGuardConnectionException("unknown error");
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            using var client = new HttpClient { Timeout = timeout };
            try
            {
                using var req = new HttpRequestMessage(method, url);
                req.Headers.Add("X-Api-Key", apiKey);
                req.Headers.Add("User-Agent", "loguard-csharp-sdk/0.1.0");
                using var resp = await client.SendAsync(req, ct);
                if (!RetryStatuses.Contains((int)resp.StatusCode))
                    return await RaiseForStatusAsync(resp);
                lastErr = new LoGuardConnectionException($"server error {(int)resp.StatusCode}");
            }
            catch (LoGuardException) { throw; }
            catch (Exception e) { lastErr = new LoGuardConnectionException($"connection error: {e.Message}"); }
            if (attempt < retries) await Task.Delay(400 * attempt, ct);
        }
        throw lastErr;
    }

    public static async Task<Dictionary<string, JsonElement>?> SendSignedMethodAsync<T>(
        string url, T payload, string apiKey, HttpMethod method, TimeSpan timeout, int retries, CancellationToken ct = default)
    {
        Exception lastErr = new LoGuardConnectionException("unknown error");
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            using var client = new HttpClient { Timeout = timeout };
            try
            {
                byte[] bodyBytes = Signing.BuildBody(payload);
                var headers = Signing.Sign(apiKey, bodyBytes);
                using var content = new ByteArrayContent(bodyBytes);
                content.Headers.Add("Content-Type", "application/json");
                using var req = new HttpRequestMessage(method, url) { Content = content };
                foreach (var (k, v) in headers)
                {
                    if (k == "Content-Type") continue;
                    req.Headers.TryAddWithoutValidation(k, v);
                }
                using var resp = await client.SendAsync(req, ct);
                if (!RetryStatuses.Contains((int)resp.StatusCode))
                    return await RaiseForStatusAsync(resp);
                lastErr = new LoGuardConnectionException($"server error {(int)resp.StatusCode}");
            }
            catch (LoGuardException) { throw; }
            catch (Exception e) { lastErr = new LoGuardConnectionException($"connection error: {e.Message}"); }
            if (attempt < retries) await Task.Delay(400 * attempt, ct);
        }
        throw lastErr;
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
            case 429: throw new LoGuardConnectionException("Rate limited");
        }
        if (status >= 500) throw new LoGuardConnectionException($"Server error ({status})");
        return data;
    }
}
