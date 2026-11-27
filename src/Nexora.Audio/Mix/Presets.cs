using System.Text.Json;

namespace Nexora.Audio.Mix;

/// <summary>A saved channel sound: filters, drive, EQ, dynamic EQ, gate, compressor and effects.</summary>
public sealed class ChannelPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..10];
    public string Name { get; set; } = "";
    /// <summary>Built-in starting points that come with NEX-Audio (they cannot be deleted).</summary>
    public bool BuiltIn { get; set; }
    public Strip Sound { get; set; } = new();
}

public sealed record PresetInfo(string Id, string Name, bool BuiltIn);

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
        var list = BuiltIns.Select(p => new PresetInfo(p.Id, p.Name, true)).ToList();
        lock (_gate)
            foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<ChannelPreset>(File.ReadAllText(f), Json);
                    if (p is not null) list.Add(new PresetInfo(p.Id, p.Name, false));
                }
                catch (Exception) { }
            }
        return list.OrderBy(p => !p.BuiltIn).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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

    public ChannelPreset Save(string name, Strip from)
    {
        var p = new ChannelPreset { Name = string.IsNullOrWhiteSpace(name) ? "Preset" : name.Trim(), Sound = Sound(from) };
        if (p.Name.Length > 40) p.Name = p.Name[..40];
        lock (_gate)
        {
            var f = Path.Combine(_dir, p.Id + ".json");
            File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(p, Json));
            File.Move(f + ".tmp", f, true);
        }
        return p;
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
        return x;
    }

    private static bool SafeId(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Starting points written for NEX-Audio (our own settings, meant to be adjusted by ear).</summary>
    public static readonly IReadOnlyList<ChannelPreset> BuiltIns =
    [
        Make("speech", "Speech: pastor or reader", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 100, Slope = 24 };
            s.Eq.Bands[1].Gain = -2; s.Eq.Bands[1].Freq = 300;
            s.Eq.Bands[3].Gain = 2; s.Eq.Bands[3].Freq = 3000;
            s.DynEq.Bands[0].On = true; s.DynEq.Bands[0].Freq = 220; s.DynEq.Bands[0].Range = -4;
            s.Comp = new CompSettings { On = true, Style = CompStyle.Opto, Threshold = -24, Ratio = 3, Attack = 15, Release = 200, Knee = 8, AutoMakeup = true };
        }),
        Make("leadvox", "Lead vocal", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 90, Slope = 24 };
            s.Eq.Bands[1].Gain = -2.5; s.Eq.Bands[1].Freq = 280;
            s.Eq.Bands[4].Gain = 2; s.Eq.Bands[4].Freq = 5000;
            s.DynEq.Bands[1].On = true; s.DynEq.Bands[1].Freq = 3200; s.DynEq.Bands[1].Range = -5;
            s.Drive = new DriveSettings { On = true, Style = DriveStyle.Warm, Drive = 4, Mix = 60 };
            s.Comp = new CompSettings { On = true, Style = CompStyle.Clean, Threshold = -22, Ratio = 4, Attack = 5, Release = 120, Knee = 6, AutoMakeup = true };
            s.Rack.Add(Effects.Default("deesser"));
        }),
        Make("choir", "Choir mic", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 140, Slope = 24 };
            s.Eq.Bands[1].Gain = -3; s.Eq.Bands[1].Freq = 350;
            s.Eq.Bands[5].Gain = 2; s.Eq.Bands[5].Freq = 10000;
            s.Comp = new CompSettings { On = true, Style = CompStyle.Glue, Threshold = -20, Ratio = 2, Attack = 30, Release = 250, Knee = 10, AutoMakeup = true };
        }),
        Make("kick", "Kick drum", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 35, Slope = 12 };
            s.Eq.Bands[0].Gain = 3; s.Eq.Bands[0].Freq = 70;
            s.Eq.Bands[2].Gain = -4; s.Eq.Bands[2].Freq = 400; s.Eq.Bands[2].Q = 1.5;
            s.Eq.Bands[3].Gain = 3; s.Eq.Bands[3].Freq = 3500;
            s.Gate = new GateSettings { On = true, Threshold = -35, Range = 25, Attack = 0.3, Hold = 60, Release = 120, KeyFilter = 60 };
            s.Comp = new CompSettings { On = true, Style = CompStyle.Fet, Threshold = -16, Ratio = 4, Attack = 15, Release = 80, Knee = 4 };
        }),
        Make("snare", "Snare drum", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 90, Slope = 12 };
            s.Eq.Bands[2].Gain = -2; s.Eq.Bands[2].Freq = 700;
            s.Eq.Bands[4].Gain = 3; s.Eq.Bands[4].Freq = 6000;
            s.Gate = new GateSettings { On = true, Threshold = -30, Range = 20, Attack = 0.3, Hold = 40, Release = 150, KeyFilter = 150 };
            s.Comp = new CompSettings { On = true, Style = CompStyle.Fet, Threshold = -18, Ratio = 3, Attack = 8, Release = 100, Knee = 4 };
        }),
        Make("acoustic", "Acoustic guitar", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 100, Slope = 24 };
            s.DynEq.Bands[0].On = true; s.DynEq.Bands[0].Freq = 200; s.DynEq.Bands[0].Range = -5;
            s.Eq.Bands[5].Gain = 2.5; s.Eq.Bands[5].Freq = 11000;
            s.Comp = new CompSettings { On = true, Style = CompStyle.Opto, Threshold = -22, Ratio = 3, Attack = 20, Release = 200, Knee = 8, AutoMakeup = true };
        }),
        Make("keys", "Keys and piano", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 60, Slope = 12 };
            s.Eq.Bands[1].Gain = -1.5; s.Eq.Bands[1].Freq = 300;
            s.Comp = new CompSettings { On = true, Style = CompStyle.Glue, Threshold = -18, Ratio = 2, Attack = 30, Release = 200, Knee = 10, AutoMakeup = true };
        }),
        Make("bass", "Bass guitar (DI)", s =>
        {
            s.Hpf = new PassFilter { On = true, Freq = 35, Slope = 24 };
            s.Eq.Bands[1].Gain = -2; s.Eq.Bands[1].Freq = 250;
            s.Drive = new DriveSettings { On = true, Style = DriveStyle.Warm, Drive = 8, Mix = 50 };
            s.Comp = new CompSettings { On = true, Style = CompStyle.Opto, Threshold = -20, Ratio = 4, Attack = 20, Release = 180, Knee = 6, AutoMakeup = true };
        }),
        Make("ducked", "Music under the pastor (ducker)", s =>
        {
            s.Gate = new GateSettings { On = true, Mode = GateMode.Ducker, Threshold = -40, Range = 10, Attack = 20, Hold = 400, Release = 800 };
        }),
    ];

    private static ChannelPreset Make(string id, string name, Action<Strip> set)
    {
        var s = new Strip { Kind = StripKind.Channel };
        set(s);
        return new ChannelPreset { Id = "builtin-" + id, Name = name, BuiltIn = true, Sound = s };
    }
}
