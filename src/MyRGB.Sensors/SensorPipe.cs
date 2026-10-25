using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyRGB.Core.Sensors;

namespace MyRGB.Sensors;

/// <summary>
/// The bridge between MyRGB, which usually runs without administrator rights, and the sensor
/// helper (MyRGB.Sensors.Helper), which runs elevated so LibreHardwareMonitor can see CPU
/// temperatures and drive motherboard fans. A named pipe carries JSON frames; the app polls it
/// once a second, exactly like it would read its own sensors. While the helper isn't running
/// the client reads a plain <see cref="HardwareSensors"/> of its own, which still sees CPU load,
/// memory and the GPU - so connecting to the helper only ever adds data.
/// </summary>
public static class SensorPipe
{
    public const string PipeName = "MyRGB.Sensors";

    /// <summary>Requests the helper understands. Readings is polled once a second; set and reset drive a fan.</summary>
    public sealed record Request(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("i")] string? Id = null,
        [property: JsonPropertyName("v")] double? Value = null);

    /// <summary>One sensor, in flight order: id, category, hardware, name, kind, value, key.</summary>
    public sealed record ReadingDto(
        [property: JsonPropertyName("i")] string Id,
        [property: JsonPropertyName("c")] SensorCategory Category,
        [property: JsonPropertyName("h")] string Hardware,
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("k")] SensorKind Kind,
        [property: JsonPropertyName("v")] double? Value,
        [property: JsonPropertyName("y")] string? Key = null);

    /// <summary>One fan, in flight order: id, name, guard temperature, rpm, duty cycle.</summary>
    public sealed record FanDto(
        [property: JsonPropertyName("i")] string Id,
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("g")] string GuardKey,
        [property: JsonPropertyName("r")] double? Rpm,
        [property: JsonPropertyName("p")] double? Percent);

    /// <summary>Answers carry ok; failures carry what went wrong in error. Extra fields per op. v is the helper's version.</summary>
    public sealed class Response
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("v")] public string? Version { get; set; }
        [JsonPropertyName("r")] public List<ReadingDto>? Readings { get; set; }
        [JsonPropertyName("f")] public List<FanDto>? Fans { get; set; }
    }

    internal static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>One request frame: a four-byte little-endian length, then UTF-8 JSON. Null when the client hung up.</summary>
    internal static SensorPipe.Request? ReadFrame(PipeStream pipe, CancellationToken ct)
    {
        var header = new byte[4];
        if (!ReadExactly(pipe, header, ct)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > MaxFrame) throw new IOException($"Frame of {length} bytes is not a request");
        var body = new byte[length];
        if (!ReadExactly(pipe, body, ct)) return null;
        return JsonSerializer.Deserialize<SensorPipe.Request>(body, Json);
    }

    /// <summary>
    /// One frame: a four-byte little-endian length, then UTF-8 JSON of the request or response.
    /// No Flush: on Windows it waits for the other end to read, which could stall the writer
    /// whenever the reader is slow. A local pipe's Write already delivers.
    /// </summary>
    internal static void WriteFrame<T>(PipeStream pipe, T payload, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        pipe.Write(frame, 0, frame.Length);
    }

    private const int MaxFrame = 1024 * 1024;

    private static bool ReadExactly(PipeStream pipe, byte[] buffer, CancellationToken ct)
    {
        int at = 0;
        while (at < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            int read = pipe.Read(buffer, at, buffer.Length - at);
            if (read == 0) return false; // the client hung up
            at += read;
        }
        return true;
    }
}

/// <summary>
/// The helper's half. Serves one client at a time, forever. The pipe is reachable only by the
/// user the app runs as (the task may elevate a different administrator, so the setup script
/// passes that account along with --allow) - no other user of the PC can even open it. A
/// watchdog hands fans back to their own control if the app goes quiet, so a crash never
/// leaves them stuck at whatever MyRGB last asked for.
/// </summary>
public sealed class SensorPipeServer : IDisposable
{
    public static readonly TimeSpan DefaultIdleReset = TimeSpan.FromSeconds(10);
    private const int MaxFrame = 1024 * 1024;

