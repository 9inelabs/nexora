using Nexora.Audio.Mix;

namespace Nexora.Audio.Dsp;

/// <summary>
/// The heart of every NEX-Audio compressor: a level detector and a soft-knee gain computer working in
/// decibels, with separate attack and release smoothing (the textbook feed-forward design).
/// </summary>
internal sealed class CompCore
{
    private readonly double _sr;
    private float _thr = -18, _ratio = 3, _knee = 6;
    private float _attack, _release, _rmsCoef;
    private float _env;      // smoothed gain reduction, dB
    private float _ms;       // mean square for the RMS detector
    private float _slow;     // long-term reduction, for the opto release
    private CompStyle _style;
    private bool _rms;
    private double _releaseMs = 120;

    public CompCore(double sampleRate) => _sr = sampleRate;

    public float Reduction => _env;

    public void Set(CompStyle style, double threshold, double ratio, double attackMs, double releaseMs, double knee, CompDetect detect = CompDetect.Auto)
    {
        _style = style;
        _thr = (float)threshold;
        _ratio = (float)Math.Max(1, ratio);
        _knee = (float)Math.Max(0, knee);
        if (style == CompStyle.Limit)
        {
            // A limiter: at least 20:1, a firm knee and never slower than 0.8 ms to grab a peak.
            _ratio = Math.Max(20f, _ratio);
            _knee = Math.Min(_knee, 2f);
            attackMs = Math.Min(attackMs, 0.8);
        }
        _rms = detect switch
        {
            CompDetect.Peak => false,
            CompDetect.Rms => true,
            _ => style is CompStyle.Opto or CompStyle.Glue or CompStyle.Vintage,
        };
        var minAttack = style is CompStyle.Fet or CompStyle.Limit ? 0.02 : 0.1;
        _attack = (float)Dsp.Coef(Math.Max(minAttack, attackMs), _sr);
        _releaseMs = Math.Max(5, releaseMs);
        _release = (float)Dsp.Coef(_releaseMs, _sr);
        _rmsCoef = (float)Dsp.Coef(style == CompStyle.Opto ? 10 : 6, _sr);
    }

    /// <summary>The static curve: how many dB to turn down a signal at this level.</summary>
    public static float Curve(float x, float thr, float ratio, float knee)
    {
        var over = x - thr;
        if (knee > 0 && 2 * Math.Abs(over) <= knee)
        {
            var t = over + knee / 2;
            return (1 - 1 / ratio) * t * t / (2 * knee);
        }
        return over > 0 ? over * (1 - 1 / ratio) : 0;
    }

    /// <summary>Takes one detector sample (the louder of left and right) and returns the reduction in dB.</summary>
    public float Step(float key)
    {
        float level;
        if (_rms)
        {
            _ms = _rmsCoef * _ms + (1 - _rmsCoef) * key * key;
            level = _ms <= 1e-12f ? -120f : 10f * MathF.Log10(_ms) + 3.01f; // +3 dB so a sine reads its peak
        }
        else
        {
            var a = key < 0 ? -key : key;
            level = a <= 1e-6f ? -120f : 20f * MathF.Log10(a);
        }
        var gr = level < _thr - _knee ? 0f : Curve(level, _thr, _ratio, _knee);
        if (gr > _env) _env = _attack * _env + (1 - _attack) * gr;
        else
        {
            var rel = _release;
            if (_style == CompStyle.Opto)
            {
                // Lets go slower after long, deep reduction and quicker after a short peak.
                _slow = 0.9999f * _slow + 0.0001f * _env;
                var hold = Math.Clamp(_slow / 6f, 0f, 3f);
                rel = MathF.Pow(_release, 1f / (1f + hold));
            }
            else if (_style == CompStyle.Vintage)
            {
                // Program dependent: the deeper it is pressing, the quicker it lets go (up to three times quicker).
                rel = MathF.Pow(_release, 1f + Math.Clamp(_env / 8f, 0f, 2f));
            }
            _env = rel * _env + (1 - rel) * gr;
        }
        if (_env < 1e-6f) _env = 0;
        return _env;
    }

