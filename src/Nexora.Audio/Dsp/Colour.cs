using Nexora.Audio.Mix;

namespace Nexora.Audio.Dsp;

/// <summary>
/// The strip's saturation stage: a smooth curve that rounds off peaks and adds harmonics.
/// "Warm" leans the curve to one side for mostly even harmonics; "Edge" keeps it symmetric for odd ones.
/// The level is matched afterwards, so turning up the drive changes the colour more than the loudness.
/// </summary>
public sealed class Saturator : Processor
{
    private bool _on;
    private bool _warm = true;
    private float _drive = 2, _makeup = 0.5f, _mix = 1, _out = 1, _bias;
    // A gentle high-pass (about 8 Hz) takes away the offset the lopsided Warm curve leaves.
    private float _dcL, _dcR, _xl, _xr;
    private readonly float _dcCoef;
    private readonly float[] _dryL, _dryR;

    public Saturator(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _dcCoef = (float)Math.Exp(-2 * Math.PI * 8 / sampleRate);
        _dryL = new float[maxFrames];
        _dryR = new float[maxFrames];
    }

    public bool On => _on;

    protected override void Apply(object settings)
    {
        if (settings is not DriveSettings d) return;
        _on = d.On;
        _warm = d.Style == DriveStyle.Warm;
        _drive = Db.ToGain(Math.Clamp(d.Drive, 0, 24));
        _bias = _warm ? 0.18f : 0f;
        // Small signals pass at the same level: tanh(g x) is about g x near zero, so divide by g.
        _makeup = 1f / _drive;
        _mix = (float)Math.Clamp(d.Mix / 100, 0, 1);
        _out = Db.ToGain(Math.Clamp(d.Output, -12, 12));
    }

    /// <summary>The curve itself, for drawing (input and output in linear terms).</summary>
    public static float Curve(float x, DriveSettings d)
    {
        var g = Db.ToGain(Math.Clamp(d.Drive, 0, 24));
        var bias = d.Style == DriveStyle.Warm ? 0.18f : 0f;
        return Shape(x * g, bias, d.Style == DriveStyle.Warm) / g;
    }

    private static float Shape(float x, float bias, bool warm)
    {
        if (warm) return MathF.Tanh(x + bias) - MathF.Tanh(bias);
        // Edge: a firmer knee than tanh (x / (1 + |x|^2.5)^(1/2.5)), still smooth.
        var a = MathF.Abs(x);
        return x / MathF.Pow(1 + MathF.Pow(a, 2.5f), 0.4f);
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        if (!_on) return;
        var parallel = _mix < 0.999f;
        if (parallel)
        {
            Array.Copy(left, _dryL, frames);
            if (right is not null) Array.Copy(right, _dryR, frames);
        }
        Run(left, frames, ref _dcL, ref _xl);
        if (right is not null) Run(right, frames, ref _dcR, ref _xr);
        if (parallel)
        {
            var dry = 1 - _mix;
            for (var n = 0; n < frames; n++) left[n] = left[n] * _mix + _dryL[n] * dry * _out;
            if (right is not null)
                for (var n = 0; n < frames; n++) right[n] = right[n] * _mix + _dryR[n] * dry * _out;
        }
    }

    private void Run(float[] buf, int frames, ref float dc, ref float prev)
    {
        for (var n = 0; n < frames; n++)
        {
            var y = Shape(buf[n] * _drive, _bias, _warm) * _makeup;
            if (_warm)
            {
                // One-pole DC blocker: y[n] = x[n] - x[n-1] + c * y[n-1].
                var hp = y - prev + _dcCoef * dc;
                prev = y;
                dc = Math.Abs(hp) < 1e-20f ? 0 : hp;
                y = hp;
            }
            buf[n] = y * _out;
        }
    }

    public override void Reset() => _dcL = _dcR = _xl = _xr = 0;
}

/// <summary>
/// Dynamic EQ: each band is a bell that listens to its own frequency range and only moves when that range
/// goes over its threshold, cutting (or lifting) by up to its range. Coefficients are worked out every
/// 32 samples, from a gain that glides with the band's attack and release, so it never zips or clicks.
/// </summary>
public sealed class DynamicEq : Processor
{
    private const int Block = 32;

