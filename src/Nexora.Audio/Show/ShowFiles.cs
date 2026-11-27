using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexora.Audio.Mix;

namespace Nexora.Audio.Show;

/// <summary>What a show file says about itself.</summary>
public sealed record ShowManifest(string Format, int Version, string Name, DateTime SavedUtc, string SavedBy, string App)
{
    public const string FormatName = "nexaudio-show";
    public const int CurrentVersion = 1;
}

/// <summary>What is inside a show file, once checked.</summary>
public sealed record ShowContents(ShowManifest Manifest, bool HasMix, int Channels, int Buses, int Scenes, int Snapshots, int Presets, int Cues, int Macros);

/// <summary>
/// Show files (.nexshow): the whole desk in one file to keep, copy to a USB stick or send to another
/// church. Inside is a zip of the mix, scenes, snapshots, presets, cue list and macros. The PC's own
/// settings (the X32's address, the failsafe, the recorder) stay on the PC.
/// </summary>
public static partial class ShowFiles
{
    public const string Extension = ".nexshow";

    [GeneratedRegex(@"^(mix|show)\.json$|^(scenes|snapshots|presets)/[A-Za-z0-9_-]{1,40}\.json$")]
    private static partial Regex AllowedEntry();

    private static readonly string[] Folders = ["scenes", "snapshots", "presets"];
    private static readonly JsonSerializerOptions Json = new(MixState.Json) { WriteIndented = true };

