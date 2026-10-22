using System.Diagnostics;
using System.Security.Principal;

namespace MyRGB.Cloud;

/// <summary>Opens web pages in the user's browser.</summary>
public static class Browser
{
    private static readonly bool Elevated = CheckElevated();

    /// <summary>
    /// Open <paramref name="url"/> (http, https or ms-settings). When MyRGB runs as administrator the
    /// page is handed to Explorer, so the browser starts with the user's normal rights rather than
    /// inheriting MyRGB's.
    /// </summary>
    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "ms-settings"))
            throw new ArgumentException($"Not a web or settings link: {url}", nameof(url));
        var psi = Elevated
            ? new ProcessStartInfo("explorer.exe") { ArgumentList = { uri.AbsoluteUri }, UseShellExecute = false }
            : new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
        Process.Start(psi)?.Dispose();
    }

    private static bool CheckElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
