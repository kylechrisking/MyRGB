using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace MyRGB.Core;

/// <summary>
/// Devices come and go: USB hubs power down, OpenRGB restarts, a wireless dongle is pulled. Those
/// failures are expected, and a stack trace for each one buries the log. Log them as one line;
/// anything else keeps its full trace.
/// </summary>
public static class Disconnects
{
    /// <summary>True for "the device or connection went away" errors: I/O and socket failures, however wrapped.</summary>
    public static bool IsDisconnect(Exception ex) => ex switch
    {
        AggregateException a => a.InnerExceptions.Count > 0 && a.InnerExceptions.All(IsDisconnect),
        IOException or SocketException or ObjectDisposedException => true,
        _ => ex.InnerException is { } inner && IsDisconnect(inner),
    };

    /// <summary>The innermost message, which says what actually happened ("The device is not connected.").</summary>
    public static string Reason(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: > 0 } a) ex = a.InnerExceptions[0];
        while (ex.InnerException is { } inner) ex = inner;
        return ex.Message.TrimEnd('.');
    }

    /// <summary>Warn about <paramref name="ex"/>: one line for a disconnect, the full exception otherwise.</summary>
    public static void Warn(ILogger log, Exception? ex, string message, params object?[] args)
    {
        if (ex is not null && IsDisconnect(ex))
            log.LogWarning(message + " ({Reason})", [.. args, Reason(ex)]);
        else
            log.LogWarning(ex, message, args);
    }
}
