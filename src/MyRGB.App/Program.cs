using Avalonia;
using System.Net.Http;

namespace MyRGB.App;

class Program
{
    public static bool StartMinimized { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // The background copy that repaints the desktop if the main one is ended with its wallpaper up.
        if (args.Length == 2 && args[0] == Services.Wallpaper.DesktopGuard.WatchArgument)
        {
            if (int.TryParse(args[1], out int owner)) Services.Wallpaper.DesktopGuard.Watch(owner);
            return;
        }

        StartMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        LogCrashes();

        // One instance only: it owns the devices and the API port. A second launch just
        // asks the running one to show its window, unless it runs as administrator and the
        // running one doesn't: then it asks that one to close and takes over, so "Run as
        // administrator" gets the CPU sensors and fan control instead of doing nothing.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\MyRGB.App", out bool first);
        if (!first && !(IsElevated && TakeOver(mutex)))
        {
            if (!StartMinimized) RequestShow();
            return;
        }

        // Only an instance without admin rights offers to hand over.
        if (!IsElevated) Handover = new EventWaitHandle(false, EventResetMode.AutoReset, HandoverName);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Put a crash in MyRGB's own log, not just Windows' event log. The 11-18 crash (a switch
    /// re-templated by a theme change) was only found five days later in the Application event log.
    /// </summary>
    private static void LogCrashes() => AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        try
        {
            string log = Path.Combine(MyRGB.App.Services.SettingsStore.DefaultDataDirectory, "logs", "myrgb.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} Critical    Program: MyRGB crashed{Environment.NewLine}{e.ExceptionObject}{Environment.NewLine}");
        }
        catch { /* the process is going down either way */ }
    };

    private const string HandoverName = @"Local\MyRGB.Handover";

    /// <summary>Signalled when an elevated MyRGB wants this one to close and hand it the devices. Null when this one is elevated.</summary>
    public static EventWaitHandle? Handover { get; private set; }

    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Ask the running instance to close, then wait for it to let go of the mutex.</summary>
    private static bool TakeOver(Mutex mutex)
    {
        if (!OperatingSystem.IsWindows() || !EventWaitHandle.TryOpenExisting(HandoverName, out var handover)) return false; // the running one is elevated too
        using (handover) handover.Set();
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(20));
        }
        catch (AbandonedMutexException)
        {
            return true; // it exited without releasing it, which still hands it over
        }
    }

    /// <summary>Run one program elevated and hidden, e.g. the setup script behind a single Windows prompt. False if Windows was told no.</summary>
    public static bool Elevate(string fileName, string arguments)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // the user said no to the Windows prompt
        }
    }

    private static void RequestShow()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.Add(Api.LocalApiServer.ClientHeader, "app");
            http.PostAsync($"http://localhost:{Api.LocalApiServer.DefaultPort}/api/app/show", null).GetAwaiter().GetResult();
        }
        catch (Exception) { /* the other instance is still starting; nothing to do */ }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
