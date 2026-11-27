using System.Text.Json;
using System.Text.Json.Serialization;
using Nexora.Audio.Dsp;

namespace Nexora.Audio.Mix;

public enum StripKind
{
    /// <summary>One of the 32 X32 inputs.</summary>
    Channel,
    /// <summary>A reverb, delay or other shared effect: channels send to it, its return feeds the mix.</summary>
    Fx,
    /// <summary>Drums, vocals, band, choir: channels go through it on their way to the main mix.</summary>
    Group,
    /// <summary>A wedge, in-ear or side fill mix.</summary>
    Mix,
    /// <summary>The main left and right for the PA.</summary>
    Main,
    /// <summary>Mono or sub, fed from the main mix.</summary>
    Mono,
    /// <summary>Lobby, overflow room, delay speakers: built from the main mix, groups and mixes.</summary>
    Matrix,
    /// <summary>The stream and recording mix, sent to the Live Output PC.</summary>
    Stream,
}

public enum CompStyle
{
    /// <summary>Clean and transparent.</summary>
    Clean,
    /// <summary>Smooth, slow to let go, like an optical compressor.</summary>
    Opto,
    /// <summary>Fast and punchy with a little colour.</summary>
    Fet,
    /// <summary>Gentle, holds a group or the whole mix together.</summary>
    Glue,
}

public enum SendTap
{
    /// <summary>After the fader: the send follows the channel's fader.</summary>
    Post,
    /// <summary>Before the fader: monitors stay put when the front-of-house fader moves.</summary>
    Pre,
}

public enum StreamSource
{
    /// <summary>The stream gets a copy of the main mix.</summary>
    Main,
    /// <summary>The stream has its own balance: every channel has its own stream level.</summary>
    Own,
}

public enum SoloMode
{
    /// <summary>Hear the channel before its fader (pre-fade listen).</summary>
    Pfl,
    /// <summary>Hear the channel after its fader and pan (after-fade listen).</summary>
    Afl,
}

public sealed class EqBand
{
    public bool On { get; set; } = true;
    public FilterShape Shape { get; set; } = FilterShape.Peak;
    public double Freq { get; set; } = 1000;
    public double Gain { get; set; }
    public double Q { get; set; } = 1.0;

    public EqBand Clone() => (EqBand)MemberwiseClone();
}

public sealed class PassFilter
{
    public bool On { get; set; }
    public double Freq { get; set; } = 80;
    /// <summary>12 or 24 dB per octave.</summary>
    public int Slope { get; set; } = 12;

    public PassFilter Clone() => (PassFilter)MemberwiseClone();
}

public sealed class EqSettings
{
    public bool On { get; set; } = true;
    public List<EqBand> Bands { get; set; } = DefaultBands();

    public static List<EqBand> DefaultBands() =>
    [
        new() { Shape = FilterShape.LowShelf, Freq = 100, Q = 0.7 },
        new() { Shape = FilterShape.Peak, Freq = 250, Q = 1.2 },
        new() { Shape = FilterShape.Peak, Freq = 800, Q = 1.2 },
        new() { Shape = FilterShape.Peak, Freq = 2500, Q = 1.2 },
        new() { Shape = FilterShape.Peak, Freq = 6000, Q = 1.2 },
        new() { Shape = FilterShape.HighShelf, Freq = 10000, Q = 0.7 },
    ];

    public bool IsFlat => !On || Bands.All(b => !b.On || Math.Abs(b.Gain) < 0.01 && b.Shape is not (FilterShape.HighPass or FilterShape.LowPass or FilterShape.Notch or FilterShape.BandPass));

    public EqSettings Clone() => new() { On = On, Bands = Bands.Select(b => b.Clone()).ToList() };
}

public sealed class GateSettings
{
    public bool On { get; set; }
    public double Threshold { get; set; } = -50;
    /// <summary>How far down the gate turns the sound when closed, in dB (positive).</summary>
    public double Range { get; set; } = 40;
    public double Attack { get; set; } = 0.5;
    public double Hold { get; set; } = 40;
    public double Release { get; set; } = 150;
    /// <summary>The gate listens through a high-pass at this frequency, so bass spill does not open it. 0 is off.</summary>
    public double KeyFilter { get; set; }

    public GateSettings Clone() => (GateSettings)MemberwiseClone();
}

