using System.Text.RegularExpressions;
using Nexora.Audio.Dsp;
using Nexora.Audio.Record;

namespace Nexora.Audio.Drums;

/// <summary>One sample in the drum library, as the screens list it.</summary>
public sealed record DrumSampleInfo(string Path, string Kit, string Name, double Seconds);

/// <summary>
/// The drum library: WAV samples in folders, one folder per kit ("Starter", "Sunday kit"), kept with the desk's
/// other files so a show file carries them to another PC. A trigger names a sample as "Kit/Name.wav".
/// Samples are read and turned into the desk's sample rate here, on the control thread, never on the audio thread.
/// </summary>
public sealed partial class DrumLibrary
{
    /// <summary>The longest sample kept: a drum hit with a long tail is still well under this.</summary>
    public const double MaxSeconds = 8;
    public const string StarterKit = "Starter";

    private readonly string _root;
    private readonly Dictionary<string, (DateTime Stamp, double Rate, DrumSample Sample)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public DrumLibrary(string dataDir)
    {
        _root = Path.Combine(dataDir, "drums");
    }

    public string Root => _root;

    [GeneratedRegex(@"^[A-Za-z0-9 _()'&+,-]{1,40}$")]
    private static partial Regex SafeName();

    /// <summary>A sample's place in the library, checked so it can never point outside it. Null when it is not a library path.</summary>
    public string? FullPath(string path)
    {
        var parts = (path ?? "").Replace('\\', '/').Split('/');
        if (parts.Length != 2 || !SafeName().IsMatch(parts[0])) return null;
        var file = parts[1];
        if (!file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !SafeName().IsMatch(file[..^4])) return null;
        return Path.Combine(_root, parts[0], file);
    }

    public bool Exists(string path) => FullPath(path) is { } f && File.Exists(f);

    /// <summary>
    /// A short fingerprint of the trigger's files (which exist, and when they changed), so a sample added, replaced
    /// or brought back by opening a show is picked up without touching the trigger's settings.
    /// </summary>
    public string Stamp(Mix.TriggerSettings t)
    {
        if (!t.On) return "";
        var h = new System.Text.StringBuilder();
        foreach (var p in t.AllSamples())
        {
            var f = FullPath(p);
            h.Append(f is not null && File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks : 0).Append(';');
        }
        return h.ToString();
    }

