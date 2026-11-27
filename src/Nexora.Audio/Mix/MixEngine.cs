using Nexora.Audio.Dsp;

namespace Nexora.Audio.Mix;

/// <summary>A processor in a rack, with its bypass. When bypassed it still delays by its latency so timing never jumps.</summary>
internal sealed class RackSlot
{
    public RackSlot(string id, string type, Processor processor)
    {
        Id = id;
        Type = type;
        Processor = processor;
    }

    public string Id { get; }
    public string Type { get; }
    public Processor Processor { get; }
    public volatile bool Bypass;
    /// <summary>Runs before the EQ and dynamics instead of after them.</summary>
    public volatile bool Pre;
    public volatile int Latency;
    public string? LastConfig;
    private DelayLine? _bl, _br;

    public void Run(float[] l, float[]? r, int frames)
    {
        if (!Bypass)
        {
            Processor.Run(l, r, frames);
            return;
        }
        var lat = Latency;
        if (lat <= 0) return;
        _bl ??= new DelayLine(Math.Max(lat, 1024));
        _bl.Process(l, frames, lat);
        if (r is not null)
        {
            _br ??= new DelayLine(Math.Max(lat, 1024));
            _br.Process(r, frames, lat);
        }
    }
}

/// <summary>Everything the audio thread keeps for one channel or bus.</summary>
internal sealed class StripRt
{
    public StripRt(string id, StripKind kind, bool stereo, int maxFrames, double sampleRate)
    {
        Id = id;
        Kind = kind;
        Stereo = stereo;
        L = new float[maxFrames];
        R = new float[maxFrames];
        PreL = new float[maxFrames];
        PreR = new float[maxFrames];
        PostL = new float[maxFrames];
        PostR = new float[maxFrames];
        InL = new float[maxFrames];
        InR = new float[maxFrames];
        TapInL = new float[maxFrames];
        TapInR = new float[maxFrames];
        TapPreEqL = new float[maxFrames];
        TapPreEqR = new float[maxFrames];
        TapPostEqL = new float[maxFrames];
        TapPostEqR = new float[maxFrames];
        Filters = new StripFilters(sampleRate, maxFrames);
        Gate = new Gate(sampleRate, maxFrames);
        Comp = new Compressor(sampleRate, maxFrames);
        Drive = new Saturator(sampleRate, maxFrames);
        DynEq = new DynamicEq(sampleRate, maxFrames);
        Raw = new float[maxFrames];
        var maxDelay = (int)(0.1 * sampleRate);
        InDelayL = new DelayLine(maxDelay);
        InDelayR = new DelayLine(maxDelay);
    }

    public readonly string Id;
    public readonly StripKind Kind;
    public readonly bool Stereo;
    public readonly float[] L, R, PreL, PreR, PostL, PostR, InL, InR;
    /// <summary>Copies at the earlier tap points, filled only while some send uses them.</summary>
    public readonly float[] TapInL, TapInR, TapPreEqL, TapPreEqR, TapPostEqL, TapPostEqR;
    public volatile bool NeedTapIn, NeedTapPreEq, NeedTapPostEq;
    public readonly StripFilters Filters;
    public readonly Gate Gate;
    public readonly Compressor Comp;
    public readonly Saturator Drive;
    public readonly DynamicEq DynEq;
    /// <summary>Channels: this buffer's input after the trim, for other strips' gates and compressors to listen to.</summary>
    public readonly float[] Raw;
    public volatile RackSlot[] Rack = [];
    public volatile StripRt? GateKey, CompKey;
    public volatile bool DynamicsFirst;
    /// <summary>Replaced (on the control thread) by a longer one when the delay needs more room.</summary>
    public volatile DelayLine InDelayL, InDelayR;
    public string? LastFilters, LastGate, LastComp, LastDrive, LastDynEq;

    // Targets, written by the control thread (single floats and ints, so always whole).
    public volatile int Input = -1;            // channels: which driver input feeds this strip
    public volatile float Trim = 1;            // includes polarity
    public volatile float FaderL = 1, FaderR = 1; // fader, mute and pan for the main mix
    public volatile float Fader = 1;           // fader and mute only (for sends to mono buses)
    public volatile int InDelay;               // samples
    public volatile bool Muted;
    /// <summary>Hard muted: the direct out goes silent too.</summary>
    public volatile bool HardMuted;

    public SmoothGain GTrim, GFaderL, GFaderR;

    // Meters (written by the audio thread, taken by the control thread).
    public float MeterIn, MeterL, MeterR, MeterGate, MeterComp, MeterRack, MeterDynEq;
}

/// <summary>A feed from one strip into another: a send, a group assignment, or a strip's place in the main mix.</summary>
internal sealed class Feed
{
    public Feed(StripRt from, StripRt to, SendTap tap)
    {
        From = from;
        To = to;
        Tap = tap;
        Pre = tap != SendTap.Post;
    }

    public readonly StripRt From;
    public readonly StripRt To;
    public readonly SendTap Tap;
    /// <summary>Taken anywhere before the fader (so a mute must silence it).</summary>
    public readonly bool Pre;
    public volatile float GainL, GainR;
    /// <summary>Samples this feed waits, so it arrives in line with the slowest path into the same bus (plugins, look-ahead).</summary>
    public volatile int Delay;
    public volatile DelayLine? DL, DR;
    public SmoothGain SL, SR;
    public string Key => $"{From.Id}>{To.Id}>{Tap}";
}

internal enum OutSource
{
    Off,
    Tone,
    Input,
    StripMono,
    StripLeft,
    StripRight,
    ChannelDirect,
}

internal sealed class OutputRt
{
    public OutputRt(int maxFrames, double sampleRate)
    {
        Filters = new StripFilters(sampleRate, maxFrames);
        Limiter = new Limiter(sampleRate, maxFrames);
        Delay = new DelayLine((int)(0.1 * sampleRate) + 8);
    }

