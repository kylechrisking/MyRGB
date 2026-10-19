using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MyRGB.Hid;

/// <param name="InputReportLength">Bytes per input report, including the report id byte.</param>
/// <param name="OutputReportLength">Bytes per output report, including the report id byte.</param>
/// <param name="FeatureReportLength">Bytes per feature report, including the report id byte.</param>
public sealed record HidInterface(
    string Path, ushort UsagePage, ushort Usage, int InputReportLength, int OutputReportLength, int FeatureReportLength);

/// <summary>
/// Just enough Windows HID plumbing for MyRGB's native drivers: find interfaces, open them for
/// overlapped report I/O, and exchange feature reports. Windows only; no third-party HID library.
/// </summary>
public static class HidDevices
{
    /// <summary>Present HID interfaces whose path contains <paramref name="pathFilter"/> (e.g. "vid_1b1c&amp;pid_2b0e").</summary>
    public static List<HidInterface> Find(string pathFilter)
    {
        var result = new List<HidInterface>();
        if (!OperatingSystem.IsWindows()) return result;
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var data = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                var buf = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, needed, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buf + 4)!;
                    if (path.Contains(pathFilter, StringComparison.OrdinalIgnoreCase) && Caps(path) is { } caps)
                        result.Add(caps);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    /// <summary>Opens the interface for overlapped read/write, so a pending read never blocks a write.</summary>
    public static FileStream Open(string path)
    {
        var h = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"Could not open HID device (error {Marshal.GetLastWin32Error()}).");
        return new FileStream(h, FileAccess.ReadWrite, 0, isAsync: true);
    }

    /// <summary>
    /// Opens the interface for feature reports only. Falls back to a zero-access handle, which is
    /// all Windows allows on the mouse and keyboard collections it owns, and is still enough for
    /// HidD_SetFeature / HidD_GetFeature.
    /// </summary>
    public static SafeFileHandle OpenForFeatures(string path)
    {
        var h = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        h = CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"Could not open HID device (error {Marshal.GetLastWin32Error()}).");
        return h;
    }

    /// <summary>Send a feature report. <paramref name="report"/> starts with the report id byte.</summary>
    public static bool SetFeature(SafeFileHandle h, byte[] report) => HidD_SetFeature(h, report, report.Length);

    /// <summary>Read a feature report into <paramref name="report"/>, whose first byte must hold the report id.</summary>
    public static bool GetFeature(SafeFileHandle h, byte[] report) => HidD_GetFeature(h, report, report.Length);

    private static HidInterface? Caps(string path)
    {
        // Zero access rights: enough to read capabilities, even for keyboards Windows holds exclusively.
        using var h = CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid || !HidD_GetPreparsedData(h, out var pp)) return null;
        try
        {
            return HidP_GetCaps(pp, out var caps) == HidpStatusSuccess
                ? new HidInterface(path, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength)
                : null;
        }
        finally
        {
            HidD_FreePreparsedData(pp);
        }
    }

    private const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3, FileFlagOverlapped = 0x40000000;
    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, out HidpCaps caps);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
