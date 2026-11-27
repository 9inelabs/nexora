using Nexora.Audio.Dsp;

namespace Nexora.Audio.Mix;

/// <summary>One setting of an effect, for building its controls on screen.</summary>
public sealed record ParamInfo(string Key, string Label, double Min, double Max, double Default, string Unit, bool Log = false, double Step = 0)
{
    public string Format(double v) => Unit switch
    {
        "Hz" when Min == 0 && v < 0.5 => "Off",
        "st" => $"{v:+0;-0;0} st",
        "cents" => $"{v:+0;-0;0} cents",
        "Hz" => v >= 1000 ? $"{v / 1000:0.##} kHz" : $"{v:0} Hz",
        "dB" => $"{v:+0.0;-0.0;0.0} dB",
        "ms" => v >= 1000 ? $"{v / 1000:0.00} s" : $"{v:0.#} ms",
        "s" => $"{v:0.0} s",
        "%" => $"{v:0}%",
        ":1" => $"{v:0.#}:1",
        "bpm" => v < 30 ? "Off" : $"{v:0} bpm",
        "on" => v >= 0.5 ? "On" : "Off",
        "beats" => BeatName(v),
        _ => $"{v:0.##} {Unit}".Trim(),
    };

    public static string BeatName(double beats) => beats switch
    {
        >= 3.9 => "1 bar",
        >= 1.9 => "1/2",
        >= 1.4 => "Dotted 1/4",
        >= 0.99 => "1/4",
        >= 0.74 => "Dotted 1/8",
        >= 0.66 => "1/4 triplet",
        >= 0.49 => "1/8",
        >= 0.37 => "Dotted 1/16",
        >= 0.33 => "1/8 triplet",
        >= 0.24 => "1/16",
        _ => "1/32",
    };
}

public sealed record ChoiceInfo(string Key, string Label, IReadOnlyList<string> Options, string Default);

/// <summary>An effect NEX-Audio can put in a rack.</summary>
public sealed record EffectInfo(string Type, string Name, string Group, string Description, IReadOnlyList<ParamInfo> Params, IReadOnlyList<ChoiceInfo> Choices);

/// <summary>The pools the desk shares out, like slots on a hardware desk.</summary>
public enum EffectPool
{
    /// <summary>Reverbs, delays and the other effects: no fixed limit, only the PC's headroom.</summary>
    Effect,
    ColourStrip,
    /// <summary>The effects rack: 8 mono or 4 stereo slots for the 6-band dynamic EQ and multiband.</summary>
    Rack,
    /// <summary>Processing nodes: EQ, compressor, multiband, de-esser, limiter, gate or ducker anywhere in a path.</summary>
    Node,
    Plugin,
}