    public volatile OutSource Kind = OutSource.Off;
    public volatile int Input = -1;
    public volatile StripRt? Strip;
    /// <summary>For a channel's direct out: where in the channel it is taken from.</summary>
    public volatile SendTap Tap = SendTap.Post;
    public volatile float Gain = 1;
    public volatile int DelaySamples;
    public volatile bool Limit;
    public readonly StripFilters Filters;
    public readonly Limiter Limiter;
    public volatile DelayLine Delay;
    public SmoothGain G;
    public string? LastFilters, LastLimiter;
}

/// <summary>Which strips and feeds exist. Replaced as a whole when buses or routes are added or removed.</summary>
internal sealed class Topology
{
    public StripRt[] Channels = [];
    /// <summary>Every strip that is not a channel, in the routing map's order, each with the feeds it sends on.</summary>
    public StripRt[] Steps = [];
    public Feed[][] StepFeeds = [];
    public StripRt Main = null!, Mono = null!, Stream = null!;
    public StripRt[] All = [];
    public Feed[] ChannelFeeds = [];
    /// <summary>The bus the test signal plays into, if any.</summary>
    public StripRt? ToneInto;
    public bool ToneUsed;
    public ToneKind ToneKind;
    public double ToneFreq = 1000;
    public float ToneGain = 0.125893f;
    /// <summary>The strips heard on solo bus 1 and solo bus 2 (empty: that bus plays the main mix).</summary>
    public StripRt[] Soloed = [], Soloed2 = [];
    public bool SoloAfl, SoloAfl2;
    public float SoloGain = 1, SoloGain2 = 1;
    public bool StreamFromMain = true;
    public int StreamDelay;
    public StripRt? Analyse;
}

/// <summary>
/// NEX-Audio's mixer on the audio thread: channel strips, effect returns, groups, mixes, the main mix, mono,
/// matrices and the stream mix, then the 32 outputs with their speaker processing.
/// The control thread calls <see cref="Load"/> with the whole mix after any change; the audio thread calls
/// <see cref="Process"/> every buffer. Nothing in <see cref="Process"/> allocates, locks or waits.
/// </summary>
public sealed class MixEngine
{
    private readonly int _inputs, _outputs, _maxFrames;
    private readonly double _sr;
    private readonly Dictionary<string, StripRt> _strips = [];
    private readonly Dictionary<string, Feed> _feeds = [];
    private readonly Dictionary<string, RackSlot> _slots = [];
    private readonly OutputRt[] _outs;
    private readonly Func<RackItem, Processor?>? _pluginFactory;
    private readonly float[] _mono;
    private Topology _topo = new();
    private double _tonePhase;
    private readonly object _gate = new();

    public MixEngine(int inputs, int outputs, int maxFrames, double sampleRate, Func<RackItem, Processor?>? pluginFactory = null)
    {
        _inputs = inputs;
        _outputs = outputs;
        _maxFrames = maxFrames;
        _sr = sampleRate;
        _pluginFactory = pluginFactory;
        _outs = new OutputRt[outputs];
        for (var o = 0; o < outputs; o++) _outs[o] = new OutputRt(maxFrames, sampleRate);
        _mono = new float[maxFrames];
        _scrL = new float[maxFrames];
        _scrR = new float[maxFrames];
        _tone = new float[maxFrames];
        _monitor = new StripRt("mon", StripKind.Mix, true, maxFrames, sampleRate);
        _monitor2 = new StripRt("mon2", StripKind.Mix, true, maxFrames, sampleRate);
        Loudness = new LoudnessMeter(sampleRate);
        Analyser = new Analyser(sampleRate);
        StreamTap = new StreamTap(sampleRate);
    }

    public double SampleRate => _sr;
    public int MaxFrames => _maxFrames;
    public LoudnessMeter Loudness { get; }
    public Analyser Analyser { get; }
    /// <summary>The stream mix for sending to the Live Output PC.</summary>
    public StreamTap StreamTap { get; }

    /// <summary>How late the main mix is compared with the inputs, from plugins and look-ahead (samples).</summary>
    public int MixLatency { get; private set; }

    /// <summary>Which strip the spectrum analyser listens to.</summary>
    public string? AnalyseId { get; set; }

    // ---- Control thread -------------------------------------------------------------------

