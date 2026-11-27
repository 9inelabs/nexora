using Nexora.Audio.Mix;

namespace Nexora.Audio.Dsp;

/// <summary>A processor that can listen to another channel (a ducker under the pastor's mic, for example).</summary>
public interface IKeyed
{
    /// <summary>The other channel's sound, set by the mixer before each buffer (null listens to itself).</summary>
    float[]? KeyInput { get; set; }
}

/// <summary>
/// The colour strip: a whole extra channel strip in one insert. Low and high cut, a tube, tape or iron (transformer)
/// stage, a four-band EQ, a compressor in any of NEX-Audio's characters, and a gate or ducker that can listen to
/// another channel. NEX-Audio's own design, built from the same parts as the main strip.
/// </summary>
public sealed class ColourStrip : Processor, IKeyed
{
    private readonly StripFilters _filters;
    private readonly Saturator _sat;
    private readonly Compressor _comp;
    private readonly Gate _gate;
    private Biquad _tape = Biquad.Identity;
    private BiquadState _tapeL, _tapeR;
    private SmoothGain _outL, _outR;
    private float _out = 1;

    public ColourStrip(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _filters = new StripFilters(sampleRate, maxFrames);
        _sat = new Saturator(sampleRate, maxFrames);
        _comp = new Compressor(sampleRate, maxFrames);
        _gate = new Gate(sampleRate, maxFrames);
        _outL.Current = _outR.Current = 1;
    }

    public float[]? KeyInput { get; set; }

    public static readonly string[] Characters = ["Tube", "Tape", "Iron", "Clean"];
    public static readonly string[] CompStyles = ["Off", "Vintage", "Opto", "Fet", "Clean", "Glue", "Limit"];
    public static readonly string[] DynModes = ["Off", "Gate", "Ducker"];

    /// <summary>The EQ part of a colour strip (for drawing it, and for the strip itself).</summary>
    public static FilterSettings Filters(RackItem r)
    {
        var lowcut = r.P("lowcut", 0);
        var highcut = r.P("highcut", 20000);
        var eq = new EqSettings
        {
            On = true,
            Bands =
            [
                new EqBand { Shape = FilterShape.LowShelf, Freq = r.P("lf", 100), Gain = r.P("lg", 0), Q = 0.7 },
                new EqBand { Shape = FilterShape.Peak, Freq = r.P("lmf", 400), Gain = r.P("lmg", 0), Q = 1.0 },
                new EqBand { Shape = FilterShape.Peak, Freq = r.P("hmf", 2500), Gain = r.P("hmg", 0), Q = 1.0 },
                new EqBand { Shape = FilterShape.HighShelf, Freq = r.P("hf", 8000), Gain = r.P("hg", 0), Q = 0.7 },
            ],
        };
        return new FilterSettings(
            new PassFilter { On = lowcut >= 20, Freq = Math.Max(20, lowcut), Slope = 12 },
            new PassFilter { On = highcut < 19500, Freq = highcut, Slope = 12 }, eq);
    }

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        var character = r.T("character", "Tube");
        var drive = Math.Clamp(r.P("drive", 6), 0, 24);
        _sat.Configure(new DriveSettings
        {
            On = character != "Clean" && drive > 0.05,
            Style = character == "Iron" ? DriveStyle.Edge : DriveStyle.Warm,
            Drive = character == "Tape" ? drive * 0.75 : drive,
            Mix = 100,
        });
        // Tape rounds the very top a little.
        _tape = character == "Tape" ? Biquad.Make(FilterShape.HighShelf, 12000, 0.7, -2.5, SampleRate) : Biquad.Identity;
        _filters.Configure(Filters(r));
        var style = r.T("compstyle", "Off");
        _comp.Configure(new CompSettings
        {
            On = style != "Off",
            Style = Enum.TryParse<CompStyle>(style, true, out var cs) ? cs : CompStyle.Clean,
            Threshold = r.P("cthr", -18),
            Ratio = r.P("cratio", 3),
            Attack = r.P("cattack", 10),
            Release = r.P("crelease", 120),
            Knee = 6,
            Makeup = r.P("cmakeup", 0),
        });
        var mode = r.T("dynamics", "Off");
        _gate.Configure(new GateSettings
        {
            On = mode != "Off",
            Mode = mode == "Ducker" ? GateMode.Ducker : GateMode.Gate,
            Threshold = r.P("dthr", -45),
            Range = r.P("drange", 20),
            Attack = 0.5,
            Hold = 40,
            Release = r.P("drelease", 150),
        });
        _out = Db.ToGain(Math.Clamp(r.P("output", 0), -12, 12));
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        _filters.Run(left, right, frames);
        _sat.Run(left, right, frames);
        if (!_tape.IsIdentity)
        {
            _tapeL.Process(_tape, left.AsSpan(0, frames));
            if (right is not null) _tapeR.Process(_tape, right.AsSpan(0, frames));
        }
        _comp.Run(left, right, frames);
        _gate.KeyInput = KeyInput;
        _gate.Run(left, right, frames);
        _outL.Apply(left, frames, _out);
        if (right is not null) _outR.Apply(right, frames, _out);
        Reduction = Math.Max(_comp.Reduction, _gate.Reduction);
    }

    public override void Reset()
    {
        _filters.Reset();
        _sat.Reset();
        _comp.Reset();
        _gate.Reset();
        _tapeL.Reset();
        _tapeR.Reset();
    }
}

