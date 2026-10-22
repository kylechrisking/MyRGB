using Microsoft.Win32;
using MyRGB.Cloud;

namespace MyRGB.App.Services;

/// <summary>
/// Windows 11's own lighting control ("Dynamic Lighting"). When it's on, Windows drives some
/// keyboards and mice itself and fights MyRGB over them. MyRGB only reads the setting; turning
/// it off is left to the user, in Windows Settings.
/// </summary>
public static class DynamicLighting
{
    private const string Key = @"Software\Microsoft\Lighting";

    /// <summary>True when "Use Dynamic Lighting on my devices" is on, false when off, null on Windows without it.</summary>
    public static bool? IsOn()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue("AmbientLightingEnabled") is int value ? value != 0 : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Open Settings › Personalization › Dynamic Lighting.</summary>
    public static void OpenSettings() =>
        Browser.Open("ms-settings:personalization-lighting");
}