    /// <summary>Brings the audio thread up to date with the whole mix. Only changed settings are passed on.</summary>
    public void Load(MixState s)
    {
        lock (_gate)
        {
            var topo = new Topology();
            var seen = new HashSet<string>();
            StripRt Get(Strip st)
            {
                seen.Add(st.Id);
                if (!_strips.TryGetValue(st.Id, out var rt) || rt.Stereo != st.Stereo || rt.Kind != st.Kind)
                {
                    rt = new StripRt(st.Id, st.Kind, st.Stereo, _maxFrames, _sr);
                    _strips[st.Id] = rt;
                }
                return rt;
            }

            topo.Channels = s.Channels.Select(Get).ToArray();
            for (var i = 0; i < topo.Channels.Length; i++)
            {
                var c = s.Channels[i];
                var n = c.UseAlt && c.AltInput > 0 ? c.AltInput : c.Input;
                topo.Channels[i].Input = n >= 1 && n <= _inputs ? n - 1 : -1;
            }
            var order = Routing.Order(s) ?? s.AllStrips().OrderBy(x => Routing.Stage(x.Kind)).ToList();
            var steps = order.Where(x => x.Kind != StripKind.Channel).ToList();
            topo.Steps = steps.Select(Get).ToArray();
            topo.Main = Get(s.Main);
            topo.Mono = Get(s.Mono);
            topo.Stream = Get(s.Stream);
            topo.All = s.AllStrips().Select(Get).ToArray();
            foreach (var dead in _strips.Keys.Where(k => !seen.Contains(k)).ToList()) _strips.Remove(dead);

            // Settings for every strip's own processing, with its VCA masters and mute groups.
            var controls = ControlRules.Effects(s);
            foreach (var st in s.AllStrips()) Configure(st, _strips[st.Id], controls.GetValueOrDefault(st.Id));

            // Feeds: every route to the main mix, group assignment and send, grouped by the strip that sends.
            var feedsSeen = new HashSet<string>();
            var bySource = new Dictionary<string, List<Feed>>();
            Feed MakeFeed(Strip from, Strip to, SendTap tap, float gl, float gr)
            {
                var f = new Feed(_strips[from.Id], _strips[to.Id], tap);
                if (_feeds.TryGetValue(f.Key, out var old)) f = old;
                else _feeds[f.Key] = f;
                f.GainL = gl;
                f.GainR = gr;
                feedsSeen.Add(f.Key);
                if (!bySource.TryGetValue(from.Id, out var list)) bySource[from.Id] = list = [];
                list.Add(f);
                return f;
            }

            var anySolo = s.AllStrips().Any(x => x.Solo);
            foreach (var src in s.AllStrips())
            {
                if (src.Kind is StripKind.Channel or StripKind.Fx or StripKind.Group && src.ToMain)
                {
                    var (pl, pr) = PanGains(src.Pan, src.Stereo);
                    MakeFeed(src, s.Main, SendTap.Post, pl, pr);
                }
                foreach (var g in src.Groups)
                    if (s.Find(g) is { } grp && Routing.CanGroup(src, grp))
                    {
                        var (pl, pr) = PanGains(src.Pan, src.Stereo);
                        MakeFeed(src, grp, SendTap.Post, pl, pr);
                    }
                foreach (var send in src.Sends)
                {
                    if (!send.On || send.Level <= Db.Floor || s.Find(send.Bus) is not { } bus || !Routing.CanSend(src, bus)) continue;
                    var g = Db.ToGain(send.Level);
                    var (pl, pr) = bus.Stereo ? PanGains(send.Pan ?? src.Pan, src.Stereo) : (1f, 1f);
                    MakeFeed(src, bus, send.Tap, g * pl, g * pr);
                }
                // The stream's own balance: every strip has a stream level, taken before its fader.
                if (s.StreamSettings.Source == StreamSource.Own && src.Kind is StripKind.Channel or StripKind.Fx or StripKind.Group && src.StreamOn)
                {
                    var g = Db.ToGain(src.StreamLevel);
                    var (pl, pr) = PanGains(src.Pan, src.Stereo);
                    MakeFeed(src, s.Stream, SendTap.Pre, g * pl, g * pr);
                }
            }
            foreach (var dead in _feeds.Keys.Where(k => !feedsSeen.Contains(k)).ToList()) _feeds.Remove(dead);
            topo.StreamFromMain = s.StreamSettings.Source == StreamSource.Main;

            // Line everything up: each bus waits for the slowest path into it, and every faster path is delayed to match.
            var arrive = new Dictionary<string, int>();
            var into = new Dictionary<string, List<Feed>>();
            foreach (var f in bySource.Values.SelectMany(l => l))
            {
                if (!into.TryGetValue(f.To.Id, out var l)) into[f.To.Id] = l = [];
                l.Add(f);
            }
            int Out(Strip x) => arrive.GetValueOrDefault(x.Id) + RackLatency(x);
            int Early(Strip x) => arrive.GetValueOrDefault(x.Id) + x.Rack.Where(r => r.Pre).Sum(LatencyOf);
            int Leaves(Feed f, Strip from) => f.Tap is SendTap.Input or SendTap.PreEq or SendTap.PostEq ? Early(from) : Out(from);
            var byId = s.AllStrips().ToDictionary(x => x.Id);
            foreach (var x in order)
            {
                var at = 0;
                if (x.Kind == StripKind.Mono || x.Kind == StripKind.Stream && topo.StreamFromMain) at = Out(s.Main);
                if (into.TryGetValue(x.Id, out var feeds))
                    foreach (var f in feeds) at = Math.Max(at, Leaves(f, byId[f.From.Id]));
                arrive[x.Id] = at;
            }
            foreach (var f in bySource.Values.SelectMany(l => l))
            {
                var d = Math.Max(0, arrive[f.To.Id] - Leaves(f, byId[f.From.Id]));
                if (d > 0 && (f.DL is null || f.DL.MaxDelay < d))
                {
                    f.DL = new DelayLine(Math.Max(d, 2048));
                    f.DR = new DelayLine(Math.Max(d, 2048));
                }
                f.Delay = d;
            }
            MixLatency = Out(s.Main);
            _latency = s.AllStrips().ToDictionary(x => x.Id, x => new PathLatency(x.Id, RackLatency(x), arrive.GetValueOrDefault(x.Id), Out(x)));
            topo.ChannelFeeds = s.Channels.SelectMany(c => bySource.GetValueOrDefault(c.Id) ?? []).ToArray();
            topo.StepFeeds = steps.Select(x => (bySource.GetValueOrDefault(x.Id) ?? []).ToArray()).ToArray();

            topo.StreamDelay = (int)(Math.Clamp(s.StreamSettings.DelayMs, 0, 1000) * 0.001 * _sr);
            if (_streamDelayL is null || _streamDelayL.MaxDelay < topo.StreamDelay)
            {
                _streamDelayL = new DelayLine(Math.Max(topo.StreamDelay, (int)_sr));
                _streamDelayR = new DelayLine(Math.Max(topo.StreamDelay, (int)_sr));
            }

            var soloed = anySolo ? s.AllStrips().Where(x => x.Solo).Select(x => _strips[x.Id]).ToArray() : [];
            topo.Soloed = s.Solo.ToBus(1) ? soloed : [];
            topo.Soloed2 = s.Solo.ToBus(2) ? soloed : [];
            topo.SoloAfl = s.Solo.Mode == SoloMode.Afl;
            topo.SoloAfl2 = s.Solo.Mode2 == SoloMode.Afl;
            topo.SoloGain = Db.ToGain(s.Solo.Level);
            topo.SoloGain2 = Db.ToGain(s.Solo.Level2);
            topo.Analyse = AnalyseId is { } a && _strips.TryGetValue(a, out var ar) ? ar : null;

            // Outputs (a channel's direct out comes from its own tap point).
            _directTaps = s.Channels.ToDictionary(c => c.Id, c => c.DirectTap);
            for (var o = 0; o < _outputs; o++)
            {
                var p = o < s.Outputs.Count ? s.Outputs[o] : new OutputPatch { Number = o + 1 };
                ConfigureOutput(p, _outs[o]);
            }

            // The test signal.
            var tone = s.Tone ?? new ToneSettings();
            topo.ToneKind = tone.Kind;
            topo.ToneFreq = Math.Clamp(tone.Freq, 20, 20000);
            topo.ToneGain = Db.ToGain(Math.Min(tone.Level, ToneSettings.MaxLevel));
            topo.ToneInto = tone.On && tone.Bus.Length > 0 && _strips.TryGetValue(tone.Bus, out var tb) && tb.Kind is not (StripKind.Channel or StripKind.Stream) ? tb : null;
            topo.ToneUsed = topo.ToneInto is not null || _outs.Any(x => x.Kind == OutSource.Tone);

            // Only strips with a send or direct out at an early tap point copy their sound there.
            var live = bySource.Values.SelectMany(l => l).ToList();
            bool Wants(StripRt rt, SendTap tap) =>
                live.Any(f => f.From == rt && f.Tap == tap) || _outs.Any(x => x.Kind == OutSource.ChannelDirect && x.Strip == rt && x.Tap == tap);
            foreach (var rt in _strips.Values)
            {
                rt.NeedTapIn = Wants(rt, SendTap.Input);
                rt.NeedTapPreEq = Wants(rt, SendTap.PreEq);
                rt.NeedTapPostEq = Wants(rt, SendTap.PostEq);
            }

            Volatile.Write(ref _topo, topo);
        }
    }

