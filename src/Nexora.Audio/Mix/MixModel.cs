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
    /// <summary>Straight from the input, after the trim and delay, before any filter or processing.</summary>
    Input,
    /// <summary>After the low/high cut, drive and inserts placed before the EQ; before the EQ and dynamics.</summary>
    PreEq,
    /// <summary>Right after the EQ and dynamic EQ (with "Dynamics first", after everything but the fader).</summary>
    PostEq,
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

/// <summary>How a strip's saturation colours the sound.</summary>
public enum DriveStyle
{
    /// <summary>Rounded and thick: mostly even harmonics, like a warm valve stage.</summary>
    Warm,
    /// <summary>Brighter and firmer: mostly odd harmonics, for presence and bite.</summary>
    Edge,
}

/// <summary>What the strip's gate does with its key.</summary>
public enum GateMode
{
    /// <summary>Turns the sound down while the key is quiet (spill and noise between words or hits).</summary>
    Gate,
    /// <summary>Turns the sound down while the key is loud (music dips under the pastor's mic).</summary>
    Ducker,
}

/// <summary>Where the EQ sits against the gate and compressor.</summary>
public enum ProcessOrder
{
    /// <summary>EQ, then gate and compressor (the compressor hears the EQ'd sound).</summary>
    EqFirst,
    /// <summary>Gate and compressor, then EQ (EQ changes never push the compressor harder).</summary>
    DynamicsFirst,
}

/// <summary>A strip's saturation stage, after the low and high cut.</summary>
public sealed class DriveSettings
{
    public bool On { get; set; }
    public DriveStyle Style { get; set; } = DriveStyle.Warm;
    /// <summary>How hard the stage is pushed, 0 to 24 dB. The level is matched afterwards, so only the colour changes.</summary>
    public double Drive { get; set; } = 6;
    /// <summary>Dry and driven blended, 0 to 100.</summary>
    public double Mix { get; set; } = 100;
    /// <summary>Level after the stage, in dB.</summary>
    public double Output { get; set; }

    public DriveSettings Clone() => (DriveSettings)MemberwiseClone();
}

/// <summary>
/// One dynamic EQ band: a bell that only moves when its own frequency range gets loud,
/// so harshness or boom is tamed only at the moments it happens.
/// </summary>
public sealed class DynEqBand
{
    public bool On { get; set; }
    public double Freq { get; set; } = 3000;
    public double Q { get; set; } = 2;
    /// <summary>The band starts acting when its frequency range rises above this, in dB.</summary>
    public double Threshold { get; set; } = -30;
    /// <summary>The most it moves: negative cuts (the usual), positive lifts. -18 to +12 dB.</summary>
    public double Range { get; set; } = -6;
    public double Ratio { get; set; } = 3;
    public double Attack { get; set; } = 5;
    public double Release { get; set; } = 120;

    public DynEqBand Clone() => (DynEqBand)MemberwiseClone();
}

public sealed class DynEqSettings
{
    public bool On { get; set; } = true;
    public List<DynEqBand> Bands { get; set; } = DefaultBands();

    public static List<DynEqBand> DefaultBands() =>
    [
        new() { Freq = 300, Q = 1.4, Threshold = -28, Range = -5 },
        new() { Freq = 3500, Q = 2, Threshold = -30, Range = -6 },
    ];

    public bool Active => On && Bands.Any(b => b.On && Math.Abs(b.Range) > 0.05);

    public DynEqSettings Clone() => new() { On = On, Bands = Bands.Select(b => b.Clone()).ToList() };
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
    public GateMode Mode { get; set; } = GateMode.Gate;
    /// <summary>The channel it listens to ("ch5"), or empty to listen to itself.</summary>
    public string Key { get; set; } = "";

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
    /// <summary>The channel it listens to ("ch5"), or empty to listen to itself.</summary>
    public string Key { get; set; } = "";

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
    /// <summary>Runs before the EQ and dynamics (an insert at the top of the strip) instead of after them.</summary>
    public bool Pre { get; set; }
    public Dictionary<string, double> Params { get; set; } = [];
    /// <summary>Text choices, for example the pitch correction key ("C") or a reverb style.</summary>
    public Dictionary<string, string> Text { get; set; } = [];
    /// <summary>For plugins: which plugin and its saved settings.</summary>
    public PluginRef? Plugin { get; set; }

    public double P(string name, double fallback) => Params.TryGetValue(name, out var v) ? v : fallback;
    public string T(string name, string fallback) => Text.TryGetValue(name, out var v) ? v : fallback;

