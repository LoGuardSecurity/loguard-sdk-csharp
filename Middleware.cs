using System.Net;
using System.Text.Json;
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
        if (trustedProxies.Contains(clientIp))
        {
            var xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrEmpty(xff))
            {
                string candidate = xff.Split(',')[0].Trim();
                if (IPAddress.TryParse(candidate, out _)) return candidate;
            }
        }
        return clientIp;
    }
}

/// <summary>Options for LoGuardMiddleware.</summary>
public class LoGuardMiddlewareOptions
{
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

    public LoGuardMiddleware(RequestDelegate next, LoGuardClient client, LoGuardMiddlewareOptions opts)
    {
        _next = next;
        _client = client;
        _opts = opts;

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
        _trackHeaders = opts.TrackHeaders.Where(h => !ForbiddenTrackHeaders.Values.Contains(h)).ToArray();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string ip = IpHelper.GetRealIp(context, _opts.TrustedProxies);

        await _next(context);

        if (_opts.TrackStatuses.Contains(context.Response.StatusCode))
        {
            string eventType = context.Response.StatusCode == 401 ? "login_failed" : "http_error";
            var meta = new Dictionary<string, object>();
            if (_trackHeaders.Length > 0)
            {
                var headers = new Dictionary<string, string>();
                foreach (var h in _trackHeaders)
                {
                    var val = context.Request.Headers[h].ToString();
                    if (!string.IsNullOrEmpty(val))
                        headers[h] = val.Length > 512 ? val[..512] : val;
                }
                if (headers.Count > 0) meta["headers"] = headers;
            }

            // Fire-and-forget so we never hold up the response to the user.
            _ = _client.EventAsync(new EventInput(eventType, ip, context.Request.Path, context.Response.StatusCode, Meta: meta));
        }
    }
}

/// <summary>Extension methods for wiring the middleware up in Program.cs.</summary>
public static class LoGuardMiddlewareExtensions
{
    public static IApplicationBuilder UseLoGuard(this IApplicationBuilder app, LoGuardClient client, LoGuardMiddlewareOptions? opts = null)
        => app.UseMiddleware<LoGuardMiddleware>(client, opts ?? new LoGuardMiddlewareOptions());
}
