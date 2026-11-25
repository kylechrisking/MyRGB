using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MyRGB.Providers.OpenRgb;

/// <summary>What the elevated OpenRGB task starts, and whether it still goes through the VBScript launcher.</summary>
/// <param name="Exe">The OpenRGB it runs as administrator; null if that can't be read.</param>
/// <param name="ViaVbScript">True for tasks set up before 0.8.2, which ran wscript and a .vbs launcher.</param>
public sealed record TaskLaunch(string? Exe, bool ViaVbScript);

/// <summary>
/// Checks the elevated OpenRGB task (\MyRGB\OpenRGB, set up by tools\setup-openrgb-admin.ps1). The task
/// runs OpenRGB as administrator, so OpenRGB must live where only administrators can change it; one in
/// the user's own folder hands admin rights to anything that swaps the file. Turning on the sensor
/// helper from 0.4.0 to 0.7.6 re-pointed it there. Up to 0.8.1 the task ran a .vbs launcher, and
/// VBScript is being removed from Windows; since 0.8.2 it runs OpenRGB directly, in the background.
/// </summary>
public static partial class ElevatedTask
{
    public const string TaskName = @"\MyRGB\OpenRGB";

    /// <summary>The 64-bit Program Files, as the setup script uses, even from a 32-bit process.</summary>
    public static string ProgramFiles =>
        Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } pf ? pf : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>The launcher older setups wrote.</summary>
    public static string LauncherPath => Path.Combine(ProgramFiles, "MyRGB", "openrgb-hidden.vbs");

    /// <summary>Where the admin copy of OpenRGB belongs.</summary>
    public static string SafeOpenRgbDir => Path.Combine(ProgramFiles, "MyRGB", "OpenRGB");

    /// <summary>The exe a launcher script starts, or null if it can't be read.</summary>
    public static string? ExeFrom(string launcher) => RunLine().Match(launcher) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Whether <paramref name="exe"/> sits under <paramref name="programFiles"/>, which only administrators can change.</summary>
    public static bool IsAdminOnly(string exe, string programFiles)
    {
        string full = Path.GetFullPath(exe);
        string root = Path.GetFullPath(programFiles).TrimEnd('\\') + '\\';
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the task's action from its XML (schtasks /query /xml). A wscript action is followed into
    /// its .vbs through <paramref name="readFile"/>. Null when the XML has no Exec action.
    /// </summary>
    public static TaskLaunch? FromTaskXml(string xml, Func<string, string?> readFile)
    {
        XElement? exec;
        try
        {
            exec = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Exec");
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
        string? command = exec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim().Trim('"');
        if (string.IsNullOrEmpty(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command);

        if (Path.GetFileName(command) is { } name && (name.Equals("wscript.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("wscript", StringComparison.OrdinalIgnoreCase)))
        {
            string args = exec!.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value ?? "";
            var script = VbsArgument().Match(args);
            string? text = script.Success ? readFile(script.Groups[1].Value) : null;
            return new TaskLaunch(text is null ? null : ExeFrom(text), ViaVbScript: true);
        }
        return new TaskLaunch(command, ViaVbScript: false);
    }

    /// <summary>What the task on this PC starts; null when there's no task or it can't be read.</summary>
    public static TaskLaunch? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/query", "/tn", TaskName, "/xml" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync();
            string xml = p.StandardOutput.ReadToEnd();
            stderr.Wait(5000);
            if (!p.WaitForExit(5000) || p.ExitCode != 0) return null;
            return FromTaskXml(xml, path => File.Exists(path) ? File.ReadAllText(path) : null);
        }
        catch (Exception)
        {
            return null; // can't tell; say nothing rather than cry wolf
        }
    }

    /// <summary>The user-writable OpenRGB a task would run as administrator, or null when it's safe (or unknown).</summary>
    public static string? UnsafeExe(TaskLaunch? launch)
    {
        try
        {
            return launch?.Exe is { } exe && !IsAdminOnly(exe, ProgramFiles) ? exe : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"sh\.Run """"""(.+?)""""")]
    private static partial Regex RunLine();

    [GeneratedRegex(@"""?([^""]+\.vbs)""?", RegexOptions.IgnoreCase)]
    private static partial Regex VbsArgument();
}