    public RackItem Clone() => new()
    {
        Id = Id, Type = Type, Bypass = Bypass, Pre = Pre, Params = new(Params), Text = new(Text),
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
    /// <summary>A delay on this strip's input, in milliseconds (to line up a mic with a direct box, or a delay speaker), up to 1.3 seconds.</summary>
    public double DelayMs { get; set; }

    /// <summary>Channels: the X32 card input that feeds this channel (1 to 32, 0 for nothing).</summary>
    public int Input { get; set; }
    /// <summary>Channels: a second source to switch to (for example a soundcheck track or a spare mic), 0 for none.</summary>
    public int AltInput { get; set; }
    /// <summary>Channels: listening to the alternate input. The channel's processing stays as it is.</summary>
    public bool UseAlt { get; set; }
    /// <summary>Channels: where the direct out ("ch:5" on an output) is taken from.</summary>
    public SendTap DirectTap { get; set; } = SendTap.Post;

    public PassFilter Hpf { get; set; } = new();
    public PassFilter Lpf { get; set; } = new() { Freq = 18000 };
    public DriveSettings Drive { get; set; } = new();
    public EqSettings Eq { get; set; } = new();
    public DynEqSettings DynEq { get; set; } = new();
    public GateSettings Gate { get; set; } = new();
    public CompSettings Comp { get; set; } = new();
    public ProcessOrder Order { get; set; } = ProcessOrder.EqFirst;
    public List<RackItem> Rack { get; set; } = [];

    /// <summary>-1 left to 1 right (balance for stereo strips).</summary>
    public double Pan { get; set; }
    public double Fader { get; set; } = Db.Floor;
    public bool Mute { get; set; }
    public bool Solo { get; set; }

    /// <summary>Goes to the main left and right (channels, effects and groups).</summary>
    public bool ToMain { get; set; } = true;
    /// <summary>Mixes, effects and matrices: the tap point new sends to this bus start with (null: the usual one for the kind).</summary>
    public SendTap? SendsFrom { get; set; }
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

    /// <summary>Takes another strip's sound: input trim and delay, filters, drive, EQ, dynamic EQ, gate, compressor, order and effects.</summary>
    /// <param name="newIds">Give the effects new ids (copying onto a different strip, so both can run at once).</param>
    public void TakeProcessing(Strip src, bool newIds, bool input = true)
    {
        if (input)
        {
            Trim = src.Trim;
            Invert = src.Invert;
            DelayMs = src.DelayMs;
        }
        Hpf = src.Hpf.Clone();
        Lpf = src.Lpf.Clone();
        Drive = src.Drive.Clone();
        Eq = src.Eq.Clone();
        DynEq = src.DynEq.Clone();
        Gate = src.Gate.Clone();
        Comp = src.Comp.Clone();
        Order = src.Order;
        Rack = src.Rack.Select(r =>
        {
            var c = r.Clone();
            if (newIds) c.Id = Guid.NewGuid().ToString("n")[..8];
            return c;
        }).ToList();
    }

    public Strip Clone() => new()
    {
        Id = Id, Kind = Kind, Name = Name, Colour = Colour, Stereo = Stereo, Trim = Trim, Invert = Invert, DelayMs = DelayMs,
        Input = Input, AltInput = AltInput, UseAlt = UseAlt, DirectTap = DirectTap,
        Hpf = Hpf.Clone(), Lpf = Lpf.Clone(), Drive = Drive.Clone(), Eq = Eq.Clone(), DynEq = DynEq.Clone(), Gate = Gate.Clone(), Comp = Comp.Clone(),
        Order = Order, Rack = Rack.Select(r => r.Clone()).ToList(), Pan = Pan, Fader = Fader, Mute = Mute, Solo = Solo, ToMain = ToMain, SendsFrom = SendsFrom,
        Groups = [.. Groups], Sends = Sends.Select(s => s.Clone()).ToList(), StreamLevel = StreamLevel, StreamOn = StreamOn,
        Preamp = Preamp, Phantom = Phantom, RecallSafe = RecallSafe,
    };
}

/// <summary>What one X32 card return plays, and the speaker processing on it.</summary>
public sealed class OutputPatch
{
    public int Number { get; set; }
    /// <summary>
    /// "off", "tone", "in:5" (input 5 as it arrives), "ch:5" (channel 5's direct out, from its direct-out tap point), "mix1", "mix1:L",
    /// "main:L", "main:R", "mono", "mtx2", "stream:L", "stream:R", "mon:L", "mon:R" (the solo / monitor bus).
    /// </summary>
    public string Source { get; set; } = "off";
    public string Name { get; set; } = "";
    public double Level { get; set; }
    public bool Mute { get; set; }
    public bool Invert { get; set; }
    /// <summary>Delay for lining up speakers, in milliseconds (up to 1.3 seconds).</summary>
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

/// <summary>What the test signal plays.</summary>
public enum ToneKind
{
    /// <summary>A steady sine tone at one frequency.</summary>
    Sine,
    /// <summary>Pink noise: equal energy in every octave, for tuning speakers by ear and with a meter.</summary>
    Pink,
    /// <summary>White noise: equal energy at every frequency (sounds bright).</summary>
    White,
}

/// <summary>The test signal: played on any output set to "tone", and into one bus while <see cref="On"/>.</summary>
public sealed class ToneSettings
{
    public const double MaxLevel = -6;

