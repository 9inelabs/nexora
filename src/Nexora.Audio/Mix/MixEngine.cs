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
        LatePreL = new float[maxFrames];
        LatePostL = new float[maxFrames];
        LatePostR = new float[maxFrames];
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
    public readonly float[] L, R, PreL, PreR, PostL, PostR, InL, InR, LatePreL, LatePostL, LatePostR;
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
    public readonly DelayLine InDelayL, InDelayR;
    public DelayLine? PdcL, PdcR, LatePre, LatePost, LatePostRd;
    public string? LastFilters, LastGate, LastComp, LastDrive, LastDynEq;

    // Targets, written by the control thread (single floats and ints, so always whole).
    public volatile int Input = -1;            // channels: which driver input feeds this strip
    public volatile float Trim = 1;            // includes polarity
    public volatile float FaderL = 1, FaderR = 1; // fader, mute and pan for the main mix
    public volatile float Fader = 1;           // fader and mute only (for sends to mono buses)
    public volatile int InDelay;               // samples
    public volatile int Pdc;                   // samples, to line up with the slowest strip in its stage
    public volatile int Late;                  // samples, extra for paths that skip the effect and group stage
    public volatile bool Muted;

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
    /// <summary>The source has already been delayed to line up with the effect and group stage.</summary>
    public volatile bool UseLate;
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
        Delay = new DelayLine((int)(0.5 * sampleRate) + 8);
    }

    public volatile OutSource Kind = OutSource.Off;
    public volatile int Input = -1;
    public volatile StripRt? Strip;
    public volatile float Gain = 1;
    public volatile int DelaySamples;
    public volatile bool Limit;
    public readonly StripFilters Filters;
    public readonly Limiter Limiter;
    public readonly DelayLine Delay;
    public SmoothGain G;
    public string? LastFilters, LastLimiter;
}

/// <summary>Which strips and feeds exist. Replaced as a whole when buses or routes are added or removed.</summary>
internal sealed class Topology
{
    public StripRt[] Channels = [];
    public StripRt[] Fx = [], Groups = [], Mixes = [], Matrices = [];
    public StripRt Main = null!, Mono = null!, Stream = null!;
    public StripRt[] All = [];
    public Feed[] ChannelFeeds = [], FxFeeds = [], GroupFeeds = [], MixFeeds = [], MainFeeds = [];
    public StripRt[] Soloed = [];
    public bool SoloAfl;
    public float SoloGain = 1;
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
    private const float ToneLevel = 0.125893f; // -18 dBFS

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
        _monitor = new StripRt("mon", StripKind.Mix, true, maxFrames, sampleRate);
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
            for (var i = 0; i < topo.Channels.Length; i++) topo.Channels[i].Input = i < _inputs ? i : -1;
            topo.Fx = s.BusesOf(StripKind.Fx).Select(Get).ToArray();
            topo.Groups = s.BusesOf(StripKind.Group).Select(Get).ToArray();
            topo.Mixes = s.BusesOf(StripKind.Mix).Select(Get).ToArray();
            topo.Matrices = s.BusesOf(StripKind.Matrix).Select(Get).ToArray();
            topo.Main = Get(s.Main);
            topo.Mono = Get(s.Mono);
            topo.Stream = Get(s.Stream);
            topo.All = s.AllStrips().Select(x => _strips[x.Id]).ToArray();
            foreach (var dead in _strips.Keys.Where(k => !seen.Contains(k)).ToList()) _strips.Remove(dead);

            // Settings for every strip's own processing.
            foreach (var st in s.AllStrips()) Configure(st, _strips[st.Id]);

            // Line everything up: strips in the same stage wait for the slowest one.
            var chLat = s.Channels.Select(RackLatency).DefaultIfEmpty(0).Max();
            foreach (var c in s.Channels) _strips[c.Id].Pdc = chLat - RackLatency(c);
            var stageB = s.Buses.Where(b => b.Kind is StripKind.Fx or StripKind.Group).ToList();
            var bLat = stageB.Select(RackLatency).DefaultIfEmpty(0).Max();
            foreach (var b in stageB) _strips[b.Id].Pdc = bLat - RackLatency(b);
            foreach (var c in s.Channels) _strips[c.Id].Late = bLat;
            foreach (var b in s.Buses.Where(b => b.Kind is not (StripKind.Fx or StripKind.Group))) _strips[b.Id].Pdc = 0;
            foreach (var x in new[] { s.Main, s.Mono, s.Stream }) _strips[x.Id].Pdc = 0;
            MixLatency = chLat + bLat + RackLatency(s.Main);