/// <summary>A six-band dynamic EQ for the effects rack: each bell moves only when its own range gets loud.</summary>
public sealed class DynEq6 : Processor
{
    public const int Bands = 6;
    public static readonly double[] Freqs = [80, 250, 700, 2000, 5000, 10000];
    private readonly DynamicEq _eq;

    public DynEq6(double sampleRate, int maxFrames) : base(sampleRate, maxFrames) => _eq = new DynamicEq(sampleRate, maxFrames);

    public static DynEqSettings Settings(RackItem r) => new()
    {
        On = true,
        Bands = Enumerable.Range(1, Bands).Select(i =>
        {
            var range = Math.Clamp(r.P($"g{i}", 0), -18, 12);
            return new DynEqBand
            {
                On = Math.Abs(range) > 0.05,
                Freq = r.P($"f{i}", Freqs[i - 1]),
                Q = r.P("q", 1.4),
                Threshold = r.P($"t{i}", -30),
                Range = range,
                Ratio = r.P("ratio", 3),
                Attack = r.P("attack", 5),
                Release = r.P("release", 120),
            };
        }).ToList(),
    };

    public float[] Now => _eq.Now;

    protected override void Apply(object settings)
    {
        if (settings is RackItem r) _eq.Configure(Settings(r));
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        _eq.Run(left, right, frames);
        Reduction = _eq.Reduction;
    }

    public override void Reset() => _eq.Reset();
}

/// <summary>
/// A six-band multiband compressor or expander for the effects rack. Linkwitz-Riley crossovers split the sound, and
/// every lower band goes through the same phase turn as the splits above it, so with no reduction the bands add back
/// up to the original level at every frequency.
/// </summary>
public sealed class Multiband6 : Processor
{
    public const int Bands = 6;
    public static readonly double[] Splits = [120, 300, 900, 2500, 7000];
    private readonly Biquad[] _lp = new Biquad[Bands - 1], _hp = new Biquad[Bands - 1], _ap = new Biquad[Bands - 1];
    // Per side: 4 sections per crossover (two low-pass, two high-pass), then the phase match for the lower bands.
    private readonly BiquadState[][] _split = [new BiquadState[(Bands - 1) * 4], new BiquadState[(Bands - 1) * 4]];
    private readonly BiquadState[][] _match = [new BiquadState[Bands * Bands], new BiquadState[Bands * Bands]];
    private readonly CompCore[] _cores;
    private readonly float[] _thr = new float[Bands], _ratio = new float[Bands], _makeup = new float[Bands];
    private readonly float[] _env = new float[Bands], _gr = new float[Bands], _bandGr = new float[Bands];
    private readonly float[] _l = new float[Bands], _r = new float[Bands];
    private float _att, _rel, _peakRel;
    private bool _expand;

