using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LoGuard.Tests.TestSupport;

/// <summary>
/// One recorded HTTP request, captured exactly as it arrived over the wire
/// (real sockets, real HTTP parsing) -- not a mock object standing in for
/// one.
/// </summary>
public sealed record RecordedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>One response to serve back, for a single recorded request.</summary>
public sealed record ServedResponse(int Status, string Body, string? RedirectLocation = null);

/// <summary>
/// A tiny real HTTP server on 127.0.0.1, used because LoGuard.Sdk's
/// <c>Transport</c> constructs its own <see cref="HttpClient"/> internally
/// (see Transport.cs) and has no injection point for a fake
/// <see cref="HttpMessageHandler"/>. Rather than invent an abstraction the
/// SDK doesn't have, tests talk to this over real loopback sockets --
/// real signing, real HTTP, real retries, zero external network.
/// </summary>
public sealed class MockHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public string BaseUrl { get; }

    /// <summary>
    /// Called once per request to decide the response. Defaults to a
    /// plain 200 OK with an empty ingest-shaped body. Set
    /// <see cref="ServedResponse.RedirectLocation"/> to emit a redirect.
    /// Not thread-safe to reassign mid-request -- set it before issuing
    /// requests in a given test.
    /// </summary>
    public Func<RecordedRequest, ServedResponse> Handler { get; set; }
        = _ => new ServedResponse(200, """{"ok":true,"accepted":1,"dropped":0,"alerts":[],"plan":"test","usage":{}}""");

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public MockHttpServer()
    {
        int port = GetFreeLoopbackPort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{BaseUrl}/");
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    private static int GetFreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token);
            }
            catch (Exception)
            {
                break; // listener stopped/disposed, or token cancelled
            }

            try
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                string body = await reader.ReadToEndAsync();

                var headers = ctx.Request.Headers.AllKeys
                    .Where(k => k is not null)
                    .ToDictionary(k => k!, k => ctx.Request.Headers[k] ?? "");

                var recorded = new RecordedRequest(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, headers, body);
                _requests.Enqueue(recorded);

                ServedResponse served = Handler(recorded);
                ctx.Response.StatusCode = served.Status;
                if (served.RedirectLocation is not null)
                {
                    ctx.Response.RedirectLocation = served.RedirectLocation;
                }
                ctx.Response.ContentType = "application/json";
                byte[] buf = Encoding.UTF8.GetBytes(served.Body);
                ctx.Response.ContentLength64 = buf.Length;
                await ctx.Response.OutputStream.WriteAsync(buf);
            }
            finally
            {
                ctx.Response.OutputStream.Close();
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _listener.Close();
        _cts.Dispose();
    }
}