/// <summary>NEX-Audio's own effects (all designed for NEXORA from standard published techniques).</summary>
public static class Effects
{
    public static readonly IReadOnlyList<EffectInfo> All =
    [
        new("eq31", "Graphic EQ", "EQ", "31 bands on the standard third-octave centres, for wedges and speakers.",
            GraphicEq.Centres.Select((f, i) => new ParamInfo(GraphicEq.ParamName(i), f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0.#}", -15, 15, 0, "dB", Step: 0.5)).ToList(), []),
        new("comp", "Compressor", "Dynamics", "Evens out the level. Six characters: clean, smooth (opto), punchy (FET), glue for groups, vintage (VCA) and a fast limiter.",
        [
            new("threshold", "Threshold", -60, 0, -18, "dB"),
            new("ratio", "Ratio", 1, 20, 3, ":1", Log: true),
            new("attack", "Attack", 0.05, 200, 10, "ms", Log: true),
            new("release", "Release", 10, 2000, 120, "ms", Log: true),
            new("knee", "Knee", 0, 24, 6, "dB"),
            new("makeup", "Make-up gain", -12, 24, 0, "dB"),
            new("mix", "Mix (parallel)", 0, 100, 100, "%"),
            new("key", "Listen above", 0, 400, 0, "Hz"),
        ], [new("style", "Style", ["Clean", "Opto", "Fet", "Glue", "Vintage", "Limit"], "Clean"), new("detect", "Listens to", ["Auto", "Peak", "Rms"], "Auto")]),
        new("multiband", "Multiband compressor", "Dynamics", "Controls the low, middle and top of the sound separately.",
        [
            new("low", "Low split", 40, 1000, 200, "Hz", Log: true),
            new("high", "High split", 1000, 12000, 3000, "Hz", Log: true),
            new("lthr", "Low threshold", -60, 0, -20, "dB"), new("lratio", "Low ratio", 1, 10, 2.5, ":1"), new("lgain", "Low gain", -12, 12, 0, "dB"),
            new("mthr", "Mid threshold", -60, 0, -20, "dB"), new("mratio", "Mid ratio", 1, 10, 2.5, ":1"), new("mgain", "Mid gain", -12, 12, 0, "dB"),
            new("hthr", "Top threshold", -60, 0, -20, "dB"), new("hratio", "Top ratio", 1, 10, 2.5, ":1"), new("hgain", "Top gain", -12, 12, 0, "dB"),
            new("attack", "Attack", 0.5, 100, 10, "ms", Log: true),
            new("release", "Release", 20, 1000, 150, "ms", Log: true),
        ], []),
        new("deesser", "De-esser", "Dynamics", "Softens harsh \"s\" and \"t\" sounds in a voice.",
        [
            new("freq", "Frequency", 2000, 14000, 6500, "Hz", Log: true),
            new("threshold", "Threshold", -60, 0, -30, "dB"),
            new("range", "Most reduction", 0, 24, 10, "dB"),
        ], []),
        new("limiter", "Limiter", "Dynamics", "Stops any peak going past the ceiling. Looks 1 ms ahead.",
        [
            new("ceiling", "Ceiling", -12, 0, -1, "dB", Step: 0.1),
            new("release", "Release", 5, 1000, 80, "ms", Log: true),
            new("lookahead", "Look ahead", 0.1, 5, 1, "ms"),
        ], []),
        new("reverb", "Reverb", "Space", "Room, hall, plate, chamber, church and ambience reverbs.",
        [
            new("decay", "Decay", 0.1, 10, 2.3, "s", Log: true),
            new("predelay", "Pre-delay", 0, 200, 20, "ms"),
            new("size", "Size", 20, 160, 100, "%"),
            new("damping", "Damping", 0, 95, 50, "%"),
            new("lowcut", "Low cut", 20, 1000, 120, "Hz", Log: true),
            new("highcut", "High cut", 1000, 20000, 9000, "Hz", Log: true),
            new("width", "Width", 0, 100, 100, "%"),
            new("mix", "Mix", 0, 100, 100, "%"),
        ], [new("style", "Style", ["room", "hall", "plate", "chamber", "church", "ambience"], "hall")]),
        new("delay", "Delay", "Space", "Echoes, in time with the song when a tempo is set. Tap the tempo with the TAP button.",
        [
            new("time", "Time", 1, 2000, 375, "ms", Log: true),
            new("bpm", "Tempo", 0, 220, 0, "bpm", Step: 1),
            new("beats", "Note", 0.125, 4, 0.75, "beats"),
            new("feedback", "Repeats", 0, 95, 35, "%"),
            new("lowcut", "Low cut", 20, 1000, 150, "Hz", Log: true),
            new("highcut", "High cut", 1000, 20000, 6000, "Hz", Log: true),
            new("pingpong", "Ping-pong", 0, 1, 0, "on", Step: 1),
            new("mix", "Mix", 0, 100, 100, "%"),
        ], []),
        new("chorus", "Chorus", "Space", "Widens and thickens: gentle copies that drift in pitch.",
        [
            new("rate", "Rate", 0.05, 8, 0.8, "Hz", Log: true),
            new("depth", "Depth", 0, 100, 30, "%"),
            new("delay", "Delay", 3, 30, 12, "ms"),
            new("mix", "Mix", 0, 100, 50, "%"),
        ], []),
        new("pitch", "Pitch correction", "Vocal", "Moves a voice towards the nearest note of the key. Slower speeds sound natural.",
        [
            new("speed", "Speed", 0, 400, 60, "ms"),
            new("amount", "Amount", 0, 100, 100, "%"),
        ],
        [
            new("key", "Key", Scales.Keys, "C"),
            new("scale", "Scale", ["major", "minor", "chromatic", "pentatonic"], "major"),
        ]),
        // ---- Phase 4: colour strip, effects rack, processing nodes and more effects -------------------
        new("colour", "Colour strip", "Colour strip",
            "A whole extra strip in one insert: low and high cut, a tube, tape or iron stage, a 4-band EQ, a compressor in any character and a gate or ducker.",
        [
            new("drive", "Drive", 0, 24, 6, "dB", Step: 0.5),
            new("lowcut", "Low cut", 0, 600, 0, "Hz"),
            new("highcut", "High cut", 2000, 20000, 20000, "Hz", Log: true),
            new("lf", "Low", 30, 400, 100, "Hz", Log: true), new("lg", "Low gain", -15, 15, 0, "dB", Step: 0.5),
            new("lmf", "Low mid", 100, 2000, 400, "Hz", Log: true), new("lmg", "Low mid gain", -15, 15, 0, "dB", Step: 0.5),
            new("hmf", "High mid", 800, 10000, 2500, "Hz", Log: true), new("hmg", "High mid gain", -15, 15, 0, "dB", Step: 0.5),
            new("hf", "High", 2000, 16000, 8000, "Hz", Log: true), new("hg", "High gain", -15, 15, 0, "dB", Step: 0.5),
            new("cthr", "Comp thresh", -60, 0, -18, "dB"), new("cratio", "Comp ratio", 1, 20, 3, ":1", Log: true),
            new("cattack", "Comp attack", 0.05, 200, 10, "ms", Log: true), new("crelease", "Comp release", 10, 2000, 120, "ms", Log: true),
            new("cmakeup", "Comp make-up", -12, 24, 0, "dB"),
            new("dthr", "Gate thresh", -80, 0, -45, "dB"), new("drange", "Gate range", 0, 60, 20, "dB down"),
            new("drelease", "Gate release", 10, 2000, 150, "ms", Log: true),
            new("output", "Output", -12, 12, 0, "dB", Step: 0.5),
        ],
        [
            new("character", "Character", Dsp.ColourStrip.Characters, "Tube"),
            new("compstyle", "Compressor", Dsp.ColourStrip.CompStyles, "Off"),
            new("dynamics", "Gate / ducker", Dsp.ColourStrip.DynModes, "Off"),
            new("key", "Listens to", [], ""),
        ]),
        new("dyneq6", "Dynamic EQ (6 bands)", "Effects rack",
            "Six bells that move only when their own range gets loud: tame boom, harshness or sibilance only when it happens. Range 0 leaves a band off.",
        [
            .. Enumerable.Range(1, 6).SelectMany(i => new ParamInfo[]
            {
                new($"f{i}", $"Freq {i}", 30, 16000, Dsp.DynEq6.Freqs[i - 1], "Hz", Log: true),
                new($"t{i}", $"Thresh {i}", -60, 0, -30, "dB"),
                new($"g{i}", $"Range {i}", -18, 12, 0, "dB", Step: 0.5),
            }),
            new("q", "Width (Q)", 0.3, 8, 1.4, "", Log: true),
            new("ratio", "Ratio", 1, 10, 3, ":1"),
            new("attack", "Attack", 0.5, 100, 5, "ms", Log: true),
            new("release", "Release", 10, 1000, 120, "ms", Log: true),
        ], []),
        new("multiband6", "Multiband (6 bands)", "Effects rack",
            "Six bands, each compressed (or expanded) on its own. Expand turns quiet parts down further, to clean up noise or spill.",
        [
            .. Enumerable.Range(1, 5).Select(i => new ParamInfo($"x{i}", $"Split {i}", 40, 16000, Dsp.Multiband6.Splits[i - 1], "Hz", Log: true)),
            .. Enumerable.Range(1, 6).SelectMany(i => new ParamInfo[]
            {
                new($"t{i}", $"Thresh {i}", -70, 0, -20, "dB"),
                new($"r{i}", $"Ratio {i}", 1, 10, 2, ":1"),
                new($"g{i}", $"Gain {i}", -12, 12, 0, "dB", Step: 0.5),
            }),
            new("attack", "Attack", 0.5, 100, 10, "ms", Log: true),
            new("release", "Release", 20, 1000, 150, "ms", Log: true),
        ], [new("mode", "Mode", ["Compress", "Expand"], "Compress")]),
        new("eq", "EQ node", "EQ", "A four-band EQ with low and high cut, to drop anywhere in a path.",
        [
            new("lowcut", "Low cut", 0, 600, 0, "Hz"),
            new("lf", "Low", 30, 400, 100, "Hz", Log: true), new("lg", "Low gain", -15, 15, 0, "dB", Step: 0.5),
            new("m1f", "Mid 1", 80, 8000, 500, "Hz", Log: true), new("m1g", "Mid 1 gain", -15, 15, 0, "dB", Step: 0.5), new("m1q", "Mid 1 width", 0.3, 10, 1.2, "", Log: true),
            new("m2f", "Mid 2", 200, 16000, 3000, "Hz", Log: true), new("m2g", "Mid 2 gain", -15, 15, 0, "dB", Step: 0.5), new("m2q", "Mid 2 width", 0.3, 10, 1.2, "", Log: true),
            new("hf", "High", 2000, 16000, 10000, "Hz", Log: true), new("hg", "High gain", -15, 15, 0, "dB", Step: 0.5),
            new("highcut", "High cut", 2000, 20000, 20000, "Hz", Log: true),
        ], []),
        new("gate", "Gate / ducker node", "Dynamics", "A gate or a ducker anywhere in a path, listening to itself or to any channel.",
        [
            new("threshold", "Threshold", -80, 0, -45, "dB"),
            new("range", "Range", 0, 80, 30, "dB down"),
            new("attack", "Attack", 0.05, 50, 0.5, "ms", Log: true),
            new("hold", "Hold", 0, 500, 40, "ms"),
            new("release", "Release", 10, 2000, 150, "ms", Log: true),
            new("keyfilter", "Key filter", 0, 400, 0, "Hz"),
        ], [new("mode", "Mode", ["Gate", "Ducker"], "Gate"), new("key", "Listens to", [], "")]),
        new("pitchshift", "Pitch shifter", "Vocal", "Moves the whole sound up or down: an octave, a harmony, or a few cents for width.",
        [
            new("semis", "Semitones", -12, 12, 0, "st", Step: 1),
            new("cents", "Fine", -50, 50, 0, "cents", Step: 1),
            new("window", "Grain", 10, 80, 30, "ms"),
            new("mix", "Mix", 0, 100, 100, "%"),
        ], []),
        new("enhancer", "Enhancer", "Vocal", "Brings speech forward: adds a little edge to the top of a voice so words stay clear without turning it up.",
        [
            new("freq", "Tune", 800, 10000, 3000, "Hz", Log: true),
            new("amount", "Amount", 0, 100, 30, "%"),
            new("harmonics", "Harmonics", 0, 100, 50, "%"),
            new("body", "Body", 0, 6, 0, "dB", Step: 0.5),
        ], []),
    ];

    // ---- Pools: how many of each kind the desk can hold -----------------------------------------------

    public const int MaxColourStrips = 36;
    /// <summary>Effects-rack slots: a mono item takes one, a stereo item two.</summary>
    public const int MaxRackSlots = 8;
    public const int MaxNodes = 64;

    /// <summary>Which pool an effect counts against.</summary>
    public static EffectPool PoolOf(string type) => type switch
    {
        "colour" => EffectPool.ColourStrip,
        "dyneq6" or "multiband6" => EffectPool.Rack,
        "eq" or "gate" or "comp" or "multiband" or "deesser" or "limiter" => EffectPool.Node,
        "plugin" => EffectPool.Plugin,
        _ => EffectPool.Effect,
    };

    /// <summary>How much of its pool this item takes: rack items take two slots on a stereo strip.</summary>
    public static int SlotsFor(string type, bool stereo) => PoolOf(type) == EffectPool.Rack && stereo ? 2 : 1;

    /// <summary>How much of each pool the whole desk uses now.</summary>
    public static Dictionary<EffectPool, int> Usage(MixState m)
    {
        var used = Enum.GetValues<EffectPool>().ToDictionary(p => p, _ => 0);
        foreach (var s in m.AllStrips())
            foreach (var r in s.Rack)
                used[PoolOf(r.Type)] += SlotsFor(r.Type, s.Stereo);
        return used;
    }

    public static int LimitOf(EffectPool p) => p switch
    {
        EffectPool.ColourStrip => MaxColourStrips,
        EffectPool.Rack => MaxRackSlots,
        EffectPool.Node => MaxNodes,
        _ => 0,
    };

    /// <summary>Why this effect cannot go on that strip now (a pool is full), or null when it can.</summary>
    public static string? WhyNot(MixState m, Strip s, string type)
    {
        var pool = PoolOf(type);
        var limit = LimitOf(pool);
        if (limit == 0) return null;
        var used = Usage(m)[pool];
        var need = SlotsFor(type, s.Stereo);
        if (used + need <= limit) return null;
        return pool switch
        {
            EffectPool.ColourStrip => $"All {limit} colour strips are in use. Remove one first.",
            EffectPool.Rack => $"The effects rack is full ({used} of {limit} slots; a stereo bus takes two). Remove one first.",
            _ => $"All {limit} processing nodes are in use. Remove one first.",
        };
    }

    /// <summary>The tempo a run of taps gives, in beats per minute (30 to 220), or null with fewer than two taps.</summary>
    public static double? TempoFrom(IReadOnlyList<DateTime> taps)
    {
        if (taps.Count < 2) return null;
        var avg = (taps[^1] - taps[0]).TotalSeconds / (taps.Count - 1);
        if (avg <= 0) return null;
        return Math.Round(Math.Clamp(60 / avg, 30, 220));
    }

    public static EffectInfo? Get(string type) => All.FirstOrDefault(e => e.Type == type);

    public static string NameOf(RackItem r) => r.Type == "plugin" ? r.Plugin?.Name ?? "Plugin" : Get(r.Type)?.Name ?? r.Type;

    /// <summary>A new effect with its default settings. For the reverb, <paramref name="style"/> is room, hall or plate.</summary>
    public static RackItem Default(string type, string? style = null)
    {
        var item = new RackItem { Type = type };
        if (Get(type) is { } info)
        {
            foreach (var p in info.Params) item.Params[p.Key] = p.Default;
            foreach (var c in info.Choices) item.Text[c.Key] = c.Default;
        }
        if (type == "reverb" && style is not null)
        {
            item.Text["style"] = style;
            var st = Reverb.StyleFor(style);
            item.Params["decay"] = st.Decay;
            item.Params["damping"] = st.Damping * 100;
            item.Params["predelay"] = style switch { "room" => 5, "plate" => 10, "ambience" => 2, "chamber" => 12, "church" => 45, _ => 25 };
        }
        return item;
    }

    /// <summary>Makes the audio-thread processor for a built-in effect (null for plugins and unknown types).</summary>
    public static Processor? Create(RackItem r, double sampleRate, int maxFrames)
    {
        Processor? p = r.Type switch
        {
            "eq31" => new GraphicEq(sampleRate, maxFrames),
            "comp" => new Compressor(sampleRate, maxFrames),
            "multiband" => new MultibandCompressor(sampleRate, maxFrames),
            "deesser" => new DeEsser(sampleRate, maxFrames),
            "limiter" => new Limiter(sampleRate, maxFrames),
            "reverb" => new Reverb(sampleRate, maxFrames),
            "delay" => new Echo(sampleRate, maxFrames),
            "chorus" => new Chorus(sampleRate, maxFrames),
            "pitch" => new PitchCorrector(sampleRate, maxFrames),
            "colour" => new ColourStrip(sampleRate, maxFrames),
            "dyneq6" => new DynEq6(sampleRate, maxFrames),
            "multiband6" => new Multiband6(sampleRate, maxFrames),
            "eq" => new EqNode(sampleRate, maxFrames),
            "gate" => new GateNode(sampleRate, maxFrames),
            "pitchshift" => new PitchShifter(sampleRate, maxFrames),
            "enhancer" => new Enhancer(sampleRate, maxFrames),
            _ => null,
        };
        p?.Configure(r.Clone());
        return p;
    }
}
