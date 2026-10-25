using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using MyRGB.Core.Sensors;
using MyRGB.Sensors;

namespace MyRGB.Tests;

public class SensorPipeTests : IDisposable
{
    private readonly string _pipe = $"MyRGB.Sensors.Test.{Guid.NewGuid():N}";
    private readonly List<IDisposable> _disposables = [];

    private SensorPipeServer StartServer(FakeSensors source, TimeSpan? idle = null)
    {
        var server = new SensorPipeServer(source, pipeName: _pipe, idleReset: idle);
        server.Start();
        _disposables.Add(server);
        return server;
    }

    private PipeSensors Client(FakeSensors? fallback = null)
    {
        // The fallback must be a fake: a real HardwareSensors would pull LibreHardwareMonitor,
        // its kernel driver and this machine's actual hardware into the test process.
        var client = new PipeSensors(pipeName: _pipe, retryAfter: TimeSpan.FromMilliseconds(50),
            fallback: fallback ?? new FakeSensors());
        _disposables.Add(client);
        return client;
    }

    
    [Fact]
    public async Task Readings_and_fans_round_trip()
    {
        StartServer(new FakeSensors());
        var client = Client();

        var readings = Wait.For(() => client.Read() is { Count: > 0 } r ? r : null);

        var reading = Assert.Single(readings);
        Assert.Equal("helper/cpu/temp", reading.Id);
        Assert.Equal(55, reading.Value);
        var fan = Assert.Single(client.Fans);
        Assert.Equal("helper/cpu fan", fan.Name);
        Assert.Equal(SensorKeys.CpuTemp, fan.GuardKey);
        Assert.Equal(2400, fan.Rpm);
    }

    [Fact]
    public async Task Fan_set_and_reset_travel_to_the_helper()
    {
        var source = new FakeSensors();
        StartServer(source);
        var client = Client();
        Wait.For(() => client.Read().Count > 0);
        var fan = Assert.Single(client.Fans);

        fan.Set(45);
        Wait.For(() => source.Fan.SetCalls.Contains(45));
        fan.Reset();
        Wait.For(() => source.Fan.Resets > 0);
    }

    [Fact]
    public async Task A_set_above_100_is_clamped_by_the_helper()
    {
        var source = new FakeSensors();
        StartServer(source);
        var client = Client();
        Wait.For(() => client.Read().Count > 0);

        Assert.Single(client.Fans).Set(150);

        Wait.For(() => source.Fan.SetCalls.Contains(100));
        Assert.DoesNotContain(150, source.Fan.SetCalls);
    }

    [Fact]
    public void While_the_helper_is_away_the_fallback_answers()
    {
        var fallback = new FakeSensors("fallback");
        var client = Client(fallback);

        var readings = client.Read();

        Assert.Equal(fallback.Readings, readings);
        Assert.False(client.Connected);
        Assert.Equal(fallback.Fans, client.Fans);
    }

    [Fact]
    public async Task A_helper_started_later_is_picked_up_without_restarting()
    {
        var client = Client(new FakeSensors("fallback")); // no helper yet: the fallback answers
        Assert.Equal("fallback/cpu/temp", Assert.Single(client.Read()).Id);

        StartServer(new FakeSensors());
        var readings = Wait.For(() => client.Read() is { Count: > 0 } r ? r : null);

        Assert.Equal("helper/cpu/temp", Assert.Single(readings).Id);
    }

    [Fact]
    public async Task A_helper_that_goes_away_falls_back_and_comes_back()
    {
        var server = StartServer(new FakeSensors());
        var client = Client(new FakeSensors("fallback"));
        Wait.For(() => client.Read().Count > 0);

        server.Dispose();
        Assert.Equal("fallback/cpu/temp", Assert.Single(client.Read()).Id);

        StartServer(new FakeSensors());
        var readings = Wait.For(() => client.Read().Count > 0 && client.Read()[0].Id == "helper/cpu/temp"
            ? client.Read() : null);
        Assert.Equal("helper/cpu/temp", Assert.Single(readings).Id);
    }

    [Fact]
    public async Task An_app_gone_quiet_has_its_fans_handed_back()
    {
        var source = new FakeSensors();
        StartServer(source, idle: TimeSpan.FromMilliseconds(250));
        var client = Client();
        Wait.For(() => client.Read().Count > 0);
        Assert.Single(client.Fans).Set(50);
        Wait.For(() => source.Fan.SetCalls.Contains(50));

        Wait.For(() => source.Fan.Resets > 0, seconds: 5);
    }

    [Fact]
    public async Task Frames_reject_absurd_lengths()
    {
        using var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = client.ConnectAsync(5000);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await connected;

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        var write = Task.Run(() => client.Write(header));

        Assert.Throws<IOException>(() => SensorPipe.ReadFrame(server, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        // Clients first: a connected client keeps the server's read parked.
        for (var i = _disposables.Count - 1; i >= 0; i--) _disposables[i].Dispose();
    }
}

/// <summary>Stand-in for the helper's hardware; the id prefix tells helper and fallback readings apart.</summary>
internal sealed class FakeSensors(string idPrefix = "helper") : ISensorSource
{
    public FakeFan Fan { get; } = new($"{idPrefix}/cpu fan");
    public List<SensorReading> Readings { get; } =
        [new($"{idPrefix}/cpu/temp", SensorCategory.Cpu, "Fake CPU", "Package", SensorKind.Temperature, 55, SensorKeys.CpuTemp)];

    public IReadOnlyList<SensorReading> Read() => Readings;
    public IReadOnlyList<IFanChannel> Fans => [Fan];
    public void Dispose() { }
}

internal sealed class FakeFan(string id) : IFanChannel
{
    public ConcurrentQueue<double> SetCalls { get; } = [];
    public int Resets;

    public string Id => id;
    public string Name => id;
    public string GuardKey => SensorKeys.CpuTemp;
    public double? Rpm => 2400;
    public double? Percent => SetCalls.IsEmpty ? null : SetCalls.Last();
    public void Set(double percent) => SetCalls.Enqueue(percent);
    public void Reset() => Interlocked.Increment(ref Resets);
}

file static class Wait
{
    public static T For<T>(Func<T?> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition() is { } value) return value; }
            catch (Exception) { /* not ready yet */ }
            Thread.Sleep(20);
        }
        throw new TimeoutException("the condition was never met");
    }
}
