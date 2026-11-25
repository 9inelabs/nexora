using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Nexora.App.Output;
using Nexora.Core.Config;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.App.Services;

/// <summary>
/// The Live Output PC's media list: videos and pictures copied into its own folder (so a network drop
/// cannot stop them), each checked first for length, size and whether it will play.
/// </summary>
public static class MediaLibrary
{
    private static string ListFile => Path.Combine(AppPaths.VideosDir, "media-list.json");
    private static readonly string[] VideoExt = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".wmv", ".webm", ".mpg", ".mpeg", ".ts", ".mts", ".flv"];
    private static readonly string[] ImageExt = [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif"];

    public static IReadOnlyList<string> Extensions => [.. VideoExt, .. ImageExt];

    public static List<MediaInfo> Load()
    {
        try
        {
            if (File.Exists(ListFile) && JsonSerializer.Deserialize<List<MediaInfo>>(File.ReadAllText(ListFile), Wire.Options) is { } list)
                return list.Where(m => File.Exists(Path.Combine(AppPaths.VideosDir, m.File))).ToList();
        }
        catch (Exception ex) { AppLog.Write("Media list: " + ex.Message); }
        return [];
    }

    public static void Save(IEnumerable<MediaInfo> list)
    {
        try
        {
            var tmp = ListFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list.ToList(), Wire.Options));
            File.Move(tmp, ListFile, overwrite: true);
        }
        catch (Exception ex) { AppLog.Write("Media list: " + ex.Message); }
    }

    /// <summary>Copies a file into the media folder and checks it. Slow for big videos: call off the UI thread.</summary>
    public static MediaInfo Add(string source)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();
        var kind = ImageExt.Contains(ext) ? MediaKind.Image : MediaKind.Video;
        var name = SafeName(Path.GetFileNameWithoutExtension(source)) + ext;
        var target = Path.Combine(AppPaths.VideosDir, name);
        for (var n = 2; File.Exists(target) && !SameFile(source, target); n++)
        {
            name = $"{SafeName(Path.GetFileNameWithoutExtension(source))} ({n}){ext}";
            target = Path.Combine(AppPaths.VideosDir, name);
        }
        if (!File.Exists(target)) File.Copy(source, target);
        return Check(name, kind);
    }

    private static bool SameFile(string a, string b)
    {
        try { return new FileInfo(a).Length == new FileInfo(b).Length; }
        catch { return false; }
    }

    private static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Where(c => !bad.Contains(c) && c != '/' && c != '\\').ToArray()).Trim();
        return clean.Length == 0 ? "media" : clean.Length > 80 ? clean[..80] : clean;
    }

    public static void Remove(MediaInfo m)
    {
        foreach (var f in new[] { m.File, m.Thumbnail })
        {
            if (string.IsNullOrEmpty(f)) continue;
            try { File.Delete(Path.Combine(AppPaths.VideosDir, f)); } catch { }
        }
    }

    /// <summary>Length, picture size and codec from ffprobe, and a thumbnail from ffmpeg.</summary>
    public static MediaInfo Check(string file, MediaKind kind)
    {
        var path = Path.Combine(AppPaths.VideosDir, file);
        var title = Path.GetFileNameWithoutExtension(file);
        if (kind == MediaKind.Image)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var bmp = new Avalonia.Media.Imaging.Bitmap(fs);
                return new MediaInfo(file, title, kind, 0, bmp.PixelSize.Width, bmp.PixelSize.Height, null, null, file);
            }
            catch (Exception ex)
            {
                return new MediaInfo(file, title, kind, 0, 0, 0, null, "This picture cannot be opened: " + ex.Message, null);
            }
        }

        double duration = 0;
        int w = 0, h = 0;
        string? codec = null, problem = null, thumb = null;
        var ffmpeg = Recorder.FfmpegPath;
        var probe = ffmpeg is null ? null : Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (probe is not null && File.Exists(probe))
        {
            var json = Run(probe, ["-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,width,height", "-of", "json", path], 20000);
            try
            {
                using var doc = JsonDocument.Parse(json ?? "{}");
                if (doc.RootElement.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d))
                    double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                if (doc.RootElement.TryGetProperty("streams", out var streams))
                    foreach (var s in streams.EnumerateArray())
                        if (s.TryGetProperty("codec_type", out var t) && t.GetString() == "video" && w == 0)
                        {
                            codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                            w = s.TryGetProperty("width", out var ww) ? ww.GetInt32() : 0;
                            h = s.TryGetProperty("height", out var hh) ? hh.GetInt32() : 0;
                        }
                if (w == 0) problem = "No picture found in this file. It may be damaged or audio only.";
                else if (duration <= 0) problem = "The length could not be read; it may still play.";
            }
            catch (Exception) { problem = "This file could not be checked. Try it in rehearsal first."; }

            if (w > 0)
            {
                thumb = Path.GetFileNameWithoutExtension(file) + ".thumb.jpg";
                var at = duration > 6 ? Math.Min(5, duration / 3) : 0;
                Run(ffmpeg!, ["-v", "error", "-y", "-ss", at.ToString("0.0", CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1", "-vf", "scale=480:-2", Path.Combine(AppPaths.VideosDir, thumb)], 20000);
                if (!File.Exists(Path.Combine(AppPaths.VideosDir, thumb))) thumb = null;
            }
        }
        else problem = "This file was not checked (the checker is missing). Try it in rehearsal first.";
        return new MediaInfo(file, title, MediaKind.Video, duration, w, h, codec, problem, thumb);
    }

    private static string? Run(string exe, IEnumerable<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            return outTask.Result;
        }
        catch (Exception ex)
        {
            AppLog.Write("Media check: " + ex.Message);
            return null;
        }
    }
}
