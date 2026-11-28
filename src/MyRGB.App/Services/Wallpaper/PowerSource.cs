using System.Runtime.InteropServices;

namespace MyRGB.App.Services.Wallpaper;

/// <summary>Whether the PC is running on its battery right now (a laptop off the charger), from Windows.</summary>
internal static class PowerSource
{
    /// <summary>True while Windows says the PC runs on battery. False on a desktop, when plugged in, or when Windows can't say.</summary>
    public static bool OnBattery()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return GetSystemPowerStatus(out var status) && OnBattery(status.ACLineStatus, status.BatteryFlag);
    }

    /// <summary>Windows' answer read plainly: charger unplugged (0) and a battery present (flag 128 means none).</summary>
    internal static bool OnBattery(byte acLineStatus, byte batteryFlag) => acLineStatus == 0 && batteryFlag != 128 && batteryFlag != 255;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
