using System.Text.Json;

namespace Nexora.Audio.Mix;

/// <summary>A saved channel sound: filters, drive, EQ, dynamic EQ, gate, compressor and effects.</summary>
public sealed class ChannelPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..10];
    public string Name { get; set; } = "";
    /// <summary>Built-in starting points that come with NEX-Audio (they cannot be deleted).</summary>
    public bool BuiltIn { get; set; }
    /// <summary>The folder in the preset library ("" for none), for example "Voices" or "Sunday band".</summary>
    public string Folder { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public Strip Sound { get; set; } = new();
}

public sealed record PresetInfo(string Id, string Name, bool BuiltIn)
{
    public string Folder { get; init; } = "";
    public DateTime SavedUtc { get; init; }

    /// <summary>True when every word of the search is in the name or the folder (any case).</summary>
    public bool Matches(string? search) =>
        string.IsNullOrWhiteSpace(search) ||
        search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(w => Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) || Folder.Contains(w, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>Channel presets, kept as files on the NEX-Audio PC, plus a few starting points of our own.</summary>
public sealed class PresetStore
{
    private readonly string _dir;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(MixState.Json) { WriteIndented = true };

    public PresetStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "presets");
        Directory.CreateDirectory(_dir);
    }

    public IReadOnlyList<PresetInfo> List()
    {
        var list = BuiltIns.Select(Info).ToList();
        lock (_gate)
            foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<ChannelPreset>(File.ReadAllText(f), Json);
                    if (p is not null) list.Add(Info(p) with { BuiltIn = false });
                }
                catch (Exception) { }
            }
        return list.OrderBy(p => !p.BuiltIn).ThenBy(p => p.Folder, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static PresetInfo Info(ChannelPreset p) => new(p.Id, p.Name, p.BuiltIn) { Folder = p.Folder, SavedUtc = p.SavedUtc };

    /// <summary>Every folder in use, built-in ones first.</summary>
    public IReadOnlyList<string> Folders() =>
        List().Where(p => p.Folder.Length > 0).GroupBy(p => p.Folder, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => !g.Any(p => p.BuiltIn)).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First().Folder).ToList();

    /// <summary>A folder name as it is kept: trimmed, at most 30 characters, no slashes.</summary>
    public static string CleanFolder(string? folder)
    {
        var f = (folder ?? "").Replace('/', ' ').Replace('\\', ' ').Trim();
        return f.Length > 30 ? f[..30].Trim() : f;
    }

    public ChannelPreset? Load(string id)
    {
        if (BuiltIns.FirstOrDefault(p => p.Id == id) is { } b) return b;
        if (!SafeId(id)) return null;
        lock (_gate)
        {
            try
            {
                var f = Path.Combine(_dir, id + ".json");
                return File.Exists(f) ? JsonSerializer.Deserialize<ChannelPreset>(File.ReadAllText(f), Json) : null;
            }
            catch (Exception) { return null; }
        }
    }

    public ChannelPreset Save(string name, Strip from, string? folder = null)
    {
        var p = new ChannelPreset
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Preset" : name.Trim(), Sound = Sound(from), Folder = CleanFolder(folder), SavedUtc = DateTime.UtcNow,
        };
        if (p.Name.Length > 40) p.Name = p.Name[..40];
        Write(p);
        return p;
    }

    /// <summary>Moves one of your own presets to another folder ("" for none), and renames it when a name is given.</summary>
    public bool Move(string id, string? folder, string? name = null)
    {
        if (!SafeId(id) || BuiltIns.Any(p => p.Id == id)) return false;
        var p = Load(id);
        if (p is null) return false;
        p.Folder = CleanFolder(folder);
        if (!string.IsNullOrWhiteSpace(name)) p.Name = name.Trim().Length > 40 ? name.Trim()[..40] : name.Trim();
        Write(p);
        return true;
    }

    private void Write(ChannelPreset p)
    {
        lock (_gate)
        {
            var f = Path.Combine(_dir, p.Id + ".json");
            File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(p, Json));
            File.Move(f + ".tmp", f, true);
        }
    }

    public bool Delete(string id)
    {
        if (!SafeId(id) || BuiltIns.Any(p => p.Id == id)) return false;
        lock (_gate)
        {
            var f = Path.Combine(_dir, id + ".json");
            if (!File.Exists(f)) return false;
            File.Delete(f);
            return true;
        }
    }

    /// <summary>Just the sound of a strip (no name, level, routing or sends).</summary>
    private static Strip Sound(Strip s)
    {
        var x = new Strip { Kind = s.Kind };
        x.TakeProcessing(s, newIds: true);
        x.Gate.Key = "";
        x.Comp.Key = "";
        x.Trigger.Key = "";
        return x;
    }

    private static bool SafeId(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Starting points written for NEX-Audio (our own settings, meant to be adjusted by ear).</summary>
    public static readonly IReadOnlyList<ChannelPreset> BuiltIns =
    [
        Make("speech", "Voices", "Speech: pastor or reader", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 100, Slope = 24 };
            s.Eq.Bands[1].Gain = -2; s.Eq.Bands[1].Freq = 300;
            s.Eq.Bands[3].Gain = 2; s.Eq.Bands[3].Freq = 3000;
            s.DynEq.Bands[0].On = true; s.DynEq.Bands[0].Freq = 220; s.DynEq.Bands[0].Range = -4;
            s.Comp = new CompSettings { On = true, Style = CompStyle.Opto, Threshold = -24, Ratio = 3, Attack = 15, Release = 200, Knee = 8, AutoMakeup = true };
        }),
        Make("leadvox", "Voices", "Lead vocal", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 90, Slope = 24 };
            s.Eq.Bands[1].Gain = -2.5; s.Eq.Bands[1].Freq = 280;
            s.Eq.Bands[4].Gain = 2; s.Eq.Bands[4].Freq = 5000;
            s.DynEq.Bands[1].On = true; s.DynEq.Bands[1].Freq = 3200; s.DynEq.Bands[1].Range = -5;
            s.Drive = new DriveSettings { On = true, Style = DriveStyle.Warm, Drive = 4, Mix = 60 };
            s.Comp = new CompSettings { On = true, Style = CompStyle.Clean, Threshold = -22, Ratio = 4, Attack = 5, Release = 120, Knee = 6, AutoMakeup = true };
            s.Rack.Add(Effects.Default("deesser"));
        }),
        Make("choir", "Voices", "Choir mic", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 140, Slope = 24 };
            s.Eq.Bands[1].Gain = -3; s.Eq.Bands[1].Freq = 350;
            s.Eq.Bands[5].Gain = 2; s.Eq.Bands[5].Freq = 10000;
            s.Comp = new CompSettings { On = true, Style = CompStyle.Glue, Threshold = -20, Ratio = 2, Attack = 30, Release = 250, Knee = 10, AutoMakeup = true };
        }),
        Make("kick", "Drums", "Kick drum", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 35, Slope = 12 };
            s.Eq.Bands[0].Gain = 3; s.Eq.Bands[0].Freq = 70;
            s.Eq.Bands[2].Gain = -4; s.Eq.Bands[2].Freq = 400; s.Eq.Bands[2].Q = 1.5;
            s.Eq.Bands[3].Gain = 3; s.Eq.Bands[3].Freq = 3500;
            s.Gate = new GateSettings { On = true, Threshold = -35, Range = 25, Attack = 0.3, Hold = 60, Release = 120, KeyFilter = 60 };
            s.Comp = new CompSettings { On = true, Style = CompStyle.Fet, Threshold = -16, Ratio = 4, Attack = 15, Release = 80, Knee = 4 };
        }),
        Make("snare", "Drums", "Snare drum", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 90, Slope = 12 };
            s.Eq.Bands[2].Gain = -2; s.Eq.Bands[2].Freq = 700;
            s.Eq.Bands[4].Gain = 3; s.Eq.Bands[4].Freq = 6000;
            s.Gate = new GateSettings { On = true, Threshold = -30, Range = 20, Attack = 0.3, Hold = 40, Release = 150, KeyFilter = 150 };
            s.Comp = new CompSettings { On = true, Style = CompStyle.Fet, Threshold = -18, Ratio = 3, Attack = 8, Release = 100, Knee = 4 };
        }),
        Make("trig-kick", "Drums", "Kick drum, triggered (starter kit)", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 35, Slope = 12 };
            s.Trigger = Triggered("Kick", -26, 60, 40, 3000, 85);
            s.Comp = new CompSettings { On = true, Style = CompStyle.Fet, Threshold = -14, Ratio = 3, Attack = 15, Release = 80, Knee = 4 };
        }),
        Make("trig-snare", "Drums", "Snare drum, triggered (starter kit)", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 90, Slope = 12 };
            s.Trigger = Triggered("Snare", -24, 40, 150, 4000, 70);
            s.Comp = new CompSettings { On = true, Style = CompStyle.Fet, Threshold = -16, Ratio = 3, Attack = 8, Release = 100, Knee = 4 };
        }),
        Make("trig-racktom", "Drums", "Rack tom, triggered (starter kit)", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 70, Slope = 12 };
            s.Trigger = Triggered("Rack tom", -22, 60, 80, 2500, 90);
        }),
        Make("trig-floortom", "Drums", "Floor tom, triggered (starter kit)", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 50, Slope = 12 };
            s.Trigger = Triggered("Floor tom", -22, 70, 60, 2000, 90);
        }),
        Make("acoustic", "Instruments", "Acoustic guitar", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 100, Slope = 24 };
            s.DynEq.Bands[0].On = true; s.DynEq.Bands[0].Freq = 200; s.DynEq.Bands[0].Range = -5;
            s.Eq.Bands[5].Gain = 2.5; s.Eq.Bands[5].Freq = 11000;
            s.Comp = new CompSettings { On = true, Style = CompStyle.Opto, Threshold = -22, Ratio = 3, Attack = 20, Release = 200, Knee = 8, AutoMakeup = true };
        }),
        Make("keys", "Instruments", "Keys and piano", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 60, Slope = 12 };
            s.Eq.Bands[1].Gain = -1.5; s.Eq.Bands[1].Freq = 300;
            s.Comp = new CompSettings { On = true, Style = CompStyle.Glue, Threshold = -18, Ratio = 2, Attack = 30, Release = 200, Knee = 10, AutoMakeup = true };
        }),
        Make("bass", "Instruments", "Bass guitar (DI)", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 35, Slope = 24 };
            s.Eq.Bands[1].Gain = -2; s.Eq.Bands[1].Freq = 250;
            s.Drive = new DriveSettings { On = true, Style = DriveStyle.Warm, Drive = 8, Mix = 50 };
            s.Comp = new CompSettings { On = true, Style = CompStyle.Opto, Threshold = -20, Ratio = 4, Attack = 20, Release = 180, Knee = 6, AutoMakeup = true };
        }),
        Make("ducked", "Music and playback", "Music under the pastor (ducker)", s =>
        {
            s.Gate = new GateSettings { On = true, Mode = GateMode.Ducker, Threshold = -40, Range = 10, Attack = 20, Hold = 400, Release = 800 };
        }),
    ];

    /// <summary>A trigger on one drum of the starter kit, all four layers with two takes each.</summary>
    private static TriggerSettings Triggered(string drum, double threshold, double hold, double low, double high, double blend) => new()
    {
        On = true, Threshold = threshold, Hold = hold, FocusLow = low, FocusHigh = high, Blend = blend,
        Layers = Drums.StarterKit.Layers(drum).Select(l => new TriggerLayer { Samples = l }).ToList(),
    };

    private static ChannelPreset Make(string id, string folder, string name, Action<Strip> set)
    {
        var s = new Strip { Kind = StripKind.Channel };
        set(s);
        return new ChannelPreset { Id = "builtin-" + id, Name = name, BuiltIn = true, Folder = folder, Sound = s };
    }
}