    private sealed class Band
    {
        public bool On;
        public double Freq = 1000, Q = 1, Range;
        public float Thr = -30, Slope = 0.66f, Att, Rel;
        public Biquad Detect = Biquad.Identity;
        public BiquadState DetL, DetR, L, R;
        public float Env;       // detector level (linear peak)
        public float GainDb;    // where the bell is now
        public float EnvRel;
    }

    private Band[] _bands = [];
    private bool _on;

    public DynamicEq(double sampleRate, int maxFrames) : base(sampleRate, maxFrames) { }

    /// <summary>Where each band's bell is now, in dB (for the screen).</summary>
    public float[] Now { get; private set; } = [];

    protected override void Apply(object settings)
    {
        if (settings is not DynEqSettings s) return;
        _on = s.On;
        if (_bands.Length != s.Bands.Count)
        {
            var nb = new Band[s.Bands.Count];
            for (var i = 0; i < nb.Length; i++) nb[i] = i < _bands.Length ? _bands[i] : new Band();
            _bands = nb;
            Now = new float[nb.Length];
        }
        for (var i = 0; i < s.Bands.Count; i++)
        {
            var src = s.Bands[i];
            var b = _bands[i];
            if (Math.Abs(src.Freq - b.Freq) > 0.01 || Math.Abs(src.Q - b.Q) > 0.001)
                b.Detect = Biquad.Make(FilterShape.BandPass, src.Freq, src.Q, 0, SampleRate);
            b.On = src.On;
            b.Freq = src.Freq;
            b.Q = src.Q;
            b.Range = Math.Clamp(src.Range, -18, 12);
            b.Thr = (float)src.Threshold;
            b.Slope = 1f - 1f / (float)Math.Max(1, src.Ratio);
            // Smoothing per 32-sample block.
            b.Att = (float)Math.Exp(-Block / (Math.Max(0.1, src.Attack) * 0.001 * SampleRate));
            b.Rel = (float)Math.Exp(-Block / (Math.Max(10, src.Release) * 0.001 * SampleRate));
            b.EnvRel = (float)Dsp.Coef(20, SampleRate);
            if (b.Detect.IsIdentity) b.Detect = Biquad.Make(FilterShape.BandPass, src.Freq, src.Q, 0, SampleRate);
        }
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        if (!_on || _bands.Length == 0)
        {
            Reduction = 0;
            return;
        }
        var most = 0f;
        for (var bi = 0; bi < _bands.Length; bi++)
        {
            var b = _bands[bi];
            if (!b.On && Math.Abs(b.GainDb) < 0.01f) continue;
            for (var start = 0; start < frames; start += Block)
            {
                var len = Math.Min(Block, frames - start);
                // Listen to the band's own range.
                for (var n = start; n < start + len; n++)
                {
                    var dl = MathF.Abs(b.DetL.Tick(b.Detect, left[n]));
                    var d = right is null ? dl : Math.Max(dl, MathF.Abs(b.DetR.Tick(b.Detect, right[n])));
                    b.Env = d > b.Env ? d : b.EnvRel * b.Env + (1 - b.EnvRel) * d;
                }
                var level = b.Env <= 1e-6f ? -120f : 20f * MathF.Log10(b.Env);
                var over = level - b.Thr;
                var amount = b.On && over > 0 ? Math.Min(over * b.Slope, (float)Math.Abs(b.Range)) : 0f;
                var target = b.Range < 0 ? -amount : amount;
                // Moving away from flat uses the attack, coming back uses the release.
                var c = Math.Abs(target) > Math.Abs(b.GainDb) ? b.Att : b.Rel;
                b.GainDb = c * b.GainDb + (1 - c) * target;
                // Never exactly flat, so the filter keeps running and its memory stays warm (no click when it moves).
                var g = Math.Abs(b.GainDb) < 1e-4f ? 1e-4f : b.GainDb;
                var coefs = Biquad.Make(FilterShape.Peak, b.Freq, b.Q, g, SampleRate);
                b.L.Process(coefs, left.AsSpan(start, len));
                if (right is not null) b.R.Process(coefs, right.AsSpan(start, len));
            }
            if (bi < Now.Length) Now[bi] = b.GainDb;
            most = Math.Max(most, Math.Abs(b.GainDb));
        }
        Reduction = most;
    }

    public override void Reset()
    {
        foreach (var b in _bands)
        {
            b.DetL.Reset(); b.DetR.Reset(); b.L.Reset(); b.R.Reset();
            b.Env = 0; b.GainDb = 0;
        }
    }
}
