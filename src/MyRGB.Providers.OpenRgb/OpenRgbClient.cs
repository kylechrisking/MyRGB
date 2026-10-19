using System.Buffers;
using System.Net.Sockets;
using MyRGB.Core;

namespace MyRGB.Providers.OpenRgb;

/// <summary>
/// A connection to an OpenRGB SDK server. Requests are matched to responses by packet id
/// (the server answers each id in order). Server pushes, like DeviceListUpdated, raise events.
/// </summary>
internal sealed class OpenRgbClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<PacketId, Queue<TaskCompletionSource<byte[]>>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _readLoop = Task.CompletedTask;

    private OpenRgbClient(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
    }

    /// <summary>The protocol version both sides agreed on: min(ours, server's).</summary>
    public uint ProtocolVersion { get; private set; }

    public bool Connected => _tcp.Connected && !_readLoop.IsCompleted;

    public event Action? DeviceListUpdated;
    public event Action<Exception?>? Disconnected;

    public static async Task<OpenRgbClient> ConnectAsync(string host, int port, string clientName, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, port, timeout.Token);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        var client = new OpenRgbClient(tcp);
        client._readLoop = Task.Run(client.ReadLoopAsync);
        await client.SendAsync(0, PacketId.SetClientName, Protocol.NullTerminated(clientName), ct);

        try
        {
            var reply = await client.RequestAsync(0, PacketId.RequestProtocolVersion, Protocol.UInt32(Protocol.ClientVersion), TimeSpan.FromSeconds(1), ct);
            client.ProtocolVersion = Math.Min(Protocol.ClientVersion, BitConverter.ToUInt32(reply));
        }
        catch (TimeoutException)
        {
            client.ProtocolVersion = 0; // pre-versioning servers never answer this request
        }
        return client;
    }

    public async Task<int> GetControllerCountAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(0, PacketId.RequestControllerCount, [], TimeSpan.FromSeconds(5), ct);
        return (int)BitConverter.ToUInt32(reply);
    }

    public async Task<ControllerData> GetControllerAsync(uint index, CancellationToken ct)
    {
        byte[] payload = ProtocolVersion > 0 ? Protocol.UInt32(ProtocolVersion) : [];
        var reply = await RequestAsync(index, PacketId.RequestControllerData, payload, TimeSpan.FromSeconds(5), ct);
        return Protocol.ParseController(reply, ProtocolVersion);
    }

    /// <summary>Switch a device to its per-LED "Direct" mode so UpdateLeds takes effect.</summary>
    public ValueTask SetCustomModeAsync(uint index, CancellationToken ct) =>
        SendAsync(index, PacketId.SetCustomMode, [], ct);

    /// <summary>Set a resizable zone's LED count. No reply; re-request the controller to see the result.</summary>
    public ValueTask ResizeZoneAsync(uint index, int zone, int newSize, CancellationToken ct) =>
        WriteAsync(Protocol.ResizeZone(index, zone, newSize), ct);

    public async ValueTask UpdateLedsAsync(uint index, ReadOnlyMemory<Rgb> colors, CancellationToken ct)
    {
        int size = Protocol.UpdateLedsSize(colors.Length);
        var buf = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            Protocol.WriteUpdateLeds(buf.AsSpan(0, size), index, colors.Span);
            await WriteAsync(buf.AsMemory(0, size), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private ValueTask SendAsync(uint index, PacketId id, byte[] payload, CancellationToken ct) =>
        WriteAsync(Protocol.Packet(index, id, payload), ct);

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await _stream.WriteAsync(packet, ct); }
        finally { _writeLock.Release(); }
    }

    private async Task<byte[]> RequestAsync(uint index, PacketId id, byte[] payload, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Enqueue and send under the write lock so the waiter order matches the order requests hit the wire.
        await _writeLock.WaitAsync(ct);
        try
        {
            lock (_pending)
            {
                if (!_pending.TryGetValue(id, out var q)) _pending[id] = q = new();
                q.Enqueue(tcs);
            }
            await _stream.WriteAsync(Protocol.Packet(index, id, payload), ct);
        }
        finally
        {
            _writeLock.Release();
        }
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch
        {
            lock (_pending)
                if (_pending.TryGetValue(id, out var q) && q.Count > 0 && q.Peek() == tcs) q.Dequeue();
            throw;
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        var header = new byte[Protocol.HeaderSize];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _cts.Token);
                var (_, id, size) = Protocol.ReadHeader(header);
                var payload = new byte[size];
                if (size > 0) await _stream.ReadExactlyAsync(payload, _cts.Token);

                if (id == PacketId.DeviceListUpdated)
                {
                    DeviceListUpdated?.Invoke();
                    continue;
                }

                TaskCompletionSource<byte[]>? waiter = null;
                lock (_pending)
                    if (_pending.TryGetValue(id, out var q) && q.Count > 0) waiter = q.Dequeue();
                waiter?.TrySetResult(payload);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { error = ex; }

        lock (_pending)
        {
            foreach (var q in _pending.Values)
                while (q.Count > 0) q.Dequeue().TrySetException(new IOException("OpenRGB connection closed.", error));
        }
        if (!_cts.IsCancellationRequested) Disconnected?.Invoke(error);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _tcp.Dispose();
        try { await _readLoop; } catch { /* closing */ }
        _cts.Dispose();
    }
}
