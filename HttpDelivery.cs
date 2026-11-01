using System.Threading.Channels;

namespace LoGuard.Sdk;

public partial class LoGuardClient : IAsyncDisposable
{
    private readonly object _httpGate = new();
    private readonly Channel<Event> _httpQueue = Channel.CreateBounded<Event>(new BoundedChannelOptions(2000)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly CancellationTokenSource _httpStop = new();
    private Task? _httpWorker;
    private bool _httpClosed;

    internal bool TryQueueHttp(EventInput input)
    {
        // Build before enqueueing so timestamps reflect capture time.
        var ev = EventBuilder.Build(input, _env, _defaultService);
        lock (_httpGate)
        {
            if (_httpClosed) return false;
            if (_httpWorker is null)
            {
                // A long-lived worker must not retain the first request's AsyncLocal context.
                if (ExecutionContext.IsFlowSuppressed()) _httpWorker = Task.Run(DeliverHttpAsync);
                else
                {
                    using (ExecutionContext.SuppressFlow()) _httpWorker = Task.Run(DeliverHttpAsync);
                }
            }
            return _httpQueue.Writer.TryWrite(ev);
        }
    }

    private async Task DeliverHttpAsync()
    {
        try
        {
            await foreach (var head in _httpQueue.Reader.ReadAllAsync(_httpStop.Token))
            {
                var batch = new List<Event> { head };
                while (batch.Count < 50 && _httpQueue.Reader.TryRead(out var next)) batch.Add(next);
                try
                {
                    await Transport.SendSignedAsync(IngestUrl, new { events = batch }, _apiKey, _timeout, _retries, _httpStop.Token);
                }
                catch (Exception) { /* Best-effort delivery after transport retries. */ }
            }
        }
        catch (OperationCanceledException) when (_httpStop.IsCancellationRequested) { }
        finally { while (_httpQueue.Reader.TryRead(out _)) { } }
    }

    /// <summary>Stops HTTP admission and waits for delivery attempts.
    /// Cancellation aborts remaining HTTP delivery. Acceptance is not guaranteed.</summary>
    public async Task StopHttpDeliveryAsync(CancellationToken ct = default)
    {
        Task? worker;
        lock (_httpGate)
        {
            _httpClosed = true;
            _httpQueue.Writer.TryComplete();
            worker = _httpWorker;
        }
        if (worker is null) return;
        try { await worker.WaitAsync(ct); }
        catch (OperationCanceledException) { _httpStop.Cancel(); throw; }
    }

    /// <summary>Allows 30 seconds for queued HTTP delivery attempts before cancellation.</summary>
    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await StopHttpDeliveryAsync(deadline.Token); }
        catch (OperationCanceledException) { }
    }
}
