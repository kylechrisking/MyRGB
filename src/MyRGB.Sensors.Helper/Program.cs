// MyRGB's elevated sensor helper. Runs as administrator (a scheduled task the setup script
// registers at logon), reads the PC's sensors through LibreHardwareMonitor and answers MyRGB's
// requests over a named pipe, so MyRGB itself never needs to run elevated. The pipe only
// accepts the user MyRGB runs as; see SensorPipeServer for the rest of the contract.
using MyRGB.Core.Sensors;
using MyRGB.Sensors;

namespace MyRGB.Sensors.Helper;

internal static class Program
{
    private const string SingleInstance = @"Local\MyRGB.Sensors.Helper";

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 1;

        using var mutex = new Mutex(initiallyOwned: true, SingleInstance, out bool first);
        if (!first) return 0;

        // The task normally elevates the user who installed MyRGB, but UAC may have elevated a
        // different administrator; --allow is that desktop user, whose app needs the pipe.
        var allow = args.Zip(args.Skip(1)).Where(p => p.First is "--allow" or "-a").Select(p => p.Second).ToArray();

        var log = OpenLog();
        log("helper starting" + (allow.Length > 0 ? $" (pipe also allowed to {string.Join(", ", allow)})" : ""));

        ISensorSource source;
        try
        {
            source = new HardwareSensors();
        }
        catch (Exception ex)
        {
            log($"could not open the hardware: {ex.Message}");
            return 1;
        }

        using var server = new SensorPipeServer(source, null, SensorPipe.PipeName, null, allow);
        server.Start();
        log("serving");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    /// <summary>A line a day in the app's log folder, next to myrgb.log. Failure to log is never fatal.</summary>
    private static Action<string> OpenLog()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyRGB", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "helper.log");
            return message =>
            {
                try { File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}"); }
                catch (IOException) { }
            };
        }
        catch (Exception)
        {
            return _ => { };
        }
    }
}