    /// <summary>Writes the desk's files into one show file.</summary>
    public static void Export(string dataDir, string file, string name, string by)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir is not null) Directory.CreateDirectory(dir);
        var tmp = file + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            var manifest = new ShowManifest(ShowManifest.FormatName, ShowManifest.CurrentVersion, name, DateTime.UtcNow, by, "NEX-Audio");
            var e = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open())) w.Write(JsonSerializer.Serialize(manifest, Json));
            foreach (var top in new[] { "mix.json", "show.json" })
            {
                var f = Path.Combine(dataDir, top);
                if (File.Exists(f)) zip.CreateEntryFromFile(f, top, CompressionLevel.Optimal);
            }
            foreach (var folder in Folders)
            {
                var d = Path.Combine(dataDir, folder);
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.EnumerateFiles(d, "*.json"))
                {
                    var entry = $"{folder}/{Path.GetFileName(f)}";
                    if (AllowedEntry().IsMatch(entry)) zip.CreateEntryFromFile(f, entry, CompressionLevel.Optimal);
                }
            }
        }
        File.Move(tmp, file, true);
    }

    /// <summary>Opens and checks a show file without changing anything. Throws with a plain message if it is not right.</summary>
    public static ShowContents Inspect(string file) => Read(file, null);

    /// <summary>
    /// Puts a show file's scenes, snapshots, presets, cue list and macros on this desk, and its mix too when
    /// takeMix is set. Everything is checked before anything is replaced.
    /// </summary>
    public static ShowContents Import(string dataDir, string file, bool takeMix)
    {
        var staging = Path.Combine(dataDir, ".opening");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            var contents = Read(file, staging);
            foreach (var folder in Folders)
            {
                var dst = Path.Combine(dataDir, folder);
                Directory.CreateDirectory(dst);
                foreach (var old in Directory.EnumerateFiles(dst, "*.json")) File.Delete(old);
                var src = Path.Combine(staging, folder);
                if (!Directory.Exists(src)) continue;
                foreach (var f in Directory.EnumerateFiles(src, "*.json")) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            }
            var show = Path.Combine(staging, "show.json");
            if (File.Exists(show)) File.Copy(show, Path.Combine(dataDir, "show.json"), true);
            else File.WriteAllText(Path.Combine(dataDir, "show.json"), JsonSerializer.Serialize(new ShowState { Name = contents.Manifest.Name }, Json));
            if (takeMix && contents.HasMix) File.Copy(Path.Combine(staging, "mix.json"), Path.Combine(dataDir, "mix.json"), true);
            return contents;
        }
        finally
        {
            try { Directory.Delete(staging, true); }
            catch (Exception) { }
        }
    }

    private static ShowContents Read(string file, string? extractTo)
    {
        if (!File.Exists(file)) throw new InvalidOperationException("That show file could not be found.");
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(file); }
        catch (Exception) { throw new InvalidOperationException("That file is not a NEX-Audio show file."); }
        using (zip)
        {
            var me = zip.GetEntry("manifest.json") ?? throw new InvalidOperationException("That file is not a NEX-Audio show file.");
            var manifest = Parse<ShowManifest>(me, "manifest.json");
            if (manifest.Format != ShowManifest.FormatName) throw new InvalidOperationException("That file is not a NEX-Audio show file.");
            if (manifest.Version > ShowManifest.CurrentVersion)
                throw new InvalidOperationException("That show file was saved by a newer NEX-Audio. Update NEX-Audio on this PC first.");

            bool hasMix = false;
            int channels = 0, buses = 0, scenes = 0, snaps = 0, presets = 0, cues = 0, macros = 0;
            long total = 0;
            foreach (var e in zip.Entries)
            {
                if (e.FullName == "manifest.json" || e.FullName.EndsWith('/')) continue;
                if (e.FullName == "snapshots/order.json")
                {
                    Copy(e, extractTo);
                    continue;
                }
                if (!AllowedEntry().IsMatch(e.FullName)) continue; // anything else is left out
                total += e.Length;
                if (total > 512L * 1024 * 1024) throw new InvalidOperationException("That show file is far too big to be a NEX-Audio show.");
                switch (e.FullName.Split('/')[0])
                {
                    case "mix.json":
                    {
                        using var r = new StreamReader(e.Open());
                        var mix = MixState.FromJson(r.ReadToEnd()) ?? throw new InvalidOperationException("The mix in that show file is damaged.");
                        hasMix = true;
                        channels = mix.Channels.Count;
                        buses = mix.Buses.Count;
                        break;
                    }
                    case "show.json":
                    {
                        var s = Parse<ShowState>(e, "the cue list");
                        s.Repair();
                        cues = s.Cues.Count;
                        macros = s.Macros.Count;
                        break;
                    }
                    case "scenes":
                        Parse<SavedMix>(e, "a scene");
                        scenes++;
                        break;
                    case "snapshots":
                        Parse<SavedMix>(e, "a snapshot");
                        snaps++;
                        break;
                    case "presets":
                        Parse<ChannelPreset>(e, "a preset");
                        presets++;
                        break;
                }
                Copy(e, extractTo);
            }
            return new ShowContents(manifest, hasMix, channels, buses, scenes, snaps, presets, cues, macros);
        }
    }

    private static T Parse<T>(ZipArchiveEntry e, string what)
    {
        try
        {
            using var s = e.Open();
            return JsonSerializer.Deserialize<T>(s, Json) ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            throw new InvalidOperationException($"Part of that show file ({what}) is damaged, so nothing was changed.");
        }
    }

    private static void Copy(ZipArchiveEntry e, string? root)
    {
        if (root is null) return;
        var dst = Path.GetFullPath(Path.Combine(root, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
        if (!dst.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        e.ExtractToFile(dst, true);
    }

    // ---- Backups ---------------------------------------------------------------------------------

    public static string BackupDir(string dataDir) => Path.Combine(dataDir, "backups");

    /// <summary>Saves a backup of the whole desk and keeps the newest 40.</summary>
    public static string Backup(string dataDir, string reason, string by)
    {
        var dir = BackupDir(dataDir);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss} {reason}{Extension}");
        Export(dataDir, file, reason, by);
        foreach (var old in Directory.EnumerateFiles(dir, "*" + Extension).OrderByDescending(File.GetLastWriteTimeUtc).Skip(40))
            try { File.Delete(old); } catch (Exception) { }
        return file;
    }

    public static IReadOnlyList<BackupInfo> Backups(string dataDir)
    {
        var dir = BackupDir(dataDir);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*" + Extension)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f =>
            {
                var n = Path.GetFileNameWithoutExtension(f.Name);
                var reason = n.Length > 16 ? n[16..] : "";
                return new BackupInfo(f.Name, n, f.LastWriteTimeUtc, f.Length, reason);
            })
            .ToList();
    }

    /// <summary>The full path of a backup by its file name (never outside the backups folder).</summary>
    public static string? BackupPath(string dataDir, string name)
    {
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return null;
        var f = Path.Combine(BackupDir(dataDir), name);
        return File.Exists(f) ? f : null;
    }
}