    public void Reset()
    {
        _env = 0;
        _ms = 0;
        _slow = 0;
    }
}

/// <summary>The channel and bus compressor, in four styles (see <see cref="CompStyle"/>).</summary>
public sealed class Compressor : Processor
{
    private readonly CompCore _core;
    private BiquadState _keyL, _keyR;
    private Biquad _key = Biquad.Identity;
    private float _makeup = 1, _mix = 1;
    private CompStyle _style;
    private bool _on;
    private readonly float[] _dryL, _dryR;

    public Compressor(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _core = new CompCore(sampleRate);
        _dryL = new float[maxFrames];
        _dryR = new float[maxFrames];
    }

    public bool On => _on;

    /// <summary>
    /// Another channel's sound for the compressor to listen to, set by the mixer before each buffer (null listens to itself).
    /// </summary>
    public float[]? KeyInput { get; set; }

    public static double AutoMakeup(CompSettings c) => Math.Max(0, -(c.Threshold * (1 - 1 / Math.Max(1, c.Ratio))) * 0.5);

    protected override void Apply(object settings)
    {
        var c = settings switch
        {
            CompSettings cs => cs,
            RackItem r => FromRack(r),
            _ => null,
        };
        if (c is null) return;
        _on = c.On;
        _style = c.Style;
        _core.Set(c.Style, c.Threshold, c.Ratio, c.Attack, c.Release, c.Knee, c.Detect);
        var makeup = c.AutoMakeup ? AutoMakeup(c) : c.Makeup;
        _makeup = Db.ToGain(makeup);
        _mix = (float)Math.Clamp(c.Mix / 100, 0, 1);
        _key = c.KeyFilter > 0 ? Biquad.Make(FilterShape.HighPass, c.KeyFilter, 0.707, 0, SampleRate) : Biquad.Identity;
        if (!_on) _core.Reset();
    }

    public static CompSettings FromRack(RackItem r) => new()
    {
        On = !r.Bypass,
        Style = Enum.TryParse<CompStyle>(r.T("style", "Clean"), true, out var st) ? st : CompStyle.Clean,
        Detect = Enum.TryParse<CompDetect>(r.T("detect", "Auto"), true, out var dt) ? dt : CompDetect.Auto,
        Threshold = r.P("threshold", -18),
        Ratio = r.P("ratio", 3),
        Attack = r.P("attack", 10),
        Release = r.P("release", 120),
        Knee = r.P("knee", 6),
        Makeup = r.P("makeup", 0),
        Mix = r.P("mix", 100),
        KeyFilter = r.P("key", 0),
    };

    protected override void Process(float[] left, float[]? right, int frames)
    {
        if (!_on)
        {
            Reduction = 0;
            return;
        }
        var parallel = _mix < 0.999f;
        if (parallel)
        {
            Array.Copy(left, _dryL, frames);
            if (right is not null) Array.Copy(right, _dryR, frames);
        }
        var keyed = !_key.IsIdentity;
        var maxGr = 0f;
        var fet = _style is CompStyle.Fet or CompStyle.Limit;
        var vintage = _style == CompStyle.Vintage;
        for (var n = 0; n < frames; n++)
        {
            var l = left[n];
            var r = right is null ? l : right[n];
            float k;
            if (KeyInput is { } ext)
            {
                var x = ext[n];
                k = Math.Abs(keyed ? _keyL.Tick(_key, x) : x);
            }
            else if (keyed)
            {
                var kl = _keyL.Tick(_key, l);
                var kr = right is null ? kl : _keyR.Tick(_key, r);
                k = Math.Max(Math.Abs(kl), Math.Abs(kr));
            }
            else k = Math.Max(Math.Abs(l), Math.Abs(r));
            var gr = _core.Step(k);
            if (gr > maxGr) maxGr = gr;
            var g = (gr <= 0 ? 1f : MathF.Pow(10f, -gr / 20f)) * _makeup;
            if (fet)
            {
                // A touch of harmonic colour that grows with the reduction.
                var drive = 1f + gr * 0.04f;
                left[n] = MathF.Tanh(l * g * drive) / drive;
                if (right is not null) right[n] = MathF.Tanh(r * g * drive) / drive;
            }
            else if (vintage)
            {
                // A little warmth from the gain stage: rounds only the biggest peaks.
                left[n] = Warm(l * g);
                if (right is not null) right[n] = Warm(r * g);
            }
            else
            {
                left[n] = l * g;
                if (right is not null) right[n] = r * g;
            }
        }
        if (parallel)
        {
            var dry = 1 - _mix;
            for (var n = 0; n < frames; n++) left[n] = left[n] * _mix + _dryL[n] * dry;
            if (right is not null)
                for (var n = 0; n < frames; n++) right[n] = right[n] * _mix + _dryR[n] * dry;
        }
        Reduction = maxGr;
    }