    /// <summary>Every sample, kit by kit.</summary>
    public IReadOnlyList<DrumSampleInfo> List()
    {
        var list = new List<DrumSampleInfo>();
        if (!Directory.Exists(_root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(_root).Order(StringComparer.OrdinalIgnoreCase))
        {
            var kit = Path.GetFileName(dir);
            if (!SafeName().IsMatch(kit)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.wav").Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(f);
                if (!SafeName().IsMatch(name[..^4])) continue;
                double secs = 0;
                try
                {
                    using var r = new WavReader(f);
                    secs = r.Frames / (double)r.SampleRate;
                }
                catch (Exception) { continue; }
                list.Add(new DrumSampleInfo($"{kit}/{name}", kit, name[..^4], secs));
            }
        }
        return list;
    }

    /// <summary>
    /// Reads a sample, as mono at this sample rate. Null when the file is missing or cannot be read
    /// (the trigger then lets the natural mic through).
    /// </summary>
    public DrumSample? Load(string path, double sampleRate)
    {
        if (FullPath(path) is not { } f || !File.Exists(f)) return null;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(f); }
        catch (Exception) { return null; }
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var c) && c.Stamp == stamp && c.Rate == sampleRate) return c.Sample;
        }
        try
        {
            var data = Read(f, sampleRate);
            var smp = new DrumSample(path, data);
            lock (_gate) _cache[path] = (stamp, sampleRate, smp);
            return smp;
        }
        catch (Exception) { return null; }
    }

    /// <summary>A WAV file as mono floats at the given rate (any channels and rate in; linear interpolation).</summary>
    public static float[] Read(string file, double sampleRate)
    {
        using var r = new WavReader(file);
        var max = (long)(MaxSeconds * r.SampleRate);
        var frames = (int)Math.Min(r.Frames, max);
        var mono = new float[frames];
        var buf = new float[4096 * r.Channels];
        var at = 0;
        while (at < frames)
        {
            var got = r.Read(buf.AsSpan(0, Math.Min(buf.Length, (frames - at) * r.Channels)));
            if (got == 0) break;
            for (var i = 0; i < got; i++)
            {
                var sum = 0f;
                for (var ch = 0; ch < r.Channels; ch++) sum += buf[i * r.Channels + ch];
                mono[at + i] = sum / r.Channels;
            }
            at += got;
        }
        if (at < frames) Array.Resize(ref mono, at);
        if (Math.Abs(r.SampleRate - sampleRate) < 0.5) return mono;
        var ratio = r.SampleRate / sampleRate;
        var outLen = (int)(mono.Length / ratio);
        var res = new float[Math.Max(0, outLen)];
        for (var i = 0; i < res.Length; i++)
        {
            var p = i * ratio;
            var ip = (int)p;
            var fr = (float)(p - ip);
            var a = mono[Math.Min(ip, mono.Length - 1)];
            var b = mono[Math.Min(ip + 1, mono.Length - 1)];
            res[i] = a + (b - a) * fr;
        }
        return res;
    }

    /// <summary>Copies a WAV file into a kit (made if needed). Returns its library path. Throws with a plain message.</summary>
    public string Import(string file, string kit)
    {
        kit = Clean(kit);
        if (kit.Length == 0) kit = "My samples";
        if (!File.Exists(file)) throw new InvalidOperationException("That sample file could not be found.");
        try
        {
            using var r = new WavReader(file);
            if (r.Frames == 0) throw new InvalidDataException("This WAV file has no sound in it.");
        }
        catch (InvalidDataException e) { throw new InvalidOperationException(e.Message); }
        catch (IOException) { throw new InvalidOperationException("That file is not a WAV file NEX-Audio can play."); }
        var name = Clean(Path.GetFileNameWithoutExtension(file));
        if (name.Length == 0) name = "Sample";
        var dir = Path.Combine(_root, kit);
        Directory.CreateDirectory(dir);
        var dst = Path.Combine(dir, name + ".wav");
        for (var i = 2; File.Exists(dst) && !SameFile(dst, file); i++) dst = Path.Combine(dir, $"{name} {i}.wav");
        if (!SameFile(dst, file)) File.Copy(file, dst, true);
        return $"{kit}/{Path.GetFileName(dst)}";
    }

    private static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    public bool Delete(string path)
    {
        if (FullPath(path) is not { } f || !File.Exists(f)) return false;
        File.Delete(f);
        lock (_gate) _cache.Remove(path);
        var dir = Path.GetDirectoryName(f)!;
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        return true;
    }

    /// <summary>A name made safe for a folder or file (letters, numbers, spaces and a few marks; at most 40).</summary>
    public static string Clean(string? name)
    {
        var s = new string((name ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || " _()'&+,-".Contains(c)).ToArray()).Trim();
        return s.Length > 40 ? s[..40].Trim() : s;
    }

    /// <summary>Writes the starter kit if it is not there yet (made by NEX-Audio itself, so it is free to use anywhere).</summary>
    public void EnsureStarter()
    {
        var dir = Path.Combine(_root, StarterKit);
        if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.wav").Any()) return;
        Directory.CreateDirectory(dir);
        foreach (var (name, data) in Drums.StarterKit.Make(48000))
        {
            var tmp = Path.Combine(dir, name + ".tmp");
            using (var w = new WavWriter(tmp, 48000, 1, RecordFormat.Pcm24)) w.Write(data);
            File.Move(tmp, Path.Combine(dir, name + ".wav"), true);
        }
    }
}
