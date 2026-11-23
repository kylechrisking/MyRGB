using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MyRGB.Core;

namespace MyRGB.Tests;

public class DisconnectsTests
{
    [Fact]
    public void Unplugged_devices_and_dropped_connections_are_disconnects()
    {
        Assert.True(Disconnects.IsDisconnect(new IOException("The device is not connected.")));
        Assert.True(Disconnects.IsDisconnect(new AggregateException(new IOException("x", new SocketException(10054)))));
        Assert.False(Disconnects.IsDisconnect(new InvalidOperationException("bug")));
        Assert.False(Disconnects.IsDisconnect(new AggregateException(new IOException("x"), new NullReferenceException())));
    }

    [Fact]
    public void The_reason_is_the_innermost_message()
    {
        var ex = new AggregateException(new IOException("Unable to write data.", new SocketException(10054)));
        Assert.Equal(new SocketException(10054).Message.TrimEnd('.'), Disconnects.Reason(ex));
    }

    [Fact]
    public void Disconnects_log_one_line_and_bugs_keep_their_trace()
    {
        var log = new ListLogger();
        Disconnects.Warn(log, new IOException("The device is not connected."), "Lost the {Device}", "keyboard");
        Disconnects.Warn(log, new InvalidOperationException("bug"), "Lost the {Device}", "mouse");
        Assert.Equal("Lost the keyboard (The device is not connected)", log.Lines[0].Text);
        Assert.Null(log.Lines[0].Ex);
        Assert.Equal("Lost the mouse", log.Lines[1].Text);
        Assert.IsType<InvalidOperationException>(log.Lines[1].Ex);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(string Text, Exception? Ex)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format) =>
            Lines.Add((format(state, ex), ex));
    }
}
