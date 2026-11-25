using System.IO.Compression;
using System.Text.Json;
using Nexora.Core.Config;
using Nexora.Core.Protocol;
using Nexora.Server;

namespace Nexora.App.Services;

/// <summary>
/// Puts a NEXORA backup in place (restore), and keeps a recent copy of the Live Output PC's library on
/// each terminal so a terminal can take over as Live Output if that PC fails.
/// </summary>
public static class Backups
{
    /// <summary>Replaces this PC's library with the one in the backup. The server must be stopped.</summary>
    /// <param name="becomeLiveOutput">True when a terminal takes over: it also takes the old Live Output PC's pairing code.</param>
    public static void Restore(string zipPath, bool becomeLiveOutput = false)
    {
        if (!LiveServer.IsBackup(zipPath)) throw new InvalidDataException("That file is not a NEXORA backup.");
        var root = AppPaths.Root;
        var libDir = Path.Combine(root, "library");

        // Keep what was there, so a bad backup can be undone by hand.
        var undo = Path.Combine(AppPaths.BackupsDir, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}");
        if (Directory.Exists(libDir))
        {
            try { CopyDir(libDir, undo); } catch (Exception ex) { AppLog.Write("Restore: could not keep the old library: " + ex.Message); }
        }

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/') || e.Name.Length == 0) continue;
            if (e.FullName.Contains("..")) continue;
            if (e.FullName.StartsWith("library/"))
            {
                var target = Path.GetFullPath(Path.Combine(root, e.FullName));
                if (!target.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, overwrite: true);
            }
        }
        // SQLite side files from the old database would confuse the restored one.
        foreach (var side in new[] { "-wal", "-shm" })
        {
            var f = AppPaths.LibraryFile + side;
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        var cfg = ThisPcConfig.Load();
        if (zip.GetEntry("live-output-pc.json") is { } pcEntry)
        {
            try
            {
                using var r = new StreamReader(pcEntry.Open());
                var old = JsonSerializer.Deserialize<ThisPcConfig>(r.ReadToEnd(), Wire.Options);
                if (old is not null)
                {
                    // Terminals know the Live Output PC by its pairing code (and the server id in the library).
                    if (!string.IsNullOrWhiteSpace(old.PairingCode)) cfg.PairingCode = old.PairingCode;
                    if (becomeLiveOutput) cfg.Port = old.Port;
                }
            }
            catch (Exception ex) { AppLog.Write("Restore: " + ex.Message); }
        }
        if (becomeLiveOutput)
        {
            cfg.Role = PcRole.LiveOutput;
            cfg.ServerId = null;
            cfg.ManualAddress = null;
        }
        cfg.Save();
        AppLog.Write($"Backup restored from {Path.GetFileName(zipPath)}{(becomeLiveOutput ? " (this PC is now Live Output)" : "")}");
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    public static string StandbyFile => Path.Combine(AppPaths.StandbyDir, "live-output-copy.zip");

    public static DateTime? StandbyTime => File.Exists(StandbyFile) ? File.GetLastWriteTime(StandbyFile) : null;

    /// <summary>Downloads a fresh copy of the Live Output PC's library (terminals do this every half hour).</summary>
    public static async Task<bool> RefreshStandbyAsync(Nexora.Core.Net.NexoraClient client)
    {
        var tmp = StandbyFile + ".part";
        try
        {
            if (!await client.DownloadPathAsync("library/promote", tmp)) return false;
            if (!LiveServer.IsBackup(tmp)) return false;
            File.Move(tmp, StandbyFile, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("Standby copy: " + ex.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