    private static (float L, float R) PanGains(double pan, bool stereoSource)
    {
        pan = Math.Clamp(pan, -1, 1);
        if (stereoSource)
        {
            // Balance: turning one side down, the centre stays at full level.
            return ((float)Math.Min(1, 1 - pan), (float)Math.Min(1, 1 + pan));
        }
        // Constant power, -3 dB in the centre.
        var a = (pan + 1) * Math.PI / 4;
        return ((float)Math.Cos(a), (float)Math.Sin(a));
    }

    private int RackLatency(Strip s) => s.Rack.Sum(r => LatencyOf(r));

    /// <summary>The delay an effect adds, in samples.</summary>
    public int LatencyOf(RackItem r)
    {
        if (r.Type == "limiter") return Math.Clamp((int)Math.Round(r.P("lookahead", 1) * 0.001 * _sr), 1, (int)(0.005 * _sr));
        if (r.Type == "plugin" && _slots.TryGetValue(r.Id, out var slot)) return slot.Processor.Latency;
        return 0;
    }

    private void Configure(Strip s, StripRt rt, ControlEffect control = default)
    {
        rt.Trim = Db.ToGain(s.Trim) * (s.Invert ? -1 : 1);
        var inDelay = (int)(Math.Clamp(s.DelayMs, 0, MixParams.MaxDelayMs) * 0.001 * _sr);
        if (inDelay > rt.InDelayL.MaxDelay)
        {
            // Room for the longest delay, made here (never on the audio thread).
            var room = (int)(MixParams.MaxDelayMs * 0.001 * _sr) + 8;
            rt.InDelayL = new DelayLine(room);
            rt.InDelayR = new DelayLine(room);
        }
        rt.InDelay = inDelay;
        // A mute, a hard mute or a group's mute all silence the strip; a VCA master moves its fader.
        var muted = s.Mute || s.HardMute || control.Muted;
        var fader = muted ? 0f : Db.ToGain(ControlRules.Effective(s, control));
        rt.Muted = muted;
        rt.HardMuted = s.HardMute;
        rt.Fader = fader;
        if (s.Stereo)
        {
            var (bl, br) = PanGains(s.Pan, true);
            rt.FaderL = fader * bl;
            rt.FaderR = fader * br;
        }
        else
        {
            rt.FaderL = fader;
            rt.FaderR = fader;
        }

        var fs = FilterSettings.From(s);
        var key = System.Text.Json.JsonSerializer.Serialize(fs, MixState.Json);
        if (key != rt.LastFilters)
        {
            rt.LastFilters = key;
            rt.Filters.Configure(fs);
        }
        key = System.Text.Json.JsonSerializer.Serialize(s.Gate, MixState.Json);
        if (key != rt.LastGate)
        {
            rt.LastGate = key;
            rt.Gate.Configure(s.Gate.Clone());
        }
        key = System.Text.Json.JsonSerializer.Serialize(s.Comp, MixState.Json);
        if (key != rt.LastComp)
        {
            rt.LastComp = key;
            rt.Comp.Configure(s.Comp.Clone());
        }
        key = System.Text.Json.JsonSerializer.Serialize(s.Drive, MixState.Json);
        if (key != rt.LastDrive)
        {
            rt.LastDrive = key;
            rt.Drive.Configure(s.Drive.Clone());
        }
        key = System.Text.Json.JsonSerializer.Serialize(s.DynEq, MixState.Json);
        if (key != rt.LastDynEq)
        {
            rt.LastDynEq = key;
            rt.DynEq.Configure(s.DynEq.Clone());
        }
        rt.DynamicsFirst = s.Order == ProcessOrder.DynamicsFirst;
        // A sidechain: the gate or compressor listens to another channel.
        rt.GateKey = s.Kind == StripKind.Channel && s.Gate.Key is { Length: > 0 } gk && gk != s.Id && _strips.TryGetValue(gk, out var gks) && gks.Kind == StripKind.Channel ? gks : null;
        rt.CompKey = s.Comp.Key is { Length: > 0 } ck && ck != s.Id && _strips.TryGetValue(ck, out var cks) && cks.Kind == StripKind.Channel ? cks : null;

        // The rack: keep processors that are still there (their memory carries on), make new ones.
        var slots = new List<RackSlot>();
        var changed = rt.Rack.Length != s.Rack.Count;
        for (var i = 0; i < s.Rack.Count; i++)
        {
            var item = s.Rack[i];
            if (!_slots.TryGetValue(item.Id, out var slot) || slot.Type != item.Type)
            {
                var proc = item.Type == "plugin" ? _pluginFactory?.Invoke(item) : Effects.Create(item, _sr, _maxFrames);
                if (proc is null) continue;
                slot = new RackSlot(item.Id, item.Type, proc);
                _slots[item.Id] = slot;
            }
            slot.Bypass = item.Bypass;
            slot.Pre = item.Pre;
            slot.Latency = LatencyOf(item);
            var cfg = System.Text.Json.JsonSerializer.Serialize(item, MixState.Json);
            if (cfg != slot.LastConfig)
            {
                slot.LastConfig = cfg;
                slot.Processor.Configure(item.Clone());
            }
            slots.Add(slot);
            if (i >= rt.Rack.Length || rt.Rack[i] != slot) changed = true;
        }
        if (changed) rt.Rack = [.. slots];
    }