    public Multiband6(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _cores = Enumerable.Range(0, Bands).Select(_ => new CompCore(sampleRate)).ToArray();
        _peakRel = (float)Dsp.Coef(10, sampleRate);
    }

    public IReadOnlyList<float> BandReduction => _bandGr;

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        _expand = r.T("mode", "Compress") == "Expand";
        var prev = 20.0;
        for (var i = 0; i < Bands - 1; i++)
        {
            var f = Math.Clamp(r.P($"x{i + 1}", Splits[i]), prev * 1.25, 16000);
            prev = f;
            _lp[i] = Biquad.Make(FilterShape.LowPass, f, 0.70710678, 0, SampleRate);
            _hp[i] = Biquad.Make(FilterShape.HighPass, f, 0.70710678, 0, SampleRate);
            _ap[i] = Biquad.Make(FilterShape.AllPass, f, 0.70710678, 0, SampleRate);
        }
        var attack = r.P("attack", 10);
        var release = r.P("release", 150);
        _att = (float)Dsp.Coef(Math.Max(0.1, attack), SampleRate);
        _rel = (float)Dsp.Coef(Math.Max(5, release), SampleRate);
        for (var b = 0; b < Bands; b++)
        {
            _thr[b] = (float)r.P($"t{b + 1}", _expand ? -50 : -20);
            _ratio[b] = (float)Math.Max(1, r.P($"r{b + 1}", 2));
            _makeup[b] = Db.ToGain(Math.Clamp(r.P($"g{b + 1}", 0), -12, 12));
            _cores[b].Set(CompStyle.Clean, _thr[b], _ratio[b], attack, release, 6);
        }
    }

    private void Split(int side, float x, float[] bands)
    {
        var st = _split[side];
        var rest = x;
        for (var i = 0; i < Bands - 1; i++)
        {
            bands[i] = st[i * 4 + 1].Tick(_lp[i], st[i * 4].Tick(_lp[i], rest));
            rest = st[i * 4 + 3].Tick(_hp[i], st[i * 4 + 2].Tick(_hp[i], rest));
        }
        bands[Bands - 1] = rest;
        // Band b has been through crossovers 0..b; it still needs the phase turn of every crossover above it.
        var m = _match[side];
        for (var b = 0; b < Bands - 2; b++)
            for (var k = b + 1; k < Bands - 1; k++)
                bands[b] = m[b * Bands + k].Tick(_ap[k], bands[b]);
    }

    private float Gain(int b, float key)
    {
        float gr;
        if (!_expand) gr = _cores[b].Step(key);
        else
        {
            // Downward expander: below the threshold, the quieter it gets the more it is turned down (40 dB at most).
            _env[b] = key > _env[b] ? key : _peakRel * _env[b] + (1 - _peakRel) * key;
            var level = _env[b] <= 1e-6f ? -120f : 20f * MathF.Log10(_env[b]);
            var target = level < _thr[b] ? Math.Min((_thr[b] - level) * (_ratio[b] - 1), 40f) : 0f;
            var c = target > _gr[b] ? _att : _rel;
            _gr[b] = c * _gr[b] + (1 - c) * target;
            gr = _gr[b];
        }
        if (gr > _bandGr[b]) _bandGr[b] = gr;
        return (gr <= 1e-6f ? 1f : MathF.Pow(10f, -gr / 20f)) * _makeup[b];
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        Array.Clear(_bandGr);
        for (var n = 0; n < frames; n++)
        {
            Split(0, left[n], _l);
            if (right is not null) Split(1, right[n], _r);
            float yl = 0, yr = 0;
            for (var b = 0; b < Bands; b++)
            {
                var key = right is null ? Math.Abs(_l[b]) : Math.Max(Math.Abs(_l[b]), Math.Abs(_r[b]));
                var g = Gain(b, key);
                yl += _l[b] * g;
                if (right is not null) yr += _r[b] * g;
            }
            left[n] = yl;
            if (right is not null) right[n] = yr;
        }
        Reduction = _bandGr.Max();
    }

    public override void Reset()
    {
        foreach (var side in _split) Array.Clear(side);
        foreach (var side in _match) Array.Clear(side);
        foreach (var c in _cores) c.Reset();
        Array.Clear(_env);
        Array.Clear(_gr);
    }
}

