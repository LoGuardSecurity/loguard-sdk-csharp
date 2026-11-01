using System.Net;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;

namespace LoGuard.Sdk;

/// <summary>Headers that have historically carried real RCE exploits
/// (ShellShock, ProxyLogon) -- see the same rationale in the Python/Node/Go
/// SDKs.</summary>
public static class KnownExploitHeaders
{
    public static readonly string[] Values = ["User-Agent", "Referer", "X-BEResource", "X-AnonResource-Backend"];
}

internal static class ForbiddenTrackHeaders
{
    public static readonly HashSet<string> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "cookie", "set-cookie", "x-api-key", "x-auth-token", "proxy-authorization",
    };
}

internal static class IpHelper
{
    public static string GetRealIp(HttpContext ctx, IReadOnlyCollection<string> trustedProxies)
    {
        string clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!trustedProxies.Contains(clientIp)) return clientIp;
        string raw = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (raw.Length == 0 || raw.Length > 4096) return clientIp;
        var chain = raw.Split(',').Select(part => part.Trim()).ToArray();
        if (chain.Length > 64 || chain.Any(part => !IPAddress.TryParse(part, out _))) return clientIp;
        string current = clientIp;
        foreach (string candidate in chain.Reverse())
        {
            if (!trustedProxies.Contains(current)) break;
            current = candidate;
        }
        return current;
    }
}

/// <summary>Options for LoGuardMiddleware.</summary>
public class LoGuardMiddlewareOptions
{
    /// <summary>Enable bounded field capture for all response statuses.</summary>
    public bool SecurityCapture { get; set; }
    public bool TrackAll { get; set; }
    public int MaxBodyBytes { get; set; } = 32768;
    public int MaxJsonDepth { get; set; } = 8;
    public HashSet<int> TrackStatuses { get; set; } = [400, 401, 403, 404, 429, 500, 502, 503];
    public string[] TrackHeaders { get; set; } = [];
    public string[] TrustedProxies { get; set; } = [];
}

/// <summary>ASP.NET Core middleware for tracking HTTP errors as LoGuard
/// security events.</summary>
public class LoGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LoGuardClient _client;
    private readonly LoGuardMiddlewareOptions _opts;
    private readonly string[] _trackHeaders;
    private readonly HttpCapturePolicy _capture;

    public LoGuardMiddleware(RequestDelegate next, LoGuardClient client, LoGuardMiddlewareOptions opts)
    {
        _next = next;
        _client = client;
        _opts = new LoGuardMiddlewareOptions
        {
            SecurityCapture = opts.SecurityCapture,
            TrackAll = opts.TrackAll,
            MaxBodyBytes = opts.MaxBodyBytes,
            MaxJsonDepth = opts.MaxJsonDepth,
            TrackStatuses = new HashSet<int>(opts.TrackStatuses),
            TrackHeaders = opts.TrackHeaders.ToArray(),
            TrustedProxies = opts.TrustedProxies.ToArray(),
        };
        _capture = new HttpCapturePolicy(opts.MaxBodyBytes, opts.MaxJsonDepth);

        // Enforced in code, not just documented -- same protection as the
        // Python/Node/Go SDKs. These headers are never sent, even if
        // explicitly listed in TrackHeaders.
        var blocked = opts.TrackHeaders.Where(h => ForbiddenTrackHeaders.Values.Contains(h)).ToArray();
        if (blocked.Length > 0)
        {
            Console.Error.WriteLine(
                $"LoGuardMiddleware: refusing to track sensitive header(s) [{string.Join(", ", blocked)}] " +
                "-- these can carry credentials and are never forwarded, regardless of configuration.");
        }
        _trackHeaders = opts.TrackHeaders.Select(h => h.Trim().ToLowerInvariant()).Where(h => !ForbiddenTrackHeaders.Values.Contains(h)).Distinct().Take(64).ToArray();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        long started = Stopwatch.GetTimestamp();
        var original = context.Request.Body;
        ObservedRequestBody? observed = null;
        if (_opts.SecurityCapture && HttpCapturePolicy.Supports(context.Request.ContentType))
        {
            observed = new ObservedRequestBody(original, _capture.MaxBodyBytes, context.Request.ContentLength);
            context.Request.Body = observed;
        }
        bool failed = false;
        try { await _next(context); }
        catch { failed = true; throw; }
        finally
        {
            try
            {
                int status = failed && !context.Response.HasStarted ? 500 : context.Response.StatusCode;
                if (_opts.SecurityCapture || _opts.TrackAll || _opts.TrackStatuses.Contains(status))
                {
                    var meta = new Dictionary<string, object>
                    {
                        ["method"] = context.Request.Method,
                        ["duration_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    };
                    var headers = new Dictionary<string, string>();
                    foreach (var name in _trackHeaders)
                    {
                        string value = context.Request.Headers[name].ToString();
                        if (value.Length > 0) headers[name] = HttpCapturePolicy.Truncate(value, 512);
                    }
                    if (headers.Count > 0) meta["headers"] = headers;
                    if (_opts.SecurityCapture)
                    {
                        foreach (var pair in _capture.Query((context.Request.QueryString.Value ?? "").TrimStart('?'))) meta[pair.Key] = pair.Value;
                        if (!HttpCapturePolicy.Supports(context.Request.ContentType)) meta["body_unsupported_content_type"] = true;
                        else if (observed!.TooLarge) meta["body_too_large"] = true;
                        else if (observed.Unavailable || !observed.Complete) meta["body_unavailable"] = true;
                        else foreach (var pair in _capture.Body(observed.Captured, context.Request.ContentType)) meta[pair.Key] = pair.Value;
                    }
                    string type = status == 401 ? "login_failed" : (_opts.SecurityCapture ? "http_request" : "http_error");
                    _client.TryQueueHttp(new EventInput(type, IpHelper.GetRealIp(context, _opts.TrustedProxies), context.Request.Path, status, Meta: meta));
                }
            }
            catch (Exception) { /* Collection failure must not replace application outcomes. */ }
            finally { context.Request.Body = original; observed?.ReleaseCapture(); }
        }
    }

}

/// <summary>Extension methods for wiring the middleware up in Program.cs.</summary>
public static class LoGuardMiddlewareExtensions
{
    public static IApplicationBuilder UseLoGuard(this IApplicationBuilder app, LoGuardClient client, LoGuardMiddlewareOptions? opts = null)
        => app.UseMiddleware<LoGuardMiddleware>(client, opts ?? new LoGuardMiddlewareOptions());
}
