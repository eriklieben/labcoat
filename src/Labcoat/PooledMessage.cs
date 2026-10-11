using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Labcoat;

/// <summary>
/// The UTF-8 JSON of one outgoing event, serialized once and shared by every client it is queued for (see
/// <see cref="EventHub"/>). The bytes live in segments rented from <see cref="ArrayPool{T}.Shared"/> of
/// <see cref="SegmentSize"/> bytes (below the 85 KB large object heap threshold), so a 20 MB <c>state</c> event never
/// becomes one large string plus a UTF-8 copy per client. Sent as one WebSocket message of several frames.
/// </summary>
/// <remarks>
/// Reference counted: the creator holds one reference; <see cref="AddRef"/> before queueing it for a client,
/// <see cref="Release"/> when the client is done with it (sent, dropped or never queued). The last release returns the
/// segments to the pool. A message that is never released is simply collected (the pool does not need them back).
/// </remarks>
internal sealed class PooledMessage : IBufferWriter<byte>
{
    /// <summary>Size of the pooled segments (a larger one is rented only when a single JSON token needs more).</summary>
    public const int SegmentSize = 32 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = Json.Options.Encoder,
        Indented = Json.Options.WriteIndented,
        MaxDepth = Json.Options.MaxDepth,
    };

    private readonly List<(byte[] Buffer, int Count)> _segments = [];
    private byte[]? _current;
    private int _position;
    private int _refs = 1;
    private bool _sealed;

    /// <summary>Total number of bytes.</summary>
    public long Length { get; private set; }

    /// <summary>Number of segments (tests).</summary>
    internal int SegmentCount => _segments.Count;

    /// <summary>Serializes <paramref name="evt"/> with <see cref="Json.Options"/> (byte-for-byte what <see cref="JsonSerializer.Serialize{TValue}(TValue, JsonSerializerOptions?)"/> returns, as UTF-8).</summary>
    public static PooledMessage Serialize(ServerEvent evt) => Serialize<ServerEvent>(evt);

    /// <summary>Serializes any value with <see cref="Json.Options"/>.</summary>
    public static PooledMessage Serialize<T>(T value)
    {
        var m = new PooledMessage();
        try
        {
            using (var writer = new Utf8JsonWriter(m, WriterOptions))
                JsonSerializer.Serialize(writer, value, Json.Options);
            m.Seal();
            return m;
        }
        catch
        {
            m.Release(); // hands the rented segments back
            throw;
        }
    }

    public void Advance(int count)
    {
        if (_current is null || count < 0 || _position + count > _current.Length) throw new InvalidOperationException("Advance past the buffer.");
        _position += count;
        Length += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => Ensure(sizeHint).AsMemory(_position);

    public Span<byte> GetSpan(int sizeHint = 0) => Ensure(sizeHint).AsSpan(_position);

    private byte[] Ensure(int sizeHint)
    {
        if (_sealed) throw new InvalidOperationException("The message is complete.");
        sizeHint = Math.Max(sizeHint, 1);
        if (_current is not null && _current.Length - _position >= sizeHint) return _current;
        Commit();
        _current = ArrayPool<byte>.Shared.Rent(Math.Max(SegmentSize, sizeHint));
        return _current;
    }

    private void Commit()
    {
        if (_current is null) return;
        if (_position > 0) _segments.Add((_current, _position));
        else ArrayPool<byte>.Shared.Return(_current);
        _current = null;
        _position = 0;
    }

    private void Seal()
    {
        Commit();
        _sealed = true;
    }

    public void AddRef()
    {
        if (Interlocked.Increment(ref _refs) <= 1) throw new InvalidOperationException("The message was already released.");
    }

    public void Release()
    {
        var left = Interlocked.Decrement(ref _refs);
        if (left > 0) return;
        if (left < 0) throw new InvalidOperationException("The message was released more often than referenced.");
        Seal();
        foreach (var (buffer, _) in _segments) ArrayPool<byte>.Shared.Return(buffer);
        _segments.Clear();
    }

    /// <summary>Sends the bytes as one text message (one frame per segment).</summary>
    public async Task SendAsync(WebSocket socket, CancellationToken ct)
    {
        if (_segments.Count == 0)
        {
            await socket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, true, ct);
            return;
        }
        for (var i = 0; i < _segments.Count; i++)
        {
            var (buffer, count) = _segments[i];
            await socket.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Text, i == _segments.Count - 1, ct);
        }
    }

    /// <summary>The bytes as one array (tests / small messages only).</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[Length];
        var offset = 0;
        foreach (var (buffer, count) in _segments)
        {
            buffer.AsSpan(0, count).CopyTo(bytes.AsSpan(offset));
            offset += count;
        }
        return bytes;
    }

    public override string ToString() => Encoding.UTF8.GetString(ToArray());
}