/// <summary>A processing node: a four-band EQ with low and high cut, to drop anywhere in a path.</summary>
public sealed class EqNode : Processor
{
    private readonly StripFilters _f;

    public EqNode(double sampleRate, int maxFrames) : base(sampleRate, maxFrames) => _f = new StripFilters(sampleRate, maxFrames);

    public static FilterSettings Filters(RackItem r)
    {
        var lowcut = r.P("lowcut", 0);
        var highcut = r.P("highcut", 20000);
        return new FilterSettings(
            new PassFilter { On = lowcut >= 20, Freq = Math.Max(20, lowcut), Slope = 24 },
            new PassFilter { On = highcut < 19500, Freq = highcut, Slope = 24 },
            new EqSettings
            {
                On = true,
                Bands =
                [
                    new EqBand { Shape = FilterShape.LowShelf, Freq = r.P("lf", 100), Gain = r.P("lg", 0), Q = 0.7 },
                    new EqBand { Shape = FilterShape.Peak, Freq = r.P("m1f", 500), Gain = r.P("m1g", 0), Q = r.P("m1q", 1.2) },
                    new EqBand { Shape = FilterShape.Peak, Freq = r.P("m2f", 3000), Gain = r.P("m2g", 0), Q = r.P("m2q", 1.2) },
                    new EqBand { Shape = FilterShape.HighShelf, Freq = r.P("hf", 10000), Gain = r.P("hg", 0), Q = 0.7 },
                ],
            });
    }

    protected override void Apply(object settings)
    {
        if (settings is RackItem r) _f.Configure(Filters(r));
    }

    protected override void Process(float[] left, float[]? right, int frames) => _f.Run(left, right, frames);

    public override void Reset() => _f.Reset();
}

/// <summary>A processing node: a gate or a ducker that can listen to any channel.</summary>
public sealed class GateNode : Processor, IKeyed
{
    private readonly Gate _g;

    public GateNode(double sampleRate, int maxFrames) : base(sampleRate, maxFrames) => _g = new Gate(sampleRate, maxFrames);

    public float[]? KeyInput { get; set; }

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        _g.Configure(new GateSettings
        {
            On = true,
            Mode = r.T("mode", "Gate") == "Ducker" ? GateMode.Ducker : GateMode.Gate,
            Threshold = r.P("threshold", -45),
            Range = r.P("range", 30),
            Attack = r.P("attack", 0.5),
            Hold = r.P("hold", 40),
            Release = r.P("release", 150),
            KeyFilter = r.P("keyfilter", 0),
        });
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        _g.KeyInput = KeyInput;
        _g.Run(left, right, frames);
        Reduction = _g.Reduction;
    }

    public override void Reset() => _g.Reset();
}

/// <summary>
/// A pitch shifter: moves the whole sound up or down by a set amount (a harmony, an octave, a slight detune for width).
/// Two read heads slide through a short delay and cross-fade, the standard rotating-head method.
/// </summary>
public sealed class PitchShifter : Processor
{
    private sealed class Head
    {
        public readonly DelayLine Line;
        public double Phase;
        public Head(int size) => Line = new DelayLine(size);
    }

    private readonly Head _l, _r;
    private double _ratio = 1, _window;
    private float _wet = 1, _dry;
    private readonly float[] _dryL, _dryR;

    public PitchShifter(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        var size = (int)(0.1 * sampleRate);
        _l = new Head(size);
        _r = new Head(size) { Phase = 0.25 };
        _window = 0.03 * sampleRate;
        _dryL = new float[maxFrames];
        _dryR = new float[maxFrames];
    }