public sealed class CompSettings
{
    public bool On { get; set; }
    public CompStyle Style { get; set; } = CompStyle.Clean;
    public double Threshold { get; set; } = -18;
    public double Ratio { get; set; } = 3;
    public double Attack { get; set; } = 10;
    public double Release { get; set; } = 120;
    public double Knee { get; set; } = 6;
    public double Makeup { get; set; }
    public bool AutoMakeup { get; set; }
    /// <summary>Dry and compressed blended, 0 to 100 (parallel compression).</summary>
    public double Mix { get; set; } = 100;
    /// <summary>The compressor listens through a high-pass at this frequency. 0 is off.</summary>
    public double KeyFilter { get; set; }

    public CompSettings Clone() => (CompSettings)MemberwiseClone();
}

/// <summary>One send from a strip to a bus.</summary>
public sealed class Send
{
    public string Bus { get; set; } = "";
    public bool On { get; set; } = true;
    public double Level { get; set; } = Db.Floor;
    public SendTap Tap { get; set; } = SendTap.Post;
    /// <summary>For stereo buses: -1 left to 1 right. Null follows the strip's own pan.</summary>
    public double? Pan { get; set; }

    public Send Clone() => (Send)MemberwiseClone();
}

/// <summary>One processor in a strip's rack: a NEX-Audio effect or a VST3 plugin.</summary>
public sealed class RackItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    /// <summary>The effect's type, for example "reverb" or "plugin" (see <see cref="Effects"/>).</summary>
    public string Type { get; set; } = "";
    public bool Bypass { get; set; }
    public Dictionary<string, double> Params { get; set; } = [];
    /// <summary>Text choices, for example the pitch correction key ("C") or a reverb style.</summary>
    public Dictionary<string, string> Text { get; set; } = [];
    /// <summary>For plugins: which plugin and its saved settings.</summary>
    public PluginRef? Plugin { get; set; }

    public double P(string name, double fallback) => Params.TryGetValue(name, out var v) ? v : fallback;
    public string T(string name, string fallback) => Text.TryGetValue(name, out var v) ? v : fallback;

    public RackItem Clone() => new()
    {
        Id = Id, Type = Type, Bypass = Bypass, Params = new(Params), Text = new(Text),
        Plugin = Plugin is null ? null : Plugin with { },
    };
}

public sealed record PluginRef(string Path, string ClassId, string Name, string Vendor)
{
    /// <summary>The plugin's own saved settings (base64), so they come back with the scene.</summary>
    public string? State { get; init; }
    public string? ControllerState { get; init; }
}

/// <summary>A channel or bus: everything from its input to its fader.</summary>
public sealed class Strip
{
    public string Id { get; set; } = "";
    public StripKind Kind { get; set; }
    public string Name { get; set; } = "";
    /// <summary>A colour like "#E0574F", or empty to use the X32's colour.</summary>
    public string Colour { get; set; } = "";
    public bool Stereo { get; set; }

    public double Trim { get; set; }
    public bool Invert { get; set; }
    /// <summary>A delay on this strip's input, in milliseconds (to line up a mic with a direct box).</summary>
    public double DelayMs { get; set; }

    public PassFilter Hpf { get; set; } = new();
    public PassFilter Lpf { get; set; } = new() { Freq = 18000 };
    public EqSettings Eq { get; set; } = new();
    public GateSettings Gate { get; set; } = new();
    public CompSettings Comp { get; set; } = new();
    public List<RackItem> Rack { get; set; } = [];

    /// <summary>-1 left to 1 right (balance for stereo strips).</summary>
    public double Pan { get; set; }
    public double Fader { get; set; } = Db.Floor;
    public bool Mute { get; set; }
    public bool Solo { get; set; }

    /// <summary>Goes to the main left and right (channels, effects and groups).</summary>
    public bool ToMain { get; set; } = true;
    /// <summary>The groups this channel feeds (after its fader).</summary>
    public List<string> Groups { get; set; } = [];
    /// <summary>Sends to mix, effect and matrix buses.</summary>
    public List<Send> Sends { get; set; } = [];

    /// <summary>This strip's level in the stream mix when the stream has its own balance.</summary>
    public double StreamLevel { get; set; }
    public bool StreamOn { get; set; } = true;

    /// <summary>The X32 preamp for this channel (adjusted over the network). Null leaves the desk as it is.</summary>
    public double? Preamp { get; set; }
    public bool Phantom { get; set; }

    /// <summary>Scenes and snapshots leave this strip alone.</summary>
    public bool RecallSafe { get; set; }

    /// <summary>The number shown on the strip (channel 1 to 32, Mix 1 to 16, and so on).</summary>
    [JsonIgnore]
    public int Number => int.TryParse(new string(Id.SkipWhile(c => !char.IsDigit(c)).ToArray()), out var n) ? n : 0;

