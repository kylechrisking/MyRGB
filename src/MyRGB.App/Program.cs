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
        StartMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        // One instance only: it owns the devices and the API port. A second launch just
        // asks the running one to show its window.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\MyRGB.App", out bool first);
        if (!first)
        {
            if (!StartMinimized) RequestShow();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