    public double Ratio => _ratio;

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        var semis = Math.Clamp(r.P("semis", 0), -12, 12) + Math.Clamp(r.P("cents", 0), -50, 50) / 100;
        _ratio = Math.Pow(2, semis / 12);
        _window = Math.Clamp(r.P("window", 30), 10, 80) * 0.001 * SampleRate;
        var mix = (float)Math.Clamp(r.P("mix", 100) / 100, 0, 1);
        _wet = mix;
        _dry = 1 - mix;
    }

    private float Tick(Head h, float x)
    {
        h.Line.Write(x);
        h.Phase += (1 - _ratio) / _window;
        if (h.Phase >= 1) h.Phase -= 1;
        else if (h.Phase < 0) h.Phase += 1;
        var p2 = h.Phase + 0.5;
        if (p2 >= 1) p2 -= 1;
        var wa = Math.Sin(Math.PI * h.Phase);
        wa *= wa;
        return h.Line.ReadCubic(h.Phase * _window + 2) * (float)wa + h.Line.ReadCubic(p2 * _window + 2) * (float)(1 - wa);
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        Array.Copy(left, _dryL, frames);
        if (right is not null) Array.Copy(right, _dryR, frames);
        for (var n = 0; n < frames; n++)
        {
            left[n] = Tick(_l, left[n]) * _wet + _dryL[n] * _dry;
            if (right is not null) right[n] = Tick(_r, right[n]) * _wet + _dryR[n] * _dry;
        }
    }

    public override void Reset()
    {
        _l.Line.Clear();
        _r.Line.Clear();
    }
}

/// <summary>
/// The enhancer: brings speech forward. A gentle high shelf lifts the top of the sound above the tune frequency, and
/// a little harmonic edge made from that top band is blended in, so words stay clear without turning the whole voice up.
/// </summary>
public sealed class Enhancer : Processor
{
    private Biquad _hp = Biquad.Identity, _shelf = Biquad.Identity, _body = Biquad.Identity;
    private BiquadState _lh, _ls, _lb, _rh, _rs, _rb;
    private float _harm, _drive = 2;

    public Enhancer(double sampleRate, int maxFrames) : base(sampleRate, maxFrames) { }

    /// <summary>The lift of the shelf, in dB, for this amount (0 to 100).</summary>
    public static double LiftDb(double amount) => Math.Clamp(amount, 0, 100) / 100 * 8;

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        var f = Math.Clamp(r.P("freq", 3000), 800, 10000);
        var lift = LiftDb(r.P("amount", 30));
        _shelf = lift > 0.05 ? Biquad.Make(FilterShape.HighShelf, f, 0.7, lift, SampleRate) : Biquad.Identity;
        _hp = Biquad.Make(FilterShape.HighPass, f, 0.707, 0, SampleRate);
        var harm = Math.Clamp(r.P("harmonics", 50), 0, 100) / 100;
        _drive = 1f + (float)(harm * 6);
        _harm = (float)(harm * Math.Clamp(r.P("amount", 30), 0, 100) / 100 * 1.5);
        var body = Math.Clamp(r.P("body", 0), 0, 6);
        _body = body > 0.05 ? Biquad.Make(FilterShape.LowShelf, 180, 0.7, body, SampleRate) : Biquad.Identity;
    }

    private float One(ref BiquadState h, ref BiquadState sh, ref BiquadState b, float x)
    {
        var top = h.Tick(_hp, x);
        // What the soft curve adds to the top band: its harmonics, with the original taken away.
        var edge = MathF.Tanh(top * _drive) / _drive - top;
        var y = (_shelf.IsIdentity ? x : sh.Tick(_shelf, x)) - edge * _harm;
        return _body.IsIdentity ? y : b.Tick(_body, y);
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        for (var n = 0; n < frames; n++)
        {
            left[n] = One(ref _lh, ref _ls, ref _lb, left[n]);
            if (right is not null) right[n] = One(ref _rh, ref _rs, ref _rb, right[n]);
        }
    }

    public override void Reset()
    {
        _lh.Reset(); _ls.Reset(); _lb.Reset();
        _rh.Reset(); _rs.Reset(); _rb.Reset();
    }
}