    public ToneKind Kind { get; set; } = ToneKind.Sine;
    public double Freq { get; set; } = 1000;
    /// <summary>In dBFS, never above <see cref="MaxLevel"/>.</summary>
    public double Level { get; set; } = -18;
    /// <summary>A bus to play the test signal into ("" for none), so it goes through that bus's processing and routing.</summary>
    public string Bus { get; set; } = "";
    /// <summary>Playing into the bus. Always off when the desk starts and never recalled from a scene.</summary>
    public bool On { get; set; }

    public ToneSettings Clone() => (ToneSettings)MemberwiseClone();
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

    /// <summary>3: channels choose their input (older files fed channel N from input N).</summary>
    public int Version { get; set; } = 3;
    public List<Strip> Channels { get; set; } = [];
    /// <summary>Effects, groups, mixes and matrices, in any order (each kind is processed in its own stage).</summary>
    public List<Strip> Buses { get; set; } = [];
    public Strip Main { get; set; } = new();
    public Strip Mono { get; set; } = new();
    public Strip Stream { get; set; } = new();
    public StreamSettings StreamSettings { get; set; } = new();
    public SoloSettings Solo { get; set; } = new();
    public ToneSettings Tone { get; set; } = new();
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
            s.Channels.Add(new Strip { Id = $"ch{i}", Kind = StripKind.Channel, Name = "", Fader = 0, ToMain = true, Input = i });
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

    private string ValidKey(Strip s, string? key) =>
        key is { Length: > 0 } k && k != s.Id && Channels.Any(c => c.Id == k) ? k : "";

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

    /// <summary>Where a new send to this bus is taken from: the bus's own choice, or before the fader for monitor mixes and after it for the rest.</summary>
    public static SendTap DefaultTap(Strip bus) => bus.SendsFrom ?? (bus.Kind == StripKind.Mix ? SendTap.Pre : SendTap.Post);

    /// <summary>The tap points in the order a strip runs them, with short names for the screens.</summary>
    public static readonly IReadOnlyList<(SendTap Tap, string Short, string Name)> Taps =
    [
        (SendTap.Input, "IN", "Input (before any processing)"),
        (SendTap.PreEq, "PRE EQ", "Before the EQ"),
        (SendTap.PostEq, "POST EQ", "After the EQ"),
        (SendTap.Pre, "PRE", "Before the fader"),
        (SendTap.Post, "POST", "After the fader"),
    ];