    /// <summary>Forgets processors no longer in any rack (after a scene recall or removing an effect).</summary>
    public IReadOnlyList<Processor> DropUnused(MixState s)
    {
        lock (_gate)
        {
            var used = s.AllStrips().SelectMany(x => x.Rack).Select(r => r.Id).ToHashSet();
            var dropped = new List<Processor>();
            foreach (var id in _slots.Keys.Where(k => !used.Contains(k)).ToList())
            {
                dropped.Add(_slots[id].Processor);
                _slots.Remove(id);
            }
            return dropped;
        }
    }

    private void ConfigureOutput(OutputPatch p, OutputRt rt)
    {
        var src = (p.Source ?? "off").Trim().ToLowerInvariant();
        OutSource kind = OutSource.Off;
        var input = -1;
        StripRt? strip = null;
        if (src == "tone") kind = OutSource.Tone;
        else if (src.StartsWith("in:") && int.TryParse(src[3..], out var n) && n >= 1 && n <= _inputs)
        {
            kind = OutSource.Input;
            input = n - 1;
        }
        else if (src.StartsWith("ch:") && int.TryParse(src[3..], out var c) && _strips.TryGetValue($"ch{c}", out var cs))
        {
            kind = OutSource.ChannelDirect;
            strip = cs;
            rt.Tap = _directTaps.GetValueOrDefault(cs.Id, SendTap.Post);
        }
        else if (src.Length > 0 && src != "off")
        {
            var parts = src.Split(':');
            var id = parts[0];
            if (id is "mon" or "mon2")
            {
                kind = parts.Length > 1 && parts[1] == "r" ? OutSource.StripRight : OutSource.StripLeft;
                strip = id == "mon" ? _monitor : _monitor2;
            }
            else if (_strips.TryGetValue(id, out var st))
            {
                strip = st;
                kind = !st.Stereo ? OutSource.StripMono : parts.Length > 1 && parts[1] == "r" ? OutSource.StripRight : OutSource.StripLeft;
            }
        }
        rt.Kind = kind;
        rt.Input = input;
        rt.Strip = strip;
        rt.Gain = p.Mute ? 0 : Db.ToGain(p.Level) * (p.Invert ? -1 : 1);
        var delay = (int)(Math.Clamp(p.DelayMs, 0, MixParams.MaxDelayMs) * 0.001 * _sr);
        if (delay > rt.Delay.MaxDelay) rt.Delay = new DelayLine((int)(MixParams.MaxDelayMs * 0.001 * _sr) + 8);
        rt.DelaySamples = delay;
        rt.Limit = p.Limit;
        var fs = FilterSettings.From(p);
        var key = System.Text.Json.JsonSerializer.Serialize(fs, MixState.Json);
        if (key != rt.LastFilters)
        {
            rt.LastFilters = key;
            rt.Filters.Configure(fs);
        }
        var lk = $"{p.Limit}|{p.Ceiling}";
        if (lk != rt.LastLimiter)
        {
            rt.LastLimiter = lk;
            rt.Limiter.Configure(new Limiter.Settings(p.Limit, p.Ceiling, 60, 1));
        }
    }

    // The two solo / monitor buses (not part of the saved mix).
    private readonly StripRt _monitor, _monitor2;
    private Dictionary<string, SendTap> _directTaps = [];
    private Dictionary<string, PathLatency> _latency = [];
    private readonly float[] _scrL, _scrR, _tone;
    private uint _noise = 0x9E3779B9;
    private float _p0, _p1, _p2, _p3, _p4, _p5, _p6;

    /// <summary>For every strip: the delay its own effects and plugins add, and how late its sound is compared with the inputs (samples).</summary>
    public IReadOnlyDictionary<string, PathLatency> Latencies
    {
        get { lock (_gate) return new Dictionary<string, PathLatency>(_latency); }
    }
    private DelayLine? _streamDelayL, _streamDelayR;

    // ---- Audio thread ----------------------------------------------------------------------

