namespace MyRGB.Providers.Corsair;

/// <summary>
/// Puts one static image on the Vanguard Pro 96's screen, following CORSAIR Web Hub's sequence
/// (docs/LCD-PROTOCOL.txt, "Web Hub's sequence for one static image"). Before writing anything it saves
/// every file it is about to change into a backup folder, and <see cref="RollBackAsync"/> puts them back
/// and deletes the files the upload made.
/// </summary>
internal static class ScreenUpload
{
    /// <summary>Web Hub keeps this much of the free storage in reserve on this model.</summary>
    public const long StorageReserve = 5 * 1024 * 1024;

    public sealed record Result(ushort ProfileFile, ushort ResourceFile, ushort ConfigFile, int ScreenIndex, bool WroteProperties, string BackupDir);

    internal const string ManifestName = "upload.txt";

    public static async Task<Result> UploadStaticImageAsync(CorsairKeyboard kb, ReadOnlyMemory<byte> rgb, string backupDir, Action<string>? say = null, CancellationToken ct = default)
    {
        say ??= _ => { };
        var image = ScreenFiles.BuildStaticImage(rgb.Span, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Everything is read and checked before the first write.
        var free = await kb.GetPropertyAsync(ScreenFiles.PropertyFreeStorage, ct) ?? throw new IOException("The keyboard didn't report its free storage.");
        if (image.Length > free - StorageReserve) throw new IOException($"Not enough room: {image.Length} bytes needed, {free} free, {StorageReserve} kept in reserve.");

        var list = ScreenFiles.ParseProfilesList(await Need(kb, ScreenFiles.ProfilesListFile, "profiles list", ct));
        // MyRGB doesn't know which profile is active yet; with one profile there is no question.
        if (list.ProfileFileIds.Count != 1) throw new NotSupportedException($"The keyboard has {list.ProfileFileIds.Count} profiles; uploading only handles one so far.");
        ushort profileId = list.ProfileFileIds[0];
        var profileBytes = await Need(kb, profileId, "profile", ct);
        var profile = ScreenFiles.ParseProfile(profileBytes) ?? throw new IOException($"Profile file {profileId} is unreadable.");
        if (profile.ScreenLayoutFile == 0 || profile.PropertiesFile == 0) throw new IOException($"Profile {profileId} has no screen layout or properties file.");

        var mapBytes = await Need(kb, ScreenFiles.ResourceMapFile, "resource map", ct);
        var layoutBytes = await Need(kb, profile.ScreenLayoutFile, "screen layout", ct);
        var propBytes = await Need(kb, profile.PropertiesFile, "properties", ct);

        var mentioned = ScreenFiles.MentionedHostIds(profileBytes).Concat(list.ProfileFileIds).Concat(ScreenFiles.MentionedHostIds(layoutBytes)).ToHashSet();
        var (resource, config) = await PickTwoFreeIdsAsync(kb, mentioned, ct);
        say($"profile {profileId}, layout {profile.ScreenLayoutFile}, properties {profile.PropertiesFile}; new resource {resource}, config {config}");

        Directory.CreateDirectory(backupDir);
        await File.WriteAllBytesAsync(Path.Combine(backupDir, $"file-{ScreenFiles.ResourceMapFile}.bin"), mapBytes, ct);
        await File.WriteAllBytesAsync(Path.Combine(backupDir, $"file-{profile.ScreenLayoutFile}.bin"), layoutBytes, ct);
        await File.WriteAllBytesAsync(Path.Combine(backupDir, $"file-{profile.PropertiesFile}.bin"), propBytes, ct);
        await File.WriteAllTextAsync(Path.Combine(backupDir, ManifestName),
            $"restore={ScreenFiles.ResourceMapFile},{profile.ScreenLayoutFile},{profile.PropertiesFile}\ncreated={config},{resource}\n", ct);
        say($"backed up to {backupDir}");

        // 1. Register the resource in the map.
        var map = ScreenFiles.ParseResourceMap(mapBytes);
        var newMap = ScreenFiles.BuildResourceMap(map.Header == 0 ? (ushort)0x0044 : map.Header, [.. map.Entries, new(resource, resource, 0)]);
        await kb.WriteFileAsync(ScreenFiles.ResourceMapFile, newMap, ct);
        say($"map 61 written ({newMap.Length} bytes)");

        // 2. The config that points at it.
        await kb.WriteFileAsync(config, ScreenFiles.BuildStaticConfig(resource), ct);
        say($"config {config} written");

        // 3. Add the config to row 0 of the profile's layout.
        var layout = ScreenFiles.WithConfigInRow0(ScreenFiles.ParseScreenLayout(layoutBytes), config);
        var newLayout = ScreenFiles.BuildScreenLayout(layout);
        await kb.WriteFileAsync(profile.ScreenLayoutFile, newLayout, ct);
        int index = layout.Rows[0].Count - 1;
        say($"layout {profile.ScreenLayoutFile} written: {Convert.ToHexString(newLayout)}");

        // 4. Point the screen index at it, only if it isn't already (Web Hub skips index 0 entirely).
        var props = ScreenFiles.ParseProperties(propBytes);
        bool wroteProps = props[ScreenFiles.PropertyScreenIndex] != (uint)index;
        if (wroteProps)
        {
            await kb.WriteFileAsync(profile.PropertiesFile, ScreenFiles.BuildProperties(ScreenFiles.WithProperty(props.Entries, ScreenFiles.PropertyScreenIndex, (uint)index)), ct);
            say($"properties {profile.PropertiesFile}: screen index set to {index}");
        }
        else say($"screen index is already {index}; properties file left alone");

        // 5. The image itself, last of the upload, as Web Hub does.
        await kb.WriteFileAsync(resource, image, ct);
        say($"image {resource} written ({image.Length} bytes)");

        // 6. Put it on screen. Web Hub's UI does this right after a successful upload (changeScreenWidget).
        await ShowAsync(kb, config, backupDir, say, ct);

        return new(profileId, resource, config, index, wroteProps, backupDir);
    }

    /// <summary>
    /// Shows a layout entry now: copies its config file into <see cref="ScreenFiles.CurrentScreenFile"/>
    /// (Web Hub's <c>selectWidget</c>). The current contents of that file are saved to the backup folder
    /// first and added to its manifest, so <see cref="RollBackAsync"/> puts back what was showing.
    /// </summary>
    public static async Task ShowAsync(CorsairKeyboard kb, ushort configFile, string backupDir, Action<string>? say = null, CancellationToken ct = default)
    {
        say ??= _ => { };
        const ushort current = ScreenFiles.CurrentScreenFile;
        var config = await Need(kb, configFile, "screen config", ct);

        var manifest = await ReadManifestAsync(backupDir, ct);
        if (!manifest["restore"].Contains(current) && !manifest["created"].Contains(current))
        {
            var before = await kb.ReadFileAsync(current, ct);
            if (before is null) manifest["created"] = [.. manifest["created"], current];
            else
            {
                await File.WriteAllBytesAsync(Path.Combine(backupDir, $"file-{current}.bin"), before, ct);
                manifest["restore"] = [current, .. manifest["restore"]];
            }
            await WriteManifestAsync(backupDir, manifest, ct);
            say($"file {current} before: {(before is null ? "did not exist" : Convert.ToHexString(before))}");
        }

        await kb.WriteFileAsync(current, config, ct);
        say($"file {current} now: {Convert.ToHexString(config)} (config {configFile})");
    }

    /// <summary>Writes the backed-up files back and deletes the ones the upload created.</summary>
    public static async Task RollBackAsync(CorsairKeyboard kb, string backupDir, Action<string>? say = null, CancellationToken ct = default)
    {
        say ??= _ => { };
        var manifest = await ReadManifestAsync(backupDir, ct);

        foreach (var id in manifest["restore"])
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(backupDir, $"file-{id}.bin"), ct);
            await kb.WriteFileAsync(id, bytes, ct);
            say($"restored file {id} ({bytes.Length} bytes)");
        }
        foreach (var id in manifest["created"])
            say($"deleted file {id}: {(await kb.DeleteFileAsync(id, ct) ? "ok" : "refused (already gone?)")}");
    }

    /// <summary>
    /// The two lowest host file ids that don't exist on the keyboard and that no profile file points at
    /// (a profile can name a file that hasn't been made yet).
    /// </summary>
    private static async Task<(ushort, ushort)> PickTwoFreeIdsAsync(CorsairKeyboard kb, ISet<ushort> mentioned, CancellationToken ct)
    {
        var found = new List<ushort>(2);
        for (int id = ScreenFiles.FirstHostFileId; id <= ScreenFiles.LastHostFileId && found.Count < 2; id++)
            if (id != ScreenFiles.SkippedFileId && !mentioned.Contains((ushort)id) && !await kb.FileExistsAsync((ushort)id, ct)) found.Add((ushort)id);
        if (found.Count < 2) throw new IOException("No free file ids left.");
        return (found[0], found[1]);
    }

    /// <summary><c>restore=&lt;ids&gt;</c> (files saved here, written back on rollback) and <c>created=&lt;ids&gt;</c> (deleted on rollback).</summary>
    private static async Task<Dictionary<string, ushort[]>> ReadManifestAsync(string backupDir, CancellationToken ct) =>
        (await File.ReadAllLinesAsync(Path.Combine(backupDir, ManifestName), ct))
            .Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ushort.Parse).ToArray());

    private static Task WriteManifestAsync(string backupDir, Dictionary<string, ushort[]> m, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(backupDir, ManifestName), string.Concat(m.Select(p => $"{p.Key}={string.Join(",", p.Value)}\n")), ct);

    private static async Task<byte[]> Need(CorsairKeyboard kb, ushort id, string what, CancellationToken ct) =>
        await kb.ReadFileAsync(id, ct) ?? throw new IOException($"The keyboard has no {what} file ({id}).");
}