    /// <summary>Linear up to half scale, then a gentle round-off (no change at normal levels).</summary>
    private static float Warm(float x)
    {
        var a = MathF.Abs(x);
        if (a <= 0.5f) return x;
        var over = a - 0.5f;
        var y = 0.5f + over / (1f + over * 0.6f);
        return x < 0 ? -y : y;
    }

    public override void Reset()
    {
        _core.Reset();
        _keyL.Reset();
        _keyR.Reset();
    }
}

/// <summary>A noise gate: closes (turns the sound down by its range) when the signal falls below the threshold.</summary>
public sealed class Gate : Processor
{
    private float _open = 0.003f, _close = 0.002f, _floor = 0.01f;
    private float _att, _rel;
    private int _hold, _holdLeft;
    private float _gain = 1, _env;
    private bool _isOpen = true, _on, _duck;
    private Biquad _key = Biquad.Identity;
    private BiquadState _ks;
    private float _envRel;

    public Gate(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _envRel = (float)Dsp.Coef(5, sampleRate);
    }

    public bool On => _on;
    /// <summary>The sound is passing at full level (a gate that is open, a ducker that is not ducking).</summary>
    public bool IsOpen => _duck ? !_isOpen : _isOpen;
    public bool IsDucker => _duck;

    /// <summary>Another channel's sound to listen to, set by the mixer before each buffer (null listens to itself).</summary>
    public float[]? KeyInput { get; set; }

    protected override void Apply(object settings)
    {
        if (settings is not GateSettings g) return;
        _on = g.On;
        _duck = g.Mode == GateMode.Ducker;
        _open = Db.ToGain(g.Threshold);
        _close = Db.ToGain(g.Threshold - 3); // 3 dB of hysteresis so it does not chatter
        _floor = Db.ToGain(-Math.Abs(g.Range));
        _att = (float)Dsp.Coef(Math.Max(0.02, g.Attack), SampleRate);
        _rel = (float)Dsp.Coef(Math.Max(1, g.Release), SampleRate);
        _hold = (int)(Math.Max(0, g.Hold) * 0.001 * SampleRate);
        _key = g.KeyFilter > 0 ? Biquad.Make(FilterShape.HighPass, g.KeyFilter, 0.707, 0, SampleRate) : Biquad.Identity;
        if (!_on)
        {
            _gain = 1;
            _isOpen = true;
        }
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        if (!_on)
        {
            Reduction = 0;
            return;
        }
        var keyed = !_key.IsIdentity;
        var minGain = 1f;
        for (var n = 0; n < frames; n++)
        {
            var l = left[n];
            var r = right is null ? l : right[n];
            float k;
            if (KeyInput is { } ext) k = Math.Abs(keyed ? _ks.Tick(_key, ext[n]) : ext[n]);
            else k = keyed ? Math.Abs(_ks.Tick(_key, Math.Abs(l) > Math.Abs(r) ? l : r)) : Math.Max(Math.Abs(l), Math.Abs(r));
            _env = k > _env ? k : _envRel * _env + (1 - _envRel) * k;
            if (_env >= _open)
            {
                _isOpen = true;
                _holdLeft = _hold;
            }
            else if (_env < _close)
            {
                if (_holdLeft > 0) _holdLeft--;
                else _isOpen = false;
            }
            // A gate opens while the key is loud; a ducker dips while the key is loud. Attack is always the move away from rest.
            var target = _duck ? _isOpen ? _floor : 1f : _isOpen ? 1f : _floor;
            var c = _duck ? target < _gain ? _att : _rel : target > _gain ? _att : _rel;
            _gain = c * _gain + (1 - c) * target;
            if (_gain < minGain) minGain = _gain;
            left[n] = l * _gain;
            if (right is not null) right[n] = r * _gain;
        }
        Reduction = -Dsp.Lin2Db(minGain);
    }