    public void Process(float[][] input, float[][] output, int frames)
    {
        var t = Volatile.Read(ref _topo);
        if (t.Main is null)
        {
            for (var o = 0; o < output.Length; o++) Array.Clear(output[o], 0, frames);
            return;
        }

        // 1. Channels. First every channel's input (after its trim), so any channel can key another's gate or compressor.
        foreach (var ch in t.Channels)
        {
            var src = ch.Input;
            if (src >= 0 && src < input.Length)
            {
                var a = input[src];
                var g = ch.Trim;
                for (var n = 0; n < frames; n++) ch.Raw[n] = a[n] * g;
            }
            else Array.Clear(ch.Raw, 0, frames);
        }
        foreach (var ch in t.Channels)
        {
            var src = ch.Input;
            if (src >= 0 && src < input.Length) Array.Copy(input[src], ch.L, frames);
            else Array.Clear(ch.L, 0, frames);
            RunStrip(ch, frames, isChannel: true);
        }

        // 2. Clear every bus input (mono / sub is filled from the main mix when its turn comes).
        foreach (var b in t.Steps) Clear(b, frames);

        // 3. The test signal, once for every place that plays it.
        if (t.ToneUsed) MakeTone(t, frames);

        // 4. Channel feeds, then every other strip in the routing map's order: each one runs once everything feeding it has.
        Mix(t.ChannelFeeds, frames);
        var steps = t.Steps;
        var stepFeeds = t.StepFeeds;
        for (var i = 0; i < steps.Length; i++)
        {
            var b = steps[i];
            if (b == t.Mono)
                for (var n = 0; n < frames; n++) b.InL[n] = (t.Main.PostL[n] + t.Main.PostR[n]) * 0.5f;
            else if (b == t.Stream && t.StreamFromMain)
            {
                Array.Copy(t.Main.PreL, b.InL, frames);
                Array.Copy(t.Main.PreR, b.InR, frames);
            }
            if (b == t.ToneInto)
            {
                var tone = _tone;
                for (var n = 0; n < frames; n++) b.InL[n] += tone[n];
                if (b.Stereo)
                    for (var n = 0; n < frames; n++) b.InR[n] += tone[n];
            }
            RunBus(b, frames);
            if (b == t.Stream)
            {
                if (t.StreamDelay > 0 && _streamDelayL is not null && _streamDelayR is not null)
                {
                    _streamDelayL.Process(b.PostL, frames, t.StreamDelay);
                    _streamDelayR.Process(b.PostR, frames, t.StreamDelay);
                }
                Loudness.Push(b.PostL, b.PostR, frames);
                StreamTap.Write(b.PostL, b.PostR, frames);
            }
            if (i < stepFeeds.Length) Mix(stepFeeds[i], frames);
        }

        // 9. Solo / monitor buses: the soloed strips, or the main mix when nothing is soloed there.
        Monitor(_monitor, t.Soloed, t.SoloAfl, t.SoloGain, t.Main, frames);
        Monitor(_monitor2, t.Soloed2, t.SoloAfl2, t.SoloGain2, t.Main, frames);

        // 10. Outputs.
        for (var o = 0; o < output.Length && o < _outs.Length; o++)
        {
            var rt = _outs[o];
            var dst = output[o];
            switch (rt.Kind)
            {
                case OutSource.Tone:
                    Array.Copy(_tone, dst, frames);
                    break;
                case OutSource.Input when rt.Input >= 0 && rt.Input < input.Length:
                    Array.Copy(input[rt.Input], dst, frames);
                    break;
                case OutSource.ChannelDirect when rt.Strip is { } cs:
                    // A normal mute leaves a direct out taken before the fader going (a recording carries on); a hard mute silences it.
                    if (cs.HardMuted || (cs.Muted && rt.Tap == SendTap.Post)) Array.Clear(dst, 0, frames);
                    else Array.Copy(rt.Tap switch
                    {
                        SendTap.Input => cs.TapInL,
                        SendTap.PreEq => cs.TapPreEqL,
                        SendTap.PostEq => cs.TapPostEqL,
                        SendTap.Pre => cs.PreL,
                        _ => cs.PostL,
                    }, dst, frames);
                    break;
                case OutSource.StripMono when rt.Strip is { } m:
                    Array.Copy(m.PostL, dst, frames);
                    break;
                case OutSource.StripLeft when rt.Strip is { } sl:
                    Array.Copy(sl.PostL, dst, frames);
                    break;
                case OutSource.StripRight when rt.Strip is { } sr:
                    Array.Copy(sr.PostR, dst, frames);
                    break;
                default:
                    Array.Clear(dst, 0, frames);
                    break;
            }
            if (rt.Kind == OutSource.Off) continue;
            rt.G.Apply(dst, frames, rt.Gain);
            rt.Filters.Run(dst, null, frames);
            rt.Delay.Process(dst, frames, rt.DelaySamples);
            if (rt.Limit) rt.Limiter.Run(dst, null, frames);
        }
        for (var o = _outs.Length; o < output.Length; o++) Array.Clear(output[o], 0, frames);

        if (t.Analyse is { } an) Analyser.Push(an.PostL, an.Stereo ? an.PostR : null, frames);
    }

    private static void Tap(float[] l, float[]? r, float[] toL, float[] toR, int frames)
    {
        Array.Copy(l, toL, frames);
        if (r is not null) Array.Copy(r, toR, frames);
    }

    private static void Clear(StripRt b, int frames)
    {
        Array.Clear(b.InL, 0, frames);
        if (b.Stereo) Array.Clear(b.InR, 0, frames);
    }