    public Send? SendTo(string bus) => Sends.FirstOrDefault(s => s.Bus == bus);

    public Strip Clone() => new()
    {
        Id = Id, Kind = Kind, Name = Name, Colour = Colour, Stereo = Stereo, Trim = Trim, Invert = Invert, DelayMs = DelayMs,
        Hpf = Hpf.Clone(), Lpf = Lpf.Clone(), Eq = Eq.Clone(), Gate = Gate.Clone(), Comp = Comp.Clone(),
        Rack = Rack.Select(r => r.Clone()).ToList(), Pan = Pan, Fader = Fader, Mute = Mute, Solo = Solo, ToMain = ToMain,
        Groups = [.. Groups], Sends = Sends.Select(s => s.Clone()).ToList(), StreamLevel = StreamLevel, StreamOn = StreamOn,
        Preamp = Preamp, Phantom = Phantom, RecallSafe = RecallSafe,
    };
}

/// <summary>What one X32 card return plays, and the speaker processing on it.</summary>
public sealed class OutputPatch
{
    public int Number { get; set; }
    /// <summary>
    /// "off", "tone", "in:5" (input 5 as it arrives), "ch:5" (channel 5 after its fader), "mix1", "mix1:L",
    /// "main:L", "main:R", "mono", "mtx2", "stream:L", "stream:R", "mon:L", "mon:R" (the solo / monitor bus).
    /// </summary>
    public string Source { get; set; } = "off";
    public string Name { get; set; } = "";
    public double Level { get; set; }
    public bool Mute { get; set; }
    public bool Invert { get; set; }
    /// <summary>Delay for lining up speakers, in milliseconds.</summary>
    public double DelayMs { get; set; }
    public EqSettings Eq { get; set; } = new() { On = false, Bands = [new() { Shape = FilterShape.Peak, Freq = 125 }, new() { Shape = FilterShape.Peak, Freq = 500 }, new() { Shape = FilterShape.Peak, Freq = 2000 }, new() { Shape = FilterShape.Peak, Freq = 8000 }] };
    public PassFilter Hpf { get; set; } = new() { Freq = 40 };
    public PassFilter Lpf { get; set; } = new() { Freq = 18000 };
    /// <summary>A safety limiter for this speaker.</summary>
    public bool Limit { get; set; }
    public double Ceiling { get; set; } = -1;

    public OutputPatch Clone() => new()
    {
        Number = Number, Source = Source, Name = Name, Level = Level, Mute = Mute, Invert = Invert, DelayMs = DelayMs,
        Eq = Eq.Clone(), Hpf = Hpf.Clone(), Lpf = Lpf.Clone(), Limit = Limit, Ceiling = Ceiling,
    };
}

/// <summary>The stream and recording mix's own settings.</summary>
public sealed class StreamSettings
{
    public StreamSource Source { get; set; } = StreamSource.Main;
    /// <summary>Send the stream mix to the Live Output PC over the network.</summary>
    public bool SendToLiveOutput { get; set; } = true;
    /// <summary>The loudness the stream aims for (YouTube and Facebook use about -14 LUFS).</summary>
    public double TargetLufs { get; set; } = -14;
    /// <summary>The final limiter's ceiling (true peak), in dB.</summary>
    public double Ceiling { get; set; } = -1;
    /// <summary>Extra delay on the stream sound to line it up with the video, in milliseconds.</summary>
    public double DelayMs { get; set; }
}

public sealed class SoloSettings
{
    public SoloMode Mode { get; set; } = SoloMode.Pfl;
    public double Level { get; set; }
}

/// <summary>Everything about the mix. Saved to audio/mix.json and in scenes.</summary>
public sealed class MixState
{
    public const int ChannelCount = 32;
    public const int OutputCount = 32;
    public const int MaxMix = 16, MaxGroups = 8, MaxMatrix = 6, MaxFx = 8;

    public int Version { get; set; } = 2;
    public List<Strip> Channels { get; set; } = [];
    /// <summary>Effects, groups, mixes and matrices, in any order (each kind is processed in its own stage).</summary>
    public List<Strip> Buses { get; set; } = [];
    public Strip Main { get; set; } = new();
    public Strip Mono { get; set; } = new();
    public Strip Stream { get; set; } = new();
    public StreamSettings StreamSettings { get; set; } = new();
    public SoloSettings Solo { get; set; } = new();
    public List<OutputPatch> Outputs { get; set; } = [];