            // Feeds.
            var feedsSeen = new HashSet<string>();
            Feed MakeFeed(Strip from, Strip to, SendTap tap, float gl, float gr, bool late)
            {
                var f = new Feed(_strips[from.Id], _strips[to.Id], tap);
                if (_feeds.TryGetValue(f.Key, out var old)) f = old;
                else _feeds[f.Key] = f;
                f.GainL = gl;
                f.GainR = gr;
                f.UseLate = late;
                feedsSeen.Add(f.Key);
                return f;
            }

            var anySolo = s.AllStrips().Any(x => x.Solo);
            var channelFeeds = new List<Feed>();
            var fxFeeds = new List<Feed>();
            var groupFeeds = new List<Feed>();
            var mixFeeds = new List<Feed>();
            var mainFeeds = new List<Feed>();

            foreach (var src in s.Channels.Concat(s.Buses).Append(s.Main).Append(s.Mono))
            {
                var list = src.Kind switch
                {
                    StripKind.Channel => channelFeeds,
                    StripKind.Fx => fxFeeds,
                    StripKind.Group => groupFeeds,
                    StripKind.Mix => mixFeeds,
                    _ => mainFeeds,
                };
                var lateForC = src.Kind == StripKind.Channel; // channels skip the effect/group stage on some paths
                if (src.Kind is StripKind.Channel or StripKind.Fx or StripKind.Group && src.ToMain)
                {
                    var (pl, pr) = PanGains(src.Pan, src.Stereo);
                    list.Add(MakeFeed(src, s.Main, SendTap.Post, pl, pr, lateForC));
                }
                if (src.Kind is StripKind.Channel or StripKind.Fx)
                    foreach (var g in src.Groups)
                        if (s.Find(g) is { Kind: StripKind.Group } grp)
                        {
                            var (pl, pr) = PanGains(src.Pan, src.Stereo);
                            list.Add(MakeFeed(src, grp, SendTap.Post, pl, pr, false));
                        }
                foreach (var send in src.Sends)
                {
                    if (!send.On || send.Level <= Db.Floor || s.Find(send.Bus) is not { } bus || !Routing.CanSend(src, bus)) continue;
                    var g = Db.ToGain(send.Level);
                    var (pl, pr) = bus.Stereo ? PanGains(send.Pan ?? src.Pan, src.Stereo) : (1f, 1f);
                    var late = lateForC && bus.Kind is not StripKind.Fx;
                    list.Add(MakeFeed(src, bus, send.Tap, g * pl, g * pr, late));
                }
                // The stream's own balance: every strip has a stream level, taken before its fader.
                if (s.StreamSettings.Source == StreamSource.Own && src.Kind is StripKind.Channel or StripKind.Fx or StripKind.Group && src.StreamOn)
                {
                    var g = Db.ToGain(src.StreamLevel);
                    var (pl, pr) = PanGains(src.Pan, src.Stereo);
                    list.Add(MakeFeed(src, s.Stream, SendTap.Pre, g * pl, g * pr, lateForC));
                }
            }
            foreach (var dead in _feeds.Keys.Where(k => !feedsSeen.Contains(k)).ToList()) _feeds.Remove(dead);
            // Only strips with a send at an early tap point copy their sound there.
            var live = channelFeeds.Concat(fxFeeds).Concat(groupFeeds).Concat(mixFeeds).Concat(mainFeeds).ToList();
            foreach (var rt in _strips.Values)
            {
                rt.NeedTapIn = live.Any(f => f.From == rt && f.Tap == SendTap.Input);
                rt.NeedTapPreEq = live.Any(f => f.From == rt && f.Tap == SendTap.PreEq);
                rt.NeedTapPostEq = live.Any(f => f.From == rt && f.Tap == SendTap.PostEq);
            }
            topo.ChannelFeeds = [.. channelFeeds];
            topo.FxFeeds = [.. fxFeeds];
            topo.GroupFeeds = [.. groupFeeds];
            topo.MixFeeds = [.. mixFeeds];
            topo.MainFeeds = [.. mainFeeds];
            topo.StreamFromMain = s.StreamSettings.Source == StreamSource.Main;
            topo.StreamDelay = (int)(Math.Clamp(s.StreamSettings.DelayMs, 0, 1000) * 0.001 * _sr);
            if (_streamDelayL is null || _streamDelayL.MaxDelay < topo.StreamDelay)
            {
                _streamDelayL = new DelayLine(Math.Max(topo.StreamDelay, (int)_sr));
                _streamDelayR = new DelayLine(Math.Max(topo.StreamDelay, (int)_sr));
            }

