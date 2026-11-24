using MyRGB.Providers.OpenRgb;

namespace MyRGB.Tests;

public class ElevatedTaskTests
{
    private const string Launcher = """"
        Set sh = CreateObject("WScript.Shell")
        sh.CurrentDirectory = "C:\Users\kyle\AppData\Local\Programs\OpenRGB\OpenRGB Windows 64-bit"
        sh.Run """C:\Users\kyle\AppData\Local\Programs\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe"" --server --server-port 6742 --config ""C:\Program Files\MyRGB\config""", 0, False
        """";

    [Fact]
    public void Reads_the_exe_the_launcher_starts() =>
        Assert.Equal(@"C:\Users\kyle\AppData\Local\Programs\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe", ElevatedTask.ExeFrom(Launcher));

    [Fact]
    public void Only_Program_Files_counts_as_admin_only()
    {
        Assert.False(ElevatedTask.IsAdminOnly(@"C:\Users\kyle\AppData\Local\Programs\OpenRGB\OpenRGB.exe", @"C:\Program Files"));
        Assert.True(ElevatedTask.IsAdminOnly(@"C:\Program Files\MyRGB\OpenRGB\OpenRGB.exe", @"C:\Program Files"));
        Assert.False(ElevatedTask.IsAdminOnly(@"C:\Program Files Evil\OpenRGB.exe", @"C:\Program Files"));
        Assert.False(ElevatedTask.IsAdminOnly(@"C:\Program Files\..\Users\x\OpenRGB.exe", @"C:\Program Files"));
    }

    [Fact]
    public void A_launcher_without_a_run_line_reads_as_nothing() => Assert.Null(ElevatedTask.ExeFrom("garbage"));
}
