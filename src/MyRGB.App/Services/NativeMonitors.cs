using System.Runtime.InteropServices;

namespace MyRGB.App.Services;

public sealed record MonitorInfo(string Name, int Brightness);

/// <summary>Something that can read and set monitor brightness: the monitors themselves in the app, a fake in snapshots.</summary>
public interface IMonitorControl
{
    /// <summary>Whether this PC can talk to monitors at all.</summary>
    bool Installed { get; }

    /// <summary>Every monitor that answers a brightness query.</summary>
    Task<IReadOnlyList<MonitorInfo>> ListAsync(CancellationToken ct);

    /// <summary>Set every monitor that answers, each to <paramref name="levelFor"/>(its name), 0..100.</summary>
    Task SetAllAsync(Func<string, int> levelFor, CancellationToken ct);
}

/// <summary>
/// Monitor brightness over DDC/CI, the control channel in the display cable, through Windows' own
/// Monitor Configuration API (dxva2.dll): VCP code 0x10 is brightness. No other program is needed;
/// the monitor only has to have DDC/CI switched on in its on-screen menu (most ship with it on).
/// Names come from the monitors' EDID through the DisplayConfig API ("DELL S3222DGM").
/// Every call is slow on the wire (~60 ms a monitor), so all of it runs off the UI thread, one at a time.
/// </summary>
public sealed class NativeMonitors : IMonitorControl
{
    private const byte VcpBrightness = 0x10;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Installed => OperatingSystem.IsWindows();

    public async Task<IReadOnlyList<MonitorInfo>> ListAsync(CancellationToken ct)
    {
        if (!Installed) return [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var names = FriendlyNames();
                var list = new List<MonitorInfo>();
                ForEachMonitor((device, description, handle) =>
                {
                    if (!TryRead(handle, out uint cur, out uint max)) return;
                    string name = names.TryGetValue(device, out var friendly) && friendly.Length > 0 ? friendly : description;
                    list.Add(new MonitorInfo(name, max == 0 ? 0 : (int)Math.Round(cur * 100.0 / max)));
                });
                return (IReadOnlyList<MonitorInfo>)list;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAllAsync(Func<string, int> levelFor, CancellationToken ct)
    {
        if (!Installed) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var failed = await Task.Run(() =>
            {
                var misses = new List<string>();
                var names = FriendlyNames();
                ForEachMonitor((device, description, handle) =>
                {
                    // Monitors don't all count to 100; scale to the range each one reports.
                    if (!TryRead(handle, out _, out uint max) || max == 0) return; // no DDC/CI: not ours to drive
                    string name = names.TryGetValue(device, out var friendly) && friendly.Length > 0 ? friendly : description;
                    int level = Math.Clamp(levelFor(name), 0, 100);
                    uint value = (uint)Math.Round(level * max / 100.0);
                    // DDC/CI wants ~50 ms between commands; the ASUS VG24VQ1B rejects a write sent straight after the read.
                    Thread.Sleep(50);
                    if (!SetVCPFeature(handle, VcpBrightness, value))
                    {
                        Thread.Sleep(50); // DDC/CI is fussy about pace; one retry covers a busy monitor
                        if (!SetVCPFeature(handle, VcpBrightness, value)) misses.Add(name);
                    }
                });
                return misses;
            }, ct).ConfigureAwait(false);
            if (failed.Count > 0) throw new IOException($"{string.Join(", ", failed)} didn't take the new brightness");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryRead(IntPtr handle, out uint cur, out uint max)
    {
        if (GetVCPFeatureAndVCPFeatureReply(handle, VcpBrightness, out _, out cur, out max)) return true;
        Thread.Sleep(50);
        return GetVCPFeatureAndVCPFeatureReply(handle, VcpBrightness, out _, out cur, out max);
    }

    /// <summary>Opens each physical monitor behind each display, hands it to <paramref name="each"/>, and closes it again.</summary>
    private static void ForEachMonitor(Action<string, string, IntPtr> each)
    {
        var displays = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) => { displays.Add(hMon); return true; }, IntPtr.Zero);
        foreach (var hMon in displays)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            string device = GetMonitorInfoW(hMon, ref info) ? info.Device : "";
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0) continue;
            var physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, physical)) continue;
            try
            {
                foreach (var pm in physical) each(device, pm.Description, pm.Handle);
            }
            finally
            {
                DestroyPhysicalMonitors(count, physical);
            }
        }
    }

    /// <summary>EDID names by GDI device name (\\.\DISPLAY1 → "DELL S3222DGM").</summary>
    private static Dictionary<string, string> FriendlyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const uint QdcOnlyActivePaths = 2;
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0) return map;
        var paths = new PathInfo[pathCount];
        var modes = new ModeInfo[modeCount];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return map;
        for (int i = 0; i < pathCount; i++)
        {
            var source = new SourceName { Header = new Header { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), AdapterId = paths[i].Source.AdapterId, Id = paths[i].Source.Id } };
            var target = new TargetName { Header = new Header { Type = 2, Size = (uint)Marshal.SizeOf<TargetName>(), AdapterId = paths[i].Target.AdapterId, Id = paths[i].Target.Id } };
            if (DisplayConfigGetDeviceInfo(ref source) == 0 && DisplayConfigGetDeviceInfo(ref target) == 0)
                map[source.GdiName] = target.FriendlyName;
        }
        return map;
    }

    // ---- Windows plumbing -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public int L, T, R, B, WL, WT, WR, WB;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMon, ref MonitorInfoEx info);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, out uint count);

    [DllImport("dxva2.dll")]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll")]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out uint type, out uint current, out uint max);

    [DllImport("dxva2.dll")]
    private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct PathSource { public Luid AdapterId; public uint Id, ModeInfoIdx, StatusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PathTarget
    {
        public Luid AdapterId;
        public uint Id, ModeInfoIdx, OutputTechnology, Rotation, Scaling, RefreshNum, RefreshDen, ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ModeInfo
    {
        public uint InfoType, Id;
        public Luid AdapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] Data;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type, Size; public Luid AdapterId; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        public Header Header;
        public uint Flags, OutputTechnology;
        public ushort ManufactureId, ProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceName request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetName request);
}