    public static string TapShort(SendTap t) => Taps.First(x => x.Tap == t).Short;

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
            Channels.Add(new Strip { Id = $"ch{i}", Kind = StripKind.Channel, Fader = 0, Input = i });
        if (Channels.Count > ChannelCount) Channels.RemoveRange(ChannelCount, Channels.Count - ChannelCount);
        for (var i = 0; i < Channels.Count; i++)
        {
            var c = Channels[i];
            c.Id = $"ch{i + 1}";
            c.Kind = StripKind.Channel;
            c.Stereo = false;
            // Before version 3 channel N always heard input N.
            if (Version < 3) c.Input = i + 1;
            c.Input = Math.Clamp(c.Input, 0, ChannelCount);
            c.AltInput = Math.Clamp(c.AltInput, 0, ChannelCount);
            if (c.AltInput == 0) c.UseAlt = false;
        }
        Version = 3;
        Tone ??= new ToneSettings();
        Tone.Bus ??= "";
        if (Tone.Bus.Length > 0 && Buses.All(b => b.Id != Tone.Bus) && Tone.Bus is not ("main" or "mono" or "stream")) Tone.Bus = "";
        if (Tone.Bus.Length == 0) Tone.On = false;
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
            s.Drive ??= new DriveSettings();
            s.DynEq ??= new DynEqSettings();
            s.DynEq.Bands ??= DynEqSettings.DefaultBands();
            s.DynEq.Bands.RemoveAll(b => b is null);
            while (s.DynEq.Bands.Count < 2) s.DynEq.Bands.Add(DynEqSettings.DefaultBands()[s.DynEq.Bands.Count]);
            if (s.DynEq.Bands.Count > 4) s.DynEq.Bands.RemoveRange(4, s.DynEq.Bands.Count - 4);
            s.Gate ??= new GateSettings();
            s.Comp ??= new CompSettings();
            // A sidechain listens to another channel; anything else (itself, a bus, a channel that is gone) means "itself".
            s.Gate.Key = ValidKey(s, s.Gate.Key);
            s.Comp.Key = ValidKey(s, s.Comp.Key);
            s.Rack ??= [];
            s.Groups ??= [];
            s.Sends ??= [];
            s.Name ??= "";
            s.Colour ??= "";
            if (s.Kind is not (StripKind.Mix or StripKind.Fx or StripKind.Matrix)) s.SendsFrom = null;
            foreach (var r in s.Rack)
            {
                r.Params ??= [];
                r.Text ??= [];
            }
            // Sends only to buses that exist and that this strip is allowed to feed.
            s.Sends.RemoveAll(x => Find(x.Bus) is not { } b || !Routing.CanSend(s, b));
            s.Groups.RemoveAll(g => Find(g) is not { } gb || !Routing.CanGroup(s, gb));
            var dup = new HashSet<string>();
            s.Sends.RemoveAll(x => !dup.Add(x.Bus));
            s.Groups = s.Groups.Distinct().ToList();
            if (s.Kind != StripKind.Channel)
            {
                s.Input = s.AltInput = 0;
                s.UseAlt = false;
                s.DirectTap = SendTap.Post;
            }
        }
        // Nothing may feed back into itself (a file from elsewhere, or a hand edit).
        Routing.RemoveLoops(this);
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

/// <summary>
/// Which strips may feed which, as one map (a directed graph): channels, effects, groups, mixes, the main mix,
/// mono, matrices and the stream are the points, and every route to the main mix, group assignment and send is a line.
/// The engine runs the strips in the map's order, so a strip always runs after everything that feeds it.
/// A route that would make a loop (feedback) is never allowed.
/// </summary>
public static class Routing
{
    /// <summary>The usual order of kinds, used to keep the running order and the screens tidy.</summary>
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

    /// <summary>Which kinds a send may go between (loops are checked separately, see <see cref="WouldLoop"/>).</summary>
    public static bool KindsCanSend(StripKind from, StripKind to) => (from, to) switch
    {
        (StripKind.Channel, StripKind.Fx or StripKind.Mix or StripKind.Matrix) => true,
        (StripKind.Fx, StripKind.Fx or StripKind.Mix or StripKind.Matrix) => true,
        (StripKind.Group, StripKind.Fx or StripKind.Mix or StripKind.Matrix) => true,
        (StripKind.Mix, StripKind.Mix or StripKind.Matrix) => true,
        (StripKind.Main or StripKind.Mono, StripKind.Matrix) => true,
        (StripKind.Matrix, StripKind.Matrix) => true,
        _ => false,
    };

    public static bool CanSend(Strip from, Strip to) => from.Id != to.Id && KindsCanSend(from.Kind, to.Kind);

    /// <summary>Channels, effect returns and groups can go through a group (a group into a group makes a sub-group).</summary>
    public static bool CanGroup(Strip from, Strip to) =>
        from.Id != to.Id && to.Kind == StripKind.Group && from.Kind is StripKind.Channel or StripKind.Fx or StripKind.Group;

    /// <summary>Every line in the map, including the fixed ones (the main mix feeds mono / sub and the stream).</summary>
    public static IEnumerable<(string From, string To, string Why)> Edges(MixState s)
    {
        foreach (var x in s.AllStrips())
        {
            if (x.ToMain && x.Kind is StripKind.Channel or StripKind.Fx or StripKind.Group) yield return (x.Id, s.Main.Id, "main");
            foreach (var g in x.Groups) yield return (x.Id, g, "group");
            foreach (var send in x.Sends) yield return (x.Id, send.Bus, "send");
        }
        yield return (s.Main.Id, s.Mono.Id, "fixed");
        yield return (s.Main.Id, s.Stream.Id, "fixed");
    }

    private static Dictionary<string, List<string>> Map(MixState s, (string From, string To)? extra = null)
    {
        var map = new Dictionary<string, List<string>>();
        void Add(string a, string b)
        {
            if (!map.TryGetValue(a, out var l)) map[a] = l = [];
            if (!l.Contains(b)) l.Add(b);
        }
        foreach (var (a, b, _) in Edges(s)) Add(a, b);
        if (extra is { } e) Add(e.From, e.To);
        return map;
    }