    public override void Reset()
    {
        _env = 0;
        _gain = 1;
        _ks.Reset();
    }
}

/// <summary>
/// A look-ahead brick-wall limiter: no peak gets past the ceiling. It looks a little ahead (1 ms by default)
/// so it can turn down smoothly before a peak arrives, which delays the sound by that much.
/// </summary>
public sealed class Limiter : Processor
{
    private readonly int _maxLook;
    private int _look;
    private float _ceiling = Db.ToGain(-1);
    private float _relCoef;
    private readonly DelayLine _dl, _dr;
    // Sliding minimum over the look-ahead window (a monotonic queue in fixed arrays).
    private readonly float[] _qv;
    private readonly long[] _qi;
    private readonly int _qmask;
    private int _qh, _qt;
    private long _idx;
    private readonly float[] _box;
    private double _boxSum;
    private int _boxPos;
    private float _rel = 1;
    private bool _on = true;

    public Limiter(double sampleRate, int maxFrames, double maxLookMs = 5) : base(sampleRate, maxFrames)
    {
        _maxLook = Math.Max(1, (int)(maxLookMs * 0.001 * sampleRate));
        _dl = new DelayLine(_maxLook + 1);
        _dr = new DelayLine(_maxLook + 1);
        var size = 1;
        while (size < _maxLook + 2) size <<= 1;
        _qv = new float[size];
        _qi = new long[size];
        _qmask = size - 1;
        _box = new float[_maxLook + 1];
        SetLook(1.0);
        _relCoef = (float)Dsp.Coef(80, sampleRate);
    }

    public override int Latency => _look;

    private void SetLook(double ms)
    {
        var look = Math.Clamp((int)Math.Round(ms * 0.001 * SampleRate), 1, _maxLook);
        if (look == _look) return;
        _look = look;
        Array.Fill(_box, 1f);
        _boxSum = _look;
        _boxPos = 0;
        _qh = _qt = 0;
    }

    public record Settings(bool On, double CeilingDb, double ReleaseMs, double LookMs);

    protected override void Apply(object settings)
    {
        var s = settings switch
        {
            Settings x => x,
            RackItem r => new Settings(!r.Bypass, r.P("ceiling", -1), r.P("release", 80), r.P("lookahead", 1)),
            _ => null,
        };
        if (s is null) return;
        _on = s.On;
        _ceiling = Db.ToGain(Math.Min(0, s.CeilingDb));
        _relCoef = (float)Dsp.Coef(Math.Max(1, s.ReleaseMs), SampleRate);
        SetLook(s.LookMs);
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        var minG = 1f;
        var c = _ceiling;
        for (var n = 0; n < frames; n++)
        {
            var l = left[n];
            var r = right is null ? 0f : right[n];
            var peak = Math.Max(Math.Abs(l), Math.Abs(r));
            var req = !_on || peak <= c ? 1f : c / peak;

            // Push into the sliding-minimum queue and drop what fell out of the window.
            while (_qt != _qh && _qv[(_qt - 1) & _qmask] >= req) _qt--;
            _qv[_qt & _qmask] = req;
            _qi[_qt & _qmask] = _idx;
            _qt++;
            while (_qi[_qh & _qmask] <= _idx - _look - 1) _qh++;
            var hold = _qv[_qh & _qmask];
            _idx++;

            // Instant down, smooth back up.
            _rel = hold < _rel ? hold : _relCoef * _rel + (1 - _relCoef) * hold;
            if (_rel > hold) _rel = hold;

            // A moving average over the look-ahead turns the steps into smooth ramps.
            _boxSum += _rel - _box[_boxPos];
            _box[_boxPos] = _rel;
            _boxPos++;
            if (_boxPos >= _look) _boxPos = 0;
            var g = (float)(_boxSum / _look);
            if (g > 1f) g = 1f;
            if (g < minG) minG = g;

            _dl.Write(l);
            var outL = _dl.Read(_look + 1) * g;
            left[n] = _on ? Math.Clamp(outL, -c, c) : outL;
            if (right is not null)
            {
                _dr.Write(r);
                var outR = _dr.Read(_look + 1) * g;
                right[n] = _on ? Math.Clamp(outR, -c, c) : outR;
            }
        }
        // Keep the running sum exact over long runs.
        if ((_idx & 0xFFFF) < frames)
        {
            double s = 0;
            for (var i = 0; i < _look; i++) s += _box[i];
            _boxSum = s;
        }
        Reduction = -Dsp.Lin2Db(minG);
    }