    public IEnumerable<Strip> AllStrips()
    {
        foreach (var c in Channels) yield return c;
        foreach (var b in Buses) yield return b;
        yield return Main;
        yield return Mono;
        yield return Stream;
    }

    public Strip? Find(string id) => AllStrips().FirstOrDefault(s => s.Id == id);

    public IEnumerable<Strip> BusesOf(StripKind kind) => Buses.Where(b => b.Kind == kind).OrderBy(b => b.Number);

    /// <summary>A fresh desk: 32 channels at unity going to the main mix, a small set of buses, and every output passing its input straight through (as in Phase 1).</summary>
    public static MixState CreateDefault()
    {
        var s = new MixState();
        for (var i = 1; i <= ChannelCount; i++)
            s.Channels.Add(new Strip { Id = $"ch{i}", Kind = StripKind.Channel, Name = "", Fader = 0, ToMain = true });
        for (var i = 1; i <= 6; i++) s.Buses.Add(NewBus(StripKind.Mix, i));
        s.Buses.Add(NewBus(StripKind.Fx, 1, "Vocal reverb", Effects.Default("reverb", "hall")));
        s.Buses.Add(NewBus(StripKind.Fx, 2, "Drum room", Effects.Default("reverb", "room")));
        s.Buses.Add(NewBus(StripKind.Fx, 3, "Delay", Effects.Default("delay")));
        s.Buses.Add(NewBus(StripKind.Fx, 4, "Plate", Effects.Default("reverb", "plate")));
        s.Buses.Add(NewBus(StripKind.Group, 1, "Drums"));
        s.Buses.Add(NewBus(StripKind.Group, 2, "Band"));
        s.Buses.Add(NewBus(StripKind.Group, 3, "Vocals"));
        s.Buses.Add(NewBus(StripKind.Group, 4, "Choir"));
        s.Buses.Add(NewBus(StripKind.Matrix, 1, "Lobby"));
        s.Buses.Add(NewBus(StripKind.Matrix, 2, "Overflow"));
        s.Main = new Strip { Id = "main", Kind = StripKind.Main, Name = "Main", Stereo = true, Fader = 0, ToMain = false };
        s.Mono = new Strip { Id = "mono", Kind = StripKind.Mono, Name = "Mono / sub", Fader = Db.Floor, ToMain = false };
        s.Stream = new Strip
        {
            Id = "stream", Kind = StripKind.Stream, Name = "Stream", Stereo = true, Fader = 0, ToMain = false,
            Comp = new CompSettings { On = true, Style = CompStyle.Glue, Threshold = -20, Ratio = 2, Attack = 30, Release = 300, Knee = 6 },
            Rack = [Effects.Default("limiter")],
        };
        for (var o = 1; o <= OutputCount; o++)
            s.Outputs.Add(new OutputPatch { Number = o, Source = $"in:{o}" });
        return s;
    }

    public static Strip NewBus(StripKind kind, int number, string? name = null, RackItem? effect = null)
    {
        var prefix = kind switch
        {
            StripKind.Mix => "mix",
            StripKind.Fx => "fx",
            StripKind.Group => "grp",
            StripKind.Matrix => "mtx",
            _ => throw new ArgumentException("Not a bus", nameof(kind)),
        };
        var b = new Strip
        {
            Id = $"{prefix}{number}", Kind = kind, Name = name ?? "",
            Fader = kind is StripKind.Fx ? -6 : 0,
            Stereo = kind is StripKind.Fx or StripKind.Group,
            ToMain = kind is StripKind.Fx or StripKind.Group,
            StreamOn = kind is StripKind.Fx,
        };
        if (effect is not null) b.Rack.Add(effect);
        return b;
    }

    public static string KindLabel(StripKind k) => k switch
    {
        StripKind.Channel => "Channel",
        StripKind.Fx => "Effect",
        StripKind.Group => "Group",
        StripKind.Mix => "Mix",
        StripKind.Main => "Main",
        StripKind.Mono => "Mono",
        StripKind.Matrix => "Matrix",
        StripKind.Stream => "Stream",
        _ => "",
    };

    /// <summary>The label for a strip: its name, or "Mix 3", "Ch 5" when it has none.</summary>
    public static string Label(Strip s) => s.Name.Length > 0 ? s.Name : s.Kind switch
    {
        StripKind.Channel => $"Ch {s.Number}",
        StripKind.Main or StripKind.Mono or StripKind.Stream => KindLabel(s.Kind),
        _ => $"{KindLabel(s.Kind)} {s.Number}",
    };

