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

    private static string TaskXml(string command, string arguments) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Principals><Principal id="Author"><LogonType>S4U</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Actions Context="Author">
            <Exec>
              <Command>{command}</Command>
              <Arguments>{arguments}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    [Fact]
    public void An_old_task_is_followed_into_its_vbs_launcher()
    {
        string? asked = null;
        var launch = ElevatedTask.FromTaskXml(TaskXml("wscript.exe", "\"C:\\Program Files\\MyRGB\\openrgb-hidden.vbs\""), p => { asked = p; return Launcher; });
        Assert.Equal(@"C:\Program Files\MyRGB\openrgb-hidden.vbs", asked);
        Assert.Equal(new TaskLaunch(@"C:\Users\kyle\AppData\Local\Programs\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe", ViaVbScript: true), launch);
        Assert.NotNull(ElevatedTask.UnsafeExe(launch));
    }

    [Fact]
    public void A_new_task_runs_OpenRGB_directly()
    {
        var launch = ElevatedTask.FromTaskXml(TaskXml(@"C:\Program Files\MyRGB\OpenRGB\OpenRGB.exe", "--server --server-port 6742"), _ => throw new InvalidOperationException("no file to read"));
        Assert.Equal(new TaskLaunch(@"C:\Program Files\MyRGB\OpenRGB\OpenRGB.exe", ViaVbScript: false), launch);
        Assert.Null(ElevatedTask.UnsafeExe(launch));
    }

    [Fact]
    public void A_missing_launcher_or_broken_xml_reads_as_unknown()
    {
        Assert.Equal(new TaskLaunch(null, ViaVbScript: true), ElevatedTask.FromTaskXml(TaskXml("wscript.exe", "\"C:\\gone.vbs\""), _ => null));
        Assert.Null(ElevatedTask.FromTaskXml("<not xml", _ => null));
        Assert.Null(ElevatedTask.UnsafeExe(null));
    }
}