    public override void Reset()
    {
        _dl.Clear();
        _dr.Clear();
        Array.Fill(_box, 1f);
        _boxSum = _look;
        _qh = _qt = 0;
        _rel = 1;
    }
}

/// <summary>Turns down harsh "s" sounds: listens around the sibilance and turns down only the top of the sound.</summary>
public sealed class DeEsser : Processor
{
    private Biquad _split = Biquad.Identity, _band = Biquad.Identity;
    private BiquadState _sl, _sr, _bk;
    private float _thr = Db.ToGain(-30), _range = 10, _env, _att, _rel;
    private float _grEnv;

    public DeEsser(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _att = (float)Dsp.Coef(0.5, sampleRate);
        _rel = (float)Dsp.Coef(60, sampleRate);
    }

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        var f = Math.Clamp(r.P("freq", 6500), 2000, 14000);
        _split = Biquad.Make(FilterShape.LowPass, f * 0.8, 0.707, 0, SampleRate);
        _band = Biquad.Make(FilterShape.BandPass, f, 1.4, 0, SampleRate);
        _thr = Db.ToGain(r.P("threshold", -30));
        _range = (float)Math.Clamp(r.P("range", 10), 0, 24);
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        var thrDb = Dsp.Lin2Db(_thr);
        var maxGr = 0f;
        for (var n = 0; n < frames; n++)
        {
            var l = left[n];
            var r = right is null ? l : right[n];
            var k = Math.Abs(_bk.Tick(_band, Math.Abs(l) > Math.Abs(r) ? l : r));
            _env = k > _env ? _att * _env + (1 - _att) * k : _rel * _env + (1 - _rel) * k;
            var over = _env > _thr ? Dsp.Lin2Db(_env) - thrDb : 0f;
            var gr = Math.Min(_range, over * 0.75f);
            _grEnv = gr > _grEnv ? gr : _grEnv * 0.999f + gr * 0.001f;
            if (_grEnv > maxGr) maxGr = _grEnv;
            var g = MathF.Pow(10f, -_grEnv / 20f);
            var lowL = _sl.Tick(_split, l);
            left[n] = lowL + (l - lowL) * g;
            if (right is not null)
            {
                var lowR = _sr.Tick(_split, r);
                right[n] = lowR + (r - lowR) * g;
            }
        }
        Reduction = maxGr;
    }

    public override void Reset()
    {
        _sl.Reset();
        _sr.Reset();
        _bk.Reset();
        _env = _grEnv = 0;
    }
}

