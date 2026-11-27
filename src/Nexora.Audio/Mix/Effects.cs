using Nexora.Audio.Dsp;

namespace Nexora.Audio.Mix;

/// <summary>One setting of an effect, for building its controls on screen.</summary>
public sealed record ParamInfo(string Key, string Label, double Min, double Max, double Default, string Unit, bool Log = false, double Step = 0)
{
    public string Format(double v) => Unit switch
    {
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

/// <summary>NEX-Audio's own effects (all designed for NEXORA from standard published techniques).</summary>
public static class Effects
{
    public static readonly IReadOnlyList<EffectInfo> All =
    [
        new("eq31", "Graphic EQ", "EQ", "31 bands on the standard third-octave centres, for wedges and speakers.",
            GraphicEq.Centres.Select((f, i) => new ParamInfo(GraphicEq.ParamName(i), f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0.#}", -15, 15, 0, "dB", Step: 0.5)).ToList(), []),
        new("comp", "Compressor", "Dynamics", "Evens out the level. Four styles: clean, smooth (opto), punchy (FET) and glue for groups.",
        [
            new("threshold", "Threshold", -60, 0, -18, "dB"),
            new("ratio", "Ratio", 1, 20, 3, ":1", Log: true),
            new("attack", "Attack", 0.05, 200, 10, "ms", Log: true),
            new("release", "Release", 10, 2000, 120, "ms", Log: true),
            new("knee", "Knee", 0, 24, 6, "dB"),
            new("makeup", "Make-up gain", -12, 24, 0, "dB"),
            new("mix", "Mix (parallel)", 0, 100, 100, "%"),
            new("key", "Listen above", 0, 400, 0, "Hz"),
        ], [new("style", "Style", ["Clean", "Opto", "Fet", "Glue"], "Clean")]),
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
        new("reverb", "Reverb", "Space", "Room, hall and plate reverbs.",
        [
            new("decay", "Decay", 0.1, 10, 2.3, "s", Log: true),
            new("predelay", "Pre-delay", 0, 200, 20, "ms"),
            new("size", "Size", 20, 160, 100, "%"),
            new("damping", "Damping", 0, 95, 50, "%"),
            new("lowcut", "Low cut", 20, 1000, 120, "Hz", Log: true),
            new("highcut", "High cut", 1000, 20000, 9000, "Hz", Log: true),
            new("width", "Width", 0, 100, 100, "%"),
            new("mix", "Mix", 0, 100, 100, "%"),
        ], [new("style", "Style", ["room", "hall", "plate"], "hall")]),
        new("delay", "Delay", "Space", "Echoes, in time with the song when a tempo is set.",
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
    ];

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
            item.Params["predelay"] = style == "room" ? 5 : style == "plate" ? 10 : 25;
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
            _ => null,
        };
        p?.Configure(r.Clone());
        return p;
    }
}