    private static HashSet<string> Walk(Dictionary<string, List<string>> map, string start)
    {
        var seen = new HashSet<string>();
        var todo = new Stack<string>();
        todo.Push(start);
        while (todo.Count > 0)
        {
            var at = todo.Pop();
            if (!map.TryGetValue(at, out var next)) continue;
            foreach (var n in next)
                if (seen.Add(n)) todo.Push(n);
        }
        return seen;
    }

    /// <summary>A new line from one strip to another would let sound come back round to where it started.</summary>
    public static bool WouldLoop(MixState s, string from, string to) => from == to || Walk(Map(s), to).Contains(from);

    /// <summary>Everything this strip's sound reaches (buses, main, matrices, stream).</summary>
    public static HashSet<string> Downstream(MixState s, string id) => Walk(Map(s), id);

    /// <summary>Everything that feeds this strip, however far back.</summary>
    public static HashSet<string> Upstream(MixState s, string id)
    {
        var back = new Dictionary<string, List<string>>();
        foreach (var (a, b, _) in Edges(s))
        {
            if (!back.TryGetValue(b, out var l)) back[b] = l = [];
            if (!l.Contains(a)) l.Add(a);
        }
        return Walk(back, id);
    }

    /// <summary>
    /// The order to run every strip in: each one after everything that feeds it, and otherwise in the usual order
    /// (channels, effects, groups, mixes, main, mono, matrices, stream). Null if the map has a loop.
    /// </summary>
    public static List<Strip>? Order(MixState s)
    {
        var all = s.AllStrips().ToList();
        var rank = all.Select((x, i) => (x.Id, Key: Stage(x.Kind) * 1000 + i)).ToDictionary(p => p.Id, p => p.Key);
        var byId = all.ToDictionary(x => x.Id);
        var into = all.ToDictionary(x => x.Id, _ => 0);
        var map = Map(s);
        foreach (var (a, list) in map)
            foreach (var b in list)
                if (into.ContainsKey(b) && byId.ContainsKey(a)) into[b]++;
        var ready = new SortedSet<(int Rank, string Id)>(all.Where(x => into[x.Id] == 0).Select(x => (rank[x.Id], x.Id)));
        var order = new List<Strip>();
        while (ready.Count > 0)
        {
            var first = ready.Min;
            ready.Remove(first);
            order.Add(byId[first.Id]);
            if (!map.TryGetValue(first.Id, out var next)) continue;
            foreach (var n in next)
                if (into.ContainsKey(n) && --into[n] == 0) ready.Add((rank[n], n));
        }
        return order.Count == all.Count ? order : null;
    }

    /// <summary>Takes out any group assignment or send that closes a loop, keeping the earlier ones. Returns how many went.</summary>
    public static int RemoveLoops(MixState s)
    {
        // Start from the lines that can never loop (to the main mix, and from it to mono and the stream), then add the rest one by one.
        var map = new Dictionary<string, List<string>>();
        void Add(string a, string b)
        {
            if (!map.TryGetValue(a, out var l)) map[a] = l = [];
            if (!l.Contains(b)) l.Add(b);
        }
        foreach (var (a, b, why) in Edges(s))
            if (why is "main" or "fixed") Add(a, b);
        var removed = 0;
        foreach (var x in s.AllStrips().OrderBy(x => Stage(x.Kind)))
        {
            foreach (var g in x.Groups.ToList())
            {
                if (g == x.Id || Walk(map, g).Contains(x.Id)) { x.Groups.Remove(g); removed++; }
                else Add(x.Id, g);
            }
            foreach (var send in x.Sends.ToList())
            {
                if (send.Bus == x.Id || Walk(map, send.Bus).Contains(x.Id)) { x.Sends.Remove(send); removed++; }
                else Add(x.Id, send.Bus);
            }
        }
        return removed;
    }

    /// <summary>A short reason a route cannot be made, or null when it can.</summary>
    public static string? WhyNot(MixState s, Strip from, Strip to, bool group)
    {
        if (from.Id == to.Id) return "A bus cannot feed itself.";
        if (group ? !CanGroup(from, to) : !CanSend(from, to))
            return $"{MixState.KindLabel(from.Kind)} strips cannot feed a {MixState.KindLabel(to.Kind).ToLowerInvariant()} here.";
        if (WouldLoop(s, from.Id, to.Id))
            return $"{MixState.Label(to)} already feeds {MixState.Label(from)}, so this would make a feedback loop.";
        return null;
    }
}