    private readonly ISensorSource _source;
    private readonly ILogger _log;
    private readonly string _pipeName;
    private readonly TimeSpan _idleReset;
    private readonly string[] _allow;
    private readonly HashSet<string> _softwareFans = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private NamedPipeServerStream? _serving;
    private CancellationTokenSource? _stopping;
    private Task? _loop;
    private long _lastRequest = DateTime.UtcNow.Ticks;

    public SensorPipeServer(ISensorSource source, ILogger? log = null,
        string pipeName = SensorPipe.PipeName, TimeSpan? idleReset = null, params string[] allowAccounts)
    {
        _source = source;
        _log = log ?? NullLogger.Instance;
        _pipeName = pipeName;
        _idleReset = idleReset ?? DefaultIdleReset;
        _allow = allowAccounts;
    }

    public void Start()
    {
        if (_stopping is not null) return;
        _stopping = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_stopping.Token));
        var watchdog = new Timer(_ => CheckIdle(), null, _idleReset / 2, _idleReset / 2);
        _stopping.Token.Register(() => watchdog.Dispose());
    }

    public void Dispose()
    {
        _stopping?.Cancel();
        try { _serving?.Dispose(); } // unblocks a read parked waiting for the app
        catch (ObjectDisposedException) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _source.Dispose();
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = OpenPipe();
                await pipe.WaitForConnectionAsync(ct);
                Serve(pipe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Serving the sensor pipe failed; listening again");
                try { await Task.Delay(200, ct); } catch (OperationCanceledException) { break; }
            }
            finally { pipe?.Dispose(); }
        }
    }

    private NamedPipeServerStream OpenPipe()
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1);
        var security = new PipeSecurity();
        Allow(security, WindowsIdentity.GetCurrent().User!);
        Allow(security, new NTAccount("NT AUTHORITY", "SYSTEM"));
        foreach (var account in _allow)
            try { Allow(security, new NTAccount(account)); }
            catch (IdentityNotMappedException) { _log.LogWarning("Pipe: {Account} is not a known account", account); }
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.None, 64 * 1024, 64 * 1024, security);
    }

    [SupportedOSPlatform("windows")]
    private static void Allow(PipeSecurity security, IdentityReference identity) =>
        security.AddAccessRule(new PipeAccessRule(identity, PipeAccessRights.FullControl, AccessControlType.Allow));

    /// <summary>One request, one answer, until the client goes away. Blocking: the helper has nothing else to do.</summary>
    private void Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        _log.LogInformation("Sensor helper: app connected");
        _serving = pipe;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (SensorPipe.ReadFrame(pipe, ct) is not { } request) return;
                Touch();
                SensorPipe.WriteFrame(pipe, Handle(request));
                Touch();
            }
        }
        finally
        {
            _serving = null;
        }
    }

    private SensorPipe.Response Handle(SensorPipe.Request request)
    {
        try
        {
            // Every answer carries the helper's version, so an app that updated itself can tell
            // when the copy in Program Files is older and ask for one more permission prompt.
            var version = typeof(SensorPipeServer).Assembly.GetName().Version?.ToString(3);
            switch (request.Op)
            {
                case "ping":
                    return new SensorPipe.Response { Ok = true, Version = version };
                case "readings":
                {
                    var readings = _source.Read();
                    return new SensorPipe.Response
                    {
                        Ok = true,
                        Version = version,
                        Readings = [.. readings.Select(r => new SensorPipe.ReadingDto(r.Id, r.Category, r.Hardware, r.Name, r.Kind, r.Value, r.Key))],
                        Fans = [.. _source.Fans.Select(f => new SensorPipe.FanDto(f.Id, f.Name, f.GuardKey, f.Rpm, f.Percent))],
                    };
                }
                case "set" when request.Id is { } id && request.Value is { } percent:
                {
                    var fan = _source.Fans.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (fan is null) return new SensorPipe.Response { Ok = false, Error = "no such fan" };
                    fan.Set(Math.Clamp(percent, 0, 100));
                    lock (_gate) _softwareFans.Add(id);
                    return new SensorPipe.Response { Ok = true };
                }
                case "reset" when request.Id is { } id:
                {
                    var fan = _source.Fans.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (fan is null) return new SensorPipe.Response { Ok = false, Error = "no such fan" };
                    fan.Reset();
                    lock (_gate) _softwareFans.Remove(id);
                    return new SensorPipe.Response { Ok = true };
                }
                default:
                    return new SensorPipe.Response { Ok = false, Error = "unknown op" };
            }
        }
        catch (Exception ex)
        {
            return new SensorPipe.Response { Ok = false, Error = ex.Message };
        }
    }

    /// <summary>The app reads once a second; silence past the idle window means it's gone, so fans it was driving go back to automatic.</summary>
    internal void CheckIdle()
    {
        string[] ids;
        lock (_gate) ids = [.. _softwareFans];
        if (ids.Length == 0 || DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastRequest) < _idleReset.Ticks) return;
        _log.LogWarning("Sensor helper: the app went quiet, handing {Count} fan(s) back to automatic", ids.Length);
        foreach (var id in ids)
        {
            var fan = _source.Fans.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            try { fan?.Reset(); } catch (Exception) { /* the hardware keeps whatever control it has */ }
            lock (_gate) _softwareFans.Remove(id);
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastRequest, DateTime.UtcNow.Ticks);
}