    /// <summary>The test signal for this buffer: a sine, or white or pink noise, at the chosen level (never above -6 dBFS).</summary>
    private void MakeTone(Topology t, int frames)
    {
        var dst = _tone;
        var g = t.ToneGain;
        switch (t.ToneKind)
        {
            case ToneKind.Sine:
            {
                var step = 2 * Math.PI * t.ToneFreq / _sr;
                for (var n = 0; n < frames; n++) dst[n] = g * (float)Math.Sin(_tonePhase + n * step);
                _tonePhase = (_tonePhase + frames * step) % (2 * Math.PI);
                break;
            }
            case ToneKind.White:
                // White noise at the same RMS as a sine of this level (a full-scale uniform noise has an RMS of 0.577).
                for (var n = 0; n < frames; n++) dst[n] = g * 1.2247f * NextNoise();
                break;
            default:
                // Pink noise from white through a set of gentle filters (a well-known recipe), scaled to about the same loudness.
                for (var n = 0; n < frames; n++)
                {
                    var w = NextNoise();
                    _p0 = 0.99886f * _p0 + w * 0.0555179f;
                    _p1 = 0.99332f * _p1 + w * 0.0750759f;
                    _p2 = 0.96900f * _p2 + w * 0.1538520f;
                    _p3 = 0.86650f * _p3 + w * 0.3104856f;
                    _p4 = 0.55000f * _p4 + w * 0.5329522f;
                    _p5 = -0.7616f * _p5 - w * 0.0168980f;
                    var pink = _p0 + _p1 + _p2 + _p3 + _p4 + _p5 + _p6 + w * 0.5362f;
                    _p6 = w * 0.115926f;
                    dst[n] = Math.Clamp(g * pink * 0.33f, -1f, 1f);
                }
                break;
        }
    }

    /// <summary>A fast random number from -1 to 1 (xorshift; no allocation, fine for the audio thread).</summary>
    private float NextNoise()
    {
        var x = _noise;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _noise = x;
        return (x / (float)uint.MaxValue) * 2f - 1f;
    }

    private void Mix(Feed[] feeds, int frames)
    {
        foreach (var f in feeds)
        {
            var s = f.From;
            float[] l, r;
            if (f.Tap is SendTap.Input or SendTap.PreEq or SendTap.PostEq)
            {
                (l, r) = f.Tap switch
                {
                    SendTap.Input => (s.TapInL, s.TapInR),
                    SendTap.PreEq => (s.TapPreEqL, s.TapPreEqR),
                    _ => (s.TapPostEqL, s.TapPostEqR),
                };
                if (!s.Stereo) r = l;
            }
            else
            {
                l = f.Pre ? s.PreL : s.PostL;
                r = s.Stereo ? f.Pre ? s.PreR : s.PostR : l;
            }
            // A faster path waits for the slowest one into the same bus.
            var delay = f.Delay;
            if (delay > 0 && f.DL is { } dl && f.DR is { } dr)
            {
                Array.Copy(l, _scrL, frames);
                dl.Process(_scrL, frames, delay);
                if (r != l)
                {
                    Array.Copy(r, _scrR, frames);
                    dr.Process(_scrR, frames, delay);
                    r = _scrR;
                }
                else r = _scrL;
                l = _scrL;
            }
            var d = f.To;
            // A muted strip feeds nothing, before or after its fader (after it, the fader is already at zero).
            var muted = f.Pre && s.Muted;
            var gl = muted ? 0 : f.GainL;
            var gr = muted ? 0 : f.GainR;
            if (d.Stereo)
            {
                f.SL.Mix(l, d.InL, frames, gl);
                f.SR.Mix(r, d.InR, frames, gr);
            }
            else if (s.Stereo)
            {
                // Stereo into a mono bus: both sides at half.
                f.SL.Mix(l, d.InL, frames, gl * 0.5f);
                f.SR.Mix(r, d.InL, frames, gr * 0.5f);
            }
            else f.SL.Mix(l, d.InL, frames, gl);
        }
    }

    private static void Monitor(StripRt mon, StripRt[] soloed, bool afl, float gain, StripRt main, int frames)
    {
        if (soloed.Length == 0)
        {
            Array.Copy(main.PostL, mon.PostL, frames);
            Array.Copy(main.PostR, mon.PostR, frames);
        }
        else
        {
            Array.Clear(mon.PostL, 0, frames);
            Array.Clear(mon.PostR, 0, frames);
            foreach (var s in soloed)
            {
                var l = afl ? s.PostL : s.PreL;
                var r = s.Stereo ? afl ? s.PostR : s.PreR : l;
                for (var n = 0; n < frames; n++)
                {
                    mon.PostL[n] += l[n];
                    mon.PostR[n] += r[n];
                }
            }
        }
        if (gain != 1f)
            for (var n = 0; n < frames; n++)
            {
                mon.PostL[n] *= gain;
                mon.PostR[n] *= gain;
            }
    }

    private void RunBus(StripRt b, int frames)
    {
        Array.Copy(b.InL, b.L, frames);
        if (b.Stereo) Array.Copy(b.InR, b.R, frames);
        RunStrip(b, frames, isChannel: false);
    }