            topo.Soloed = anySolo ? s.AllStrips().Where(x => x.Solo).Select(x => _strips[x.Id]).ToArray() : [];
            topo.SoloAfl = s.Solo.Mode == SoloMode.Afl;
            topo.SoloGain = Db.ToGain(s.Solo.Level);
            topo.Analyse = AnalyseId is { } a && _strips.TryGetValue(a, out var ar) ? ar : null;

            // Outputs.
            for (var o = 0; o < _outputs; o++)
            {
                var p = o < s.Outputs.Count ? s.Outputs[o] : new OutputPatch { Number = o + 1 };
                ConfigureOutput(p, _outs[o]);
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

    private void Configure(Strip s, StripRt rt)
    {
        rt.Trim = Db.ToGain(s.Trim) * (s.Invert ? -1 : 1);
        rt.InDelay = (int)(Math.Clamp(s.DelayMs, 0, 100) * 0.001 * _sr);
        var fader = s.Mute ? 0f : Db.ToGain(s.Fader);
        rt.Muted = s.Mute;
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
        }
        else if (src.Length > 0 && src != "off")
        {
            var parts = src.Split(':');
            var id = parts[0] == "mon" ? "mon" : parts[0];
            if (id == "mon")
            {
                kind = parts.Length > 1 && parts[1] == "r" ? OutSource.StripRight : OutSource.StripLeft;
                strip = _monitor;
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
        rt.DelaySamples = (int)(Math.Clamp(p.DelayMs, 0, 500) * 0.001 * _sr);
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

    // The solo / monitor bus (not part of the saved mix).
    private readonly StripRt _monitor;
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

        // 2. Clear every bus input.
        foreach (var b in t.Fx) Clear(b, frames);
        foreach (var b in t.Groups) Clear(b, frames);
        foreach (var b in t.Mixes) Clear(b, frames);
        foreach (var b in t.Matrices) Clear(b, frames);
        Clear(t.Main, frames);
        Clear(t.Stream, frames);

        // 3. Channel feeds.
        Mix(t.ChannelFeeds, frames);

        // 4. Effects, then groups.
        foreach (var b in t.Fx) RunBus(b, frames);
        Mix(t.FxFeeds, frames);
        foreach (var b in t.Groups) RunBus(b, frames);
        Mix(t.GroupFeeds, frames);

        // 5. Mixes (wedges, in-ears, side fills).
        foreach (var b in t.Mixes) RunBus(b, frames);
        Mix(t.MixFeeds, frames);

        // 6. Main, then mono / sub from the main mix.
        RunBus(t.Main, frames);
        var mono = t.Mono;
        for (var n = 0; n < frames; n++) mono.InL[n] = (t.Main.PostL[n] + t.Main.PostR[n]) * 0.5f;
        RunBus(mono, frames);

        // 7. Matrices, from the main, mono, groups and mixes.
        Mix(t.MainFeeds, frames);
        foreach (var b in t.Matrices) RunBus(b, frames);

        // 8. The stream mix.
        var st = t.Stream;
        if (t.StreamFromMain)
        {
            Array.Copy(t.Main.PreL, st.InL, frames);
            Array.Copy(t.Main.PreR, st.InR, frames);
        }
        RunBus(st, frames);
        if (t.StreamDelay > 0 && _streamDelayL is not null && _streamDelayR is not null)
        {
            _streamDelayL.Process(st.PostL, frames, t.StreamDelay);
            _streamDelayR.Process(st.PostR, frames, t.StreamDelay);
        }
        Loudness.Push(st.PostL, st.PostR, frames);
        StreamTap.Write(st.PostL, st.PostR, frames);

        // 9. Solo / monitor bus: the soloed strips, or the main mix when nothing is soloed.
        var mon = _monitor;
        if (t.Soloed.Length == 0)
        {
            Array.Copy(t.Main.PostL, mon.PostL, frames);
            Array.Copy(t.Main.PostR, mon.PostR, frames);
        }
        else
        {
            Array.Clear(mon.PostL, 0, frames);
            Array.Clear(mon.PostR, 0, frames);
            foreach (var s in t.Soloed)
            {
                var useAfl = t.SoloAfl;
                var l = useAfl ? s.PostL : s.PreL;
                var r = s.Stereo ? useAfl ? s.PostR : s.PreR : l;
                for (var n = 0; n < frames; n++)
                {
                    mon.PostL[n] += l[n];
                    mon.PostR[n] += r[n];
                }
            }
        }
        if (t.SoloGain != 1f)
            for (var n = 0; n < frames; n++)
            {
                mon.PostL[n] *= t.SoloGain;
                mon.PostR[n] *= t.SoloGain;
            }

        // 10. Outputs.
        var phaseStep = 2 * Math.PI * 1000.0 / _sr;
        var toneUsed = false;
        for (var o = 0; o < output.Length && o < _outs.Length; o++)
        {
            var rt = _outs[o];
            var dst = output[o];
            switch (rt.Kind)
            {
                case OutSource.Tone:
                    for (var n = 0; n < frames; n++) dst[n] = ToneLevel * (float)Math.Sin(_tonePhase + n * phaseStep);
                    toneUsed = true;
                    break;
                case OutSource.Input when rt.Input >= 0 && rt.Input < input.Length:
                    Array.Copy(input[rt.Input], dst, frames);
                    break;
                case OutSource.ChannelDirect when rt.Strip is { } cs:
                    Array.Copy(cs.PostL, dst, frames);
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
        if (toneUsed) _tonePhase = (_tonePhase + frames * phaseStep) % (2 * Math.PI);

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

    private static void Mix(Feed[] feeds, int frames)
    {
        foreach (var f in feeds)
        {
            var s = f.From;
            float[] l, r;
            if (f.Tap is SendTap.Input or SendTap.PreEq or SendTap.PostEq)
            {
                // Early taps come straight from the strip (they do not wait for plugin delay compensation).
                (l, r) = f.Tap switch
                {
                    SendTap.Input => (s.TapInL, s.TapInR),
                    SendTap.PreEq => (s.TapPreEqL, s.TapPreEqR),
                    _ => (s.TapPostEqL, s.TapPostEqR),
                };
                if (!s.Stereo) r = l;
            }
            else if (f.UseLate && s.LatePost is not null)
            {
                l = f.Pre ? s.LatePreL : s.LatePostL;
                r = f.Pre ? s.LatePreL : s.Stereo ? s.LatePostR : s.LatePostL;
            }
            else
            {
                l = f.Pre ? s.PreL : s.PostL;
                r = s.Stereo ? f.Pre ? s.PreR : s.PostR : l;
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
        if (s.Pdc > 0)
        {
            s.PdcL ??= new DelayLine(16384);
            s.PdcL.Process(s.L, frames, s.Pdc);
            if (r is not null)
            {
                s.PdcR ??= new DelayLine(16384);
                s.PdcR.Process(r, frames, s.Pdc);
            }
        }
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

        // Channels that also feed later stages directly get a delayed copy, to stay in line with the groups and effects.
        if (isChannel && s.Late > 0)
        {
            s.LatePre ??= new DelayLine(16384);
            s.LatePost ??= new DelayLine(16384);
            Array.Copy(s.PreL, s.LatePreL, frames);
            s.LatePre.Process(s.LatePreL, frames, s.Late);
            Array.Copy(s.PostL, s.LatePostL, frames);
            s.LatePost.Process(s.LatePostL, frames, s.Late);
        }
        else if (isChannel && s.LatePost is not null)
        {
            Array.Copy(s.PreL, s.LatePreL, frames);
            Array.Copy(s.PostL, s.LatePostL, frames);
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

public readonly record struct StripMeters(float In, float L, float R, float Gate, float Comp, float Rack, bool GateClosed, float DynEq = 0);