/// <summary>
/// The app's half. Talks to the helper when it's running and reads a plain, unelevated
/// <see cref="HardwareSensors"/> when it isn't, so the helper only ever adds CPU temperatures
/// and motherboard fans. Keeps trying every few seconds, so enabling the helper takes effect
/// without restarting MyRGB.
/// </summary>
public sealed class PipeSensors : ISensorSource
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);
    private const int MaxFrame = 1024 * 1024;

    private readonly ILogger _log;
    private readonly string _pipeName;
    private readonly TimeSpan _retryAfter;
    private readonly ISensorSource _fallback;
    private readonly object _gate = new();
    private readonly Dictionary<string, RemoteFan> _fans = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<IFanChannel> _fansSnapshot = [];
    private NamedPipeClientStream? _pipe;
    private DateTime _nextTry = DateTime.MinValue;
    private bool _connected;

    public PipeSensors(ILogger? log = null, string pipeName = SensorPipe.PipeName,
        TimeSpan? retryAfter = null, ISensorSource? fallback = null)
    {
        _log = log ?? NullLogger.Instance;
        _pipeName = pipeName;
        _retryAfter = retryAfter ?? RetryAfter;
        _fallback = fallback ?? new HardwareSensors();
    }

    /// <summary>Whether the elevated helper answered the last reading.</summary>
    public bool Connected => _connected;

    /// <summary>The helper's version, from its last answer; null while it isn't connected.</summary>
    public string? HelperVersion { get; private set; }

    /// <summary>Never blocks on the pipe: the helper's fans while it answers, the fallback's otherwise.</summary>
    public IReadOnlyList<IFanChannel> Fans => _connected ? _fansSnapshot : _fallback.Fans;

    public IReadOnlyList<SensorReading> Read()
    {
        lock (_gate)
        {
            if (!_connected && DateTime.UtcNow >= _nextTry) TryConnect();
            if (_connected && ReadOnce(out var readings)) return readings;
            if (_connected) Disconnect();
            return _fallback.Read();
        }
    }

    private void TryConnect()
    {
        _nextTry = DateTime.UtcNow + _retryAfter;
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
        }
        catch (Exception)
        {
            pipe.Dispose(); // the helper isn't running; the fallback covers until it is
            return;
        }
        _pipe = pipe;
        _connected = true;
        _log.LogInformation("Sensor helper connected; CPU temperatures and motherboard fans available");
    }

    private bool ReadOnce(out IReadOnlyList<SensorReading> readings)
    {
        readings = [];
        try
        {
            SensorPipe.WriteFrame(_pipe!, new SensorPipe.Request("readings"));
            var reply = ReadReply();
            if (reply is not { Ok: true } or { Readings: null }) return false;
            HelperVersion = reply.Version;
            readings = [.. reply.Readings.Select(r => new SensorReading(r.Id, r.Category, r.Hardware, r.Name, r.Kind, r.Value, r.Key))];
            RefreshFans(reply.Fans ?? []);
            return true;
        }
        catch (Exception)
        {
            return false; // the helper died mid-answer; the next read falls back and retries
        }
    }

    private SensorPipe.Response? ReadReply()
    {
        var pending = ReadReplyAsync();
        try
        {
            return pending.Wait(ReplyTimeout) ? pending.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    private async Task<SensorPipe.Response?> ReadReplyAsync()
    {
        var header = new byte[4];
        var pipe = _pipe!;
        if (!await ReadExactly(pipe, header).ConfigureAwait(false)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > MaxFrame) throw new IOException($"Frame of {length} bytes is not an answer");
        var body = new byte[length];
        if (!await ReadExactly(pipe, body).ConfigureAwait(false)) return null;
        return JsonSerializer.Deserialize<SensorPipe.Response>(body, SensorPipe.Json);
    }

    private static async Task<bool> ReadExactly(PipeStream pipe, byte[] buffer)
    {
        int at = 0;
        while (at < buffer.Length)
        {
            int read = await pipe.ReadAsync(buffer.AsMemory(at)).ConfigureAwait(false);
            if (read == 0) return false;
            at += read;
        }
        return true;
    }

    private void RefreshFans(IReadOnlyList<SensorPipe.FanDto> fresh)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fresh)
        {
            seen.Add(f.Id);
            if (_fans.TryGetValue(f.Id, out var fan)) fan.Refresh(f.Rpm, f.Percent);
            else _fans[f.Id] = new RemoteFan(this, f.Id, f.Name, f.GuardKey, f.Rpm, f.Percent);
        }
        foreach (var gone in _fans.Keys.Where(id => !seen.Contains(id)).ToList()) _fans.Remove(gone);
        _fansSnapshot = [.. _fans.Values.Cast<IFanChannel>()];
    }

    internal void SendSet(string id, double percent) => Send(new SensorPipe.Request("set", id, percent));
    internal void SendReset(string id) => Send(new SensorPipe.Request("reset", id));

    private void Send(SensorPipe.Request request)
    {
        lock (_gate)
        {
            if (!_connected) return; // the helper is away; FanController re-asks within ten seconds
            try
            {
                SensorPipe.WriteFrame(_pipe!, request);
                if (ReadReply() is not { Ok: true }) Disconnect();
            }
            catch (Exception)
            {
                Disconnect();
            }
        }
    }

    private void Disconnect()
    {
        _connected = false;
        HelperVersion = null;
        _pipe?.Dispose();
        _pipe = null;
        _fans.Clear();
        _fansSnapshot = [];
        _nextTry = DateTime.UtcNow + _retryAfter;
    }

    public void Dispose()
    {
        lock (_gate) Disconnect();
        _fallback.Dispose();
    }

    private sealed class RemoteFan(PipeSensors owner, string id, string name, string guardKey,
        double? rpm, double? percent) : IFanChannel
    {
        public string Id => id;
        public string Name => name;
        public string GuardKey => guardKey;
        public double? Rpm { get; private set; } = rpm;
        public double? Percent { get; private set; } = percent;

        public void Refresh(double? rpm, double? percent) => (Rpm, Percent) = (rpm, percent);

        public void Set(double value) => owner.SendSet(id, value);
        public void Reset() => owner.SendReset(id);
    }
}