/// <summary>A three-band compressor: low, middle and top are each controlled on their own.</summary>
public sealed class MultibandCompressor : Processor
{
    private const int Bands = 3;
    private readonly CompCore[] _cores;
    private readonly float[] _makeup = [1, 1, 1];
    private Biquad _lp1a = Biquad.Identity, _lp1b = Biquad.Identity, _hp1a = Biquad.Identity, _hp1b = Biquad.Identity;
    private Biquad _lp2a = Biquad.Identity, _lp2b = Biquad.Identity, _hp2a = Biquad.Identity, _hp2b = Biquad.Identity;
    // Filter memory per side: [0] left, [1] right. 12 sections each.
    private readonly BiquadState[][] _st = [new BiquadState[12], new BiquadState[12]];
    private readonly float[] _bandGr = new float[Bands];

    public MultibandCompressor(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _cores = Enumerable.Range(0, Bands).Select(_ => new CompCore(sampleRate)).ToArray();
    }

    public IReadOnlyList<float> BandReduction => _bandGr;

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        var f1 = Math.Clamp(r.P("low", 200), 40, 1000);
        var f2 = Math.Clamp(r.P("high", 3000), f1 * 2, 12000);
        // Linkwitz-Riley crossovers (two Butterworth sections each) so the bands add back up flat.
        _lp1a = _lp1b = Biquad.Make(FilterShape.LowPass, f1, 0.70710678, 0, SampleRate);
        _hp1a = _hp1b = Biquad.Make(FilterShape.HighPass, f1, 0.70710678, 0, SampleRate);
        _lp2a = _lp2b = Biquad.Make(FilterShape.LowPass, f2, 0.70710678, 0, SampleRate);
        _hp2a = _hp2b = Biquad.Make(FilterShape.HighPass, f2, 0.70710678, 0, SampleRate);
        string[] names = ["l", "m", "h"];
        for (var b = 0; b < Bands; b++)
        {
            _cores[b].Set(CompStyle.Clean, r.P(names[b] + "thr", -20), r.P(names[b] + "ratio", 2.5), r.P("attack", b == 0 ? 20 : 8),
                r.P("release", b == 0 ? 200 : 120), 6);
            _makeup[b] = Db.ToGain(r.P(names[b] + "gain", 0));
        }
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        Array.Clear(_bandGr);
        for (var n = 0; n < frames; n++)
        {
            var l = left[n];
            var r = right is null ? l : right[n];
            Split(_st[0], l, out var l0, out var l1, out var l2);
            float r0 = l0, r1 = l1, r2 = l2;
            if (right is not null) Split(_st[1], r, out r0, out r1, out r2);
            var g0 = Gain(0, Math.Max(Math.Abs(l0), Math.Abs(r0)));
            var g1 = Gain(1, Math.Max(Math.Abs(l1), Math.Abs(r1)));
            var g2 = Gain(2, Math.Max(Math.Abs(l2), Math.Abs(r2)));
            left[n] = l0 * g0 + l1 * g1 + l2 * g2;
            if (right is not null) right[n] = r0 * g0 + r1 * g1 + r2 * g2;
        }
        Reduction = Math.Max(_bandGr[0], Math.Max(_bandGr[1], _bandGr[2]));
    }

    private float Gain(int band, float key)
    {
        var gr = _cores[band].Step(key);
        if (gr > _bandGr[band]) _bandGr[band] = gr;
        return (gr <= 0 ? 1f : MathF.Pow(10f, -gr / 20f)) * _makeup[band];
    }

    private void Split(BiquadState[] s, float x, out float low, out float mid, out float high)
    {
        var lo = s[1].Tick(_lp1b, s[0].Tick(_lp1a, x));
        var rest = s[3].Tick(_hp1b, s[2].Tick(_hp1a, x));
        mid = s[5].Tick(_lp2b, s[4].Tick(_lp2a, rest));
        high = s[7].Tick(_hp2b, s[6].Tick(_hp2a, rest));
        // The low band goes through the same phase turn as the upper split, so everything lines up again.
        low = s[9].Tick(_lp2b, s[8].Tick(_lp2a, lo)) + s[11].Tick(_hp2b, s[10].Tick(_hp2a, lo));
    }

    public override void Reset()
    {
        foreach (var side in _st)
            for (var i = 0; i < side.Length; i++) side[i].Reset();
        foreach (var c in _cores) c.Reset();
    }
}
