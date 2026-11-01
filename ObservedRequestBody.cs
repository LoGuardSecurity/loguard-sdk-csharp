namespace LoGuard.Sdk;

/// <summary>Copies only bytes consumed by downstream application code.</summary>
internal sealed class ObservedRequestBody(Stream inner, int maxBytes, long? expected) : Stream
{
    private readonly MemoryStream _captured = new();
    private long _seen;
    internal bool Complete { get; private set; } = expected == 0;
    internal bool TooLarge { get; private set; }
    internal bool Unavailable { get; private set; } = inner.CanSeek && inner.Position != 0;
    internal byte[] Captured => _captured.ToArray();

    private void Observe(ReadOnlySpan<byte> bytes, int requested)
    {
        if (bytes.Length == 0 && requested > 0) Complete = true;
        _seen += bytes.Length;
        if (expected.HasValue && _seen == expected) Complete = true;
        if (Unavailable || TooLarge) return;
        if (_captured.Length + bytes.Length > maxBytes)
        {
            TooLarge = true;
            _captured.SetLength(0);
        }
        else _captured.Write(bytes);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = inner.Read(buffer, offset, count);
        Observe(buffer.AsSpan(offset, n), count);
        return n;
    }
    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        Observe(buffer[..n], buffer.Length);
        return n;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int n = await inner.ReadAsync(buffer, ct);
        Observe(buffer.Span[..n], buffer.Length);
        return n;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int n = await inner.ReadAsync(buffer.AsMemory(offset, count), ct);
        Observe(buffer.AsSpan(offset, n), count);
        return n;
    }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set { Unavailable = true; _captured.SetLength(0); inner.Position = value; }
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Unavailable = true;
        _captured.SetLength(0);
        return inner.Seek(offset, origin);
    }
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Flush() => inner.Flush();
    internal void ReleaseCapture() => _captured.Dispose();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