    public MixState Clone() => FromJson(ToJson())!;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static MixState? FromJson(string json)
    {
        try
        {
            var s = JsonSerializer.Deserialize<MixState>(json, Json);
            s?.Repair();
            return s;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Fills anything missing (an older file, or a hand-edited one) so the engine never meets a gap.</summary>
    public void Repair()
    {
        Channels ??= [];
        Buses ??= [];
        Outputs ??= [];
        for (var i = Channels.Count + 1; i <= ChannelCount; i++)
            Channels.Add(new Strip { Id = $"ch{i}", Kind = StripKind.Channel, Fader = 0 });
        if (Channels.Count > ChannelCount) Channels.RemoveRange(ChannelCount, Channels.Count - ChannelCount);
        for (var i = 0; i < Channels.Count; i++)
        {
            Channels[i].Id = $"ch{i + 1}";
            Channels[i].Kind = StripKind.Channel;
            Channels[i].Stereo = false;
        }
        Main ??= new Strip();
        Main.Id = "main"; Main.Kind = StripKind.Main; Main.Stereo = true; Main.ToMain = false;
        Mono ??= new Strip();
        Mono.Id = "mono"; Mono.Kind = StripKind.Mono; Mono.Stereo = false; Mono.ToMain = false;
        Stream ??= new Strip();
        Stream.Id = "stream"; Stream.Kind = StripKind.Stream; Stream.Stereo = true; Stream.ToMain = false;
        StreamSettings ??= new StreamSettings();
        Solo ??= new SoloSettings();
        Buses.RemoveAll(b => b is null || b.Kind is StripKind.Channel or StripKind.Main or StripKind.Mono or StripKind.Stream);
        var seen = new HashSet<string>();
        Buses.RemoveAll(b => !seen.Add(b.Id));
        foreach (var s in AllStrips())
        {
            s.Hpf ??= new PassFilter();
            s.Lpf ??= new PassFilter { Freq = 18000 };
            s.Eq ??= new EqSettings();
            s.Eq.Bands ??= EqSettings.DefaultBands();
            s.Gate ??= new GateSettings();
            s.Comp ??= new CompSettings();
            s.Rack ??= [];
            s.Groups ??= [];
            s.Sends ??= [];
            s.Name ??= "";
            s.Colour ??= "";
            foreach (var r in s.Rack)
            {
                r.Params ??= [];
                r.Text ??= [];
            }
            // Sends only to buses that exist and that this strip is allowed to feed.
            s.Sends.RemoveAll(x => Find(x.Bus) is not { } b || !Routing.CanSend(s, b));
            s.Groups.RemoveAll(g => Find(g) is not { Kind: StripKind.Group } || s.Kind is not (StripKind.Channel or StripKind.Fx));
            var dup = new HashSet<string>();
            s.Sends.RemoveAll(x => !dup.Add(x.Bus));
        }
        for (var o = Outputs.Count + 1; o <= OutputCount; o++) Outputs.Add(new OutputPatch { Number = o, Source = "off" });
        if (Outputs.Count > OutputCount) Outputs.RemoveRange(OutputCount, Outputs.Count - OutputCount);
        for (var o = 0; o < Outputs.Count; o++)
        {
            Outputs[o].Number = o + 1;
            Outputs[o].Eq ??= new EqSettings { On = false };
            Outputs[o].Eq.Bands ??= [];
            Outputs[o].Hpf ??= new PassFilter { Freq = 40 };
            Outputs[o].Lpf ??= new PassFilter { Freq = 18000 };
            Outputs[o].Source ??= "off";
            Outputs[o].Name ??= "";
        }
    }
}

/// <summary>Which strips may feed which buses, so the mix always flows one way (no loops).</summary>
public static class Routing
{
    /// <summary>The order the stages run in. A strip can only send to a later stage.</summary>
    public static int Stage(StripKind k) => k switch
    {
        StripKind.Channel => 0,
        StripKind.Fx => 1,
        StripKind.Group => 2,
        StripKind.Mix => 3,
        StripKind.Main => 4,
        StripKind.Mono => 5,
        StripKind.Matrix => 6,
        StripKind.Stream => 7,
        _ => 9,
    };

    public static bool CanSend(Strip from, Strip to) => (from.Kind, to.Kind) switch
    {
        (StripKind.Channel, StripKind.Fx or StripKind.Mix) => true,
        (StripKind.Fx, StripKind.Mix) => true,
        (StripKind.Group, StripKind.Mix or StripKind.Matrix) => true,
        (StripKind.Mix, StripKind.Matrix) => true,
        (StripKind.Main or StripKind.Mono, StripKind.Matrix) => true,
        _ => false,
    };
}