    /// <summary>One strip from its input to its fader: trim, delay, filters and EQ, gate, compressor, rack, line-up, fader.</summary>
    private void RunStrip(StripRt s, int frames, bool isChannel)
    {
        var r = s.Stereo ? s.R : null;
        s.GTrim.Apply(s.L, frames, s.Trim);
        if (r is not null)
            for (var n = 0; n < frames; n++) r[n] *= s.GTrim.Current;
        var pin = Dsp.Dsp.Peak(s.L, frames);
        if (r is not null) pin = Math.Max(pin, Dsp.Dsp.Peak(r, frames));
        if (pin > s.MeterIn) s.MeterIn = pin;

        if (s.InDelay > 0)
        {
            s.InDelayL.Process(s.L, frames, s.InDelay);
            if (r is not null) s.InDelayR.Process(r, frames, s.InDelay);
        }
        if (s.NeedTapIn) Tap(s.L, r, s.TapInL, s.TapInR, frames);
        // Low and high cut, then the drive, then any inserts placed before the EQ and dynamics.
        s.Filters.RunCuts(s.L, r, frames);
        s.Drive.Run(s.L, r, frames);
        var rack = s.Rack;
        var rackGr = 0f;
        foreach (var slot in rack)
        {
            if (!slot.Pre) continue;
            slot.Run(s.L, r, frames);
            if (slot.Processor.Reduction > rackGr) rackGr = slot.Processor.Reduction;
        }
        s.Gate.KeyInput = s.GateKey?.Raw;
        s.Comp.KeyInput = s.CompKey?.Raw;
        if (s.DynamicsFirst)
        {
            s.Gate.Run(s.L, r, frames);
            s.Comp.Run(s.L, r, frames);
            if (s.NeedTapPreEq) Tap(s.L, r, s.TapPreEqL, s.TapPreEqR, frames);
            s.Filters.RunEq(s.L, r, frames);
            s.DynEq.Run(s.L, r, frames);
            if (s.NeedTapPostEq) Tap(s.L, r, s.TapPostEqL, s.TapPostEqR, frames);
        }
        else
        {
            if (s.NeedTapPreEq) Tap(s.L, r, s.TapPreEqL, s.TapPreEqR, frames);
            s.Filters.RunEq(s.L, r, frames);
            s.DynEq.Run(s.L, r, frames);
            if (s.NeedTapPostEq) Tap(s.L, r, s.TapPostEqL, s.TapPostEqR, frames);
            s.Gate.Run(s.L, r, frames);
            s.Comp.Run(s.L, r, frames);
        }
        foreach (var slot in rack)
        {
            if (slot.Pre) continue;
            slot.Run(s.L, r, frames);
            if (slot.Processor.Reduction > rackGr) rackGr = slot.Processor.Reduction;
        }
        if (s.DynEq.Reduction > s.MeterDynEq) s.MeterDynEq = s.DynEq.Reduction;
        if (s.Gate.Reduction > s.MeterGate) s.MeterGate = s.Gate.Reduction;
        if (s.Comp.Reduction > s.MeterComp) s.MeterComp = s.Comp.Reduction;
        if (rackGr > s.MeterRack) s.MeterRack = rackGr;

        // Before the fader.
        Array.Copy(s.L, s.PreL, frames);
        if (r is not null) Array.Copy(r, s.PreR, frames);

        // After the fader (with the pan for the main mix).
        Array.Copy(s.L, s.PostL, frames);
        if (r is not null)
        {
            Array.Copy(r, s.PostR, frames);
            s.GFaderL.Apply(s.PostL, frames, s.FaderL);
            s.GFaderR.Apply(s.PostR, frames, s.FaderR);
        }
        else
        {
            s.GFaderL.Apply(s.PostL, frames, s.Fader);
            Array.Copy(s.PostL, s.PostR, frames);
        }
        var pl = Dsp.Dsp.Peak(s.PostL, frames);
        var pr = r is not null ? Dsp.Dsp.Peak(s.PostR, frames) : pl;
        if (pl > s.MeterL) s.MeterL = pl;
        if (pr > s.MeterR) s.MeterR = pr;
    }

    // ---- For the recorder (audio thread, after Process) -------------------------------------

    /// <summary>A channel after its processing, before its fader (mono, or the left of a stereo channel).</summary>
    public float[]? ChannelPre(int index)
    {
        var t = Volatile.Read(ref _topo);
        return index >= 0 && index < t.Channels.Length ? t.Channels[index].PreL : null;
    }

    /// <summary>The main mix as it leaves the desk.</summary>
    public (float[] L, float[] R)? MainOut
    {
        get
        {
            var t = Volatile.Read(ref _topo);
            return t.Main is null ? null : (t.Main.PostL, t.Main.PostR);
        }
    }

    // ---- Meters --------------------------------------------------------------------------

    /// <summary>The loudest levels since the last call, for every strip in mix order (channels, buses, main, mono, stream).</summary>
    public StripMeters[] TakeMeters()
    {
        var t = Volatile.Read(ref _topo);
        var result = new StripMeters[t.All.Length];
        for (var i = 0; i < t.All.Length; i++)
        {
            var s = t.All[i];
            result[i] = new StripMeters(s.MeterIn, s.MeterL, s.MeterR, s.MeterGate, s.MeterComp, s.MeterRack, s.Gate.On && !s.Gate.IsOpen, s.MeterDynEq);
            s.MeterIn = s.MeterL = s.MeterR = s.MeterGate = s.MeterComp = s.MeterRack = s.MeterDynEq = 0;
        }
        return result;
    }

    public (float L, float R) MonitorLevel()
    {
        var m = _monitor;
        return (Dsp.Dsp.Peak(m.PostL, Math.Min(_maxFrames, m.PostL.Length)), Dsp.Dsp.Peak(m.PostR, Math.Min(_maxFrames, m.PostR.Length)));
    }

    /// <summary>The processor for a rack item, if it is running (for showing its live state).</summary>
    public Processor? ProcessorOf(string rackId)
    {
        lock (_gate) return _slots.TryGetValue(rackId, out var s) ? s.Processor : null;
    }
}

/// <summary>
/// How late one strip's sound is (samples): <see cref="Own"/> its own effects and plugins add, <see cref="Arrives"/> when
/// its input is lined up, <see cref="Leaves"/> when it leaves after its own effects. Faster paths into a bus are delayed to match.
/// </summary>
public readonly record struct PathLatency(string Id, int Own, int Arrives, int Leaves);

public readonly record struct StripMeters(float In, float L, float R, float Gate, float Comp, float Rack, bool GateClosed, float DynEq = 0);
