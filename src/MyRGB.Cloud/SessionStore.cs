using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyRGB.Cloud;

internal sealed record StoredSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, CloudUser User);

/// <summary>
/// Keeps the signed-in session between runs, encrypted with Windows DPAPI so only this Windows
/// account on this PC can read it. Nothing is kept in plain text.
/// </summary>
internal sealed class SessionStore(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyRGB session v1");

    public StoredSession? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredSession>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            Clear(); // unreadable (copied from another PC or account): start signed out
            return null;
        }
    }

    public void Save(StoredSession session)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(session), Entropy, DataProtectionScope.CurrentUser);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    public void Clear()
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }
}
