using System.Net.Sockets;

namespace MyRGB.Cloud;

/// <summary>
/// Plain words for "the MyRGB service didn't answer", and the underlying reason for the log.
/// On 2026-11-17 every sync since 11-07 had failed with the same "couldn't reach" line, and only
/// a DNS lookup showed why: the service had been paused, so its address no longer existed.
/// </summary>
public static class CloudErrors
{
    /// <summary>What to tell the user when a request never got an answer. <paramref name="then"/> ends the sentence.</summary>
    public static string Unreachable(Exception ex, string then = "Please try again later.") => Kind(ex) switch
    {
        Failure.NoAddress => $"The MyRGB service is offline right now. {then}",
        Failure.Timeout => $"The MyRGB service didn't answer in time. {then}",
        Failure.NoNetwork => $"This PC doesn't seem to be online. {then}",
        _ => $"Couldn't reach the MyRGB service. {then}",
    };

    /// <summary>The innermost reason, for the log: "No such host is known", "connection refused", a timeout.</summary>
    public static string Reason(Exception ex) => ex switch
    {
        TaskCanceledException => "timed out",
        _ => ex.GetBaseException() is SocketException s ? $"{s.SocketErrorCode}: {s.Message}" : ex.GetBaseException().Message,
    };

    internal enum Failure { Other, NoAddress, Timeout, NoNetwork }

    internal static Failure Kind(Exception ex)
    {
        if (ex is TaskCanceledException or TimeoutException) return Failure.Timeout;
        return ex.GetBaseException() is SocketException s
            ? s.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData => Failure.NoAddress,
                SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostUnreachable or SocketError.TryAgain => Failure.NoNetwork,
                _ => Failure.Other,
            }
            : Failure.Other;
    }
}
