using Nexora.Audio.Mix;

namespace Nexora.Audio.Dsp;

/// <summary>
/// One filter band that glides to new settings over a few buffers instead of jumping,
/// so turning an EQ knob during a service never clicks.
/// </summary>
internal sealed class GlideBand
{
    private readonly double _sr;
    private FilterShape _shape;
    private double _f = 1000, _g, _q = 0.7;          // where the band is now
    private double _tf = 1000, _tg, _tq = 0.7;       // where it is going
    private bool _active, _wantActive, _gainType = true;
    private Biquad _c = Biquad.Identity;
    private BiquadState _l, _r;

    public GlideBand(double sampleRate) => _sr = sampleRate;

    public bool Active => _active;
    public Biquad Coefs => _c;

    public void Set(FilterShape shape, double freq, double gain, double q, bool on)
    {
        freq = Math.Clamp(freq, 5, _sr * 0.49);
        q = Math.Clamp(q, 0.05, 40);
        var gainType = shape is FilterShape.Peak or FilterShape.LowShelf or FilterShape.HighShelf;
        if (shape != _shape)
        {
            _l.Reset();
            _r.Reset();
            _shape = shape;
            _f = freq; _q = q; _g = 0;
            _active = false;
        }
        _gainType = gainType;
        _tf = freq;
        _tq = q;
        if (gainType)
        {
            _tg = on ? gain : 0;
            _wantActive = on && Math.Abs(gain) > 0.01;
            if (_wantActive && !_active)
            {
                // Starts flat where it is asked to be, then the gain glides in.
                _f = freq; _q = q; _g = 0;
                _active = true;
            }
        }
        else
        {
            _tg = 0;
            _wantActive = on;
            if (!on) _active = false;
            else if (!_active)
            {
                _f = freq; _q = q;
                _l.Reset();
                _r.Reset();
                _active = true;
            }
        }
        Update(true);
    }

    private void Update(bool force)
    {
        if (!_active) return;
        var moving = Math.Abs(_f - _tf) > _tf * 0.002 || Math.Abs(_g - _tg) > 0.02 || Math.Abs(_q - _tq) > 0.005;
        if (!moving && !force) return;
        if (moving)
        {
            // Glide in log frequency, about 90% of the way in 8 buffers.
            const double k = 0.25;
            _f = Math.Exp(Math.Log(_f) + (Math.Log(_tf) - Math.Log(_f)) * k);
            _g += (_tg - _g) * k;
            _q += (_tq - _q) * k;
            if (Math.Abs(_f - _tf) <= _tf * 0.002) _f = _tf;
            if (Math.Abs(_g - _tg) <= 0.02) _g = _tg;
            if (Math.Abs(_q - _tq) <= 0.005) _q = _tq;
        }
        if (_gainType && !_wantActive && Math.Abs(_g) <= 0.02)
        {
            _active = false;
            _g = 0;
            _c = Biquad.Identity;
            return;
        }
        _c = Biquad.Make(_shape, _f, _q, _g, _sr);
    }

    public void Process(float[] left, float[]? right, int frames)
    {
        Update(false);
        if (!_active) return;
        _l.Process(_c, left.AsSpan(0, frames));
        if (right is not null) _r.Process(_c, right.AsSpan(0, frames));
    }

    public void Reset()
    {
        _l.Reset();
        _r.Reset();
    }
}

/// <summary>The settings a <see cref="StripFilters"/> takes (copies, so the control thread can keep editing its own).</summary>
public sealed record FilterSettings(PassFilter Hpf, PassFilter Lpf, EqSettings Eq)
{
    public static FilterSettings From(Strip s) => new(s.Hpf.Clone(), s.Lpf.Clone(), s.Eq.Clone());
    public static FilterSettings From(OutputPatch o) => new(o.Hpf.Clone(), o.Lpf.Clone(), o.Eq.Clone());
}

/// <summary>A strip's high-pass, low-pass and parametric EQ.</summary>
public sealed class StripFilters : Processor
{
    private readonly GlideBand[] _hp = new GlideBand[2], _lp = new GlideBand[2];
    private GlideBand[] _bands = [];
    private int _hpOrder, _lpOrder;

    public StripFilters(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        for (var i = 0; i < 2; i++)
        {
            _hp[i] = new GlideBand(sampleRate);
            _lp[i] = new GlideBand(sampleRate);
        }
    }

    protected override void Apply(object settings)
    {
        if (settings is not FilterSettings s) return;
        _hpOrder = s.Hpf.Slope >= 24 ? 2 : 1;
        var hq = Butterworth.Qs(_hpOrder * 2);
        for (var i = 0; i < 2; i++) _hp[i].Set(FilterShape.HighPass, s.Hpf.Freq, 0, i < hq.Length ? hq[i] : 0.707, s.Hpf.On && i < _hpOrder);
        _lpOrder = s.Lpf.Slope >= 24 ? 2 : 1;
        var lq = Butterworth.Qs(_lpOrder * 2);
        for (var i = 0; i < 2; i++) _lp[i].Set(FilterShape.LowPass, s.Lpf.Freq, 0, i < lq.Length ? lq[i] : 0.707, s.Lpf.On && i < _lpOrder);

        var bands = s.Eq.Bands;
        if (_bands.Length != bands.Count)
        {
            // Bands added or removed: keep the ones still there (runs rarely: only when the layout changes).
            var nb = new GlideBand[bands.Count];
            for (var i = 0; i < nb.Length; i++) nb[i] = i < _bands.Length ? _bands[i] : new GlideBand(SampleRate);
            _bands = nb;
        }
        for (var i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            _bands[i].Set(b.Shape, b.Freq, b.Gain, b.Q, s.Eq.On && b.On);
        }
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        for (var i = 0; i < 2; i++) _hp[i].Process(left, right, frames);
        for (var i = 0; i < _bands.Length; i++) _bands[i].Process(left, right, frames);
        for (var i = 0; i < 2; i++) _lp[i].Process(left, right, frames);
    }

    public override void Reset()
    {
        foreach (var b in _hp) b.Reset();
        foreach (var b in _lp) b.Reset();
        foreach (var b in _bands) b.Reset();
    }

    /// <summary>The combined response of these settings at a frequency, for drawing the EQ curve.</summary>
    public static double ResponseDb(FilterSettings s, double freq, double sampleRate)
    {
        var db = 0.0;
        if (s.Hpf.On)
            foreach (var q in Butterworth.Qs(s.Hpf.Slope >= 24 ? 4 : 2))
                db += Biquad.Make(FilterShape.HighPass, s.Hpf.Freq, q, 0, sampleRate).MagnitudeDb(freq, sampleRate);
        if (s.Lpf.On)
            foreach (var q in Butterworth.Qs(s.Lpf.Slope >= 24 ? 4 : 2))
                db += Biquad.Make(FilterShape.LowPass, s.Lpf.Freq, q, 0, sampleRate).MagnitudeDb(freq, sampleRate);
        if (s.Eq.On)
            foreach (var b in s.Eq.Bands)
                if (b.On)
                    db += Biquad.Make(b.Shape, b.Freq, b.Q, b.Gain, sampleRate).MagnitudeDb(freq, sampleRate);
        return db;
    }
}

/// <summary>A 31-band graphic EQ on the standard third-octave centres, for speakers and wedges.</summary>
public sealed class GraphicEq : Processor
{
    public static readonly double[] Centres =
    [
        20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600,
        2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000,
    ];

    /// <summary>A third of an octave wide.</summary>
    public const double BandQ = 4.318;

    private readonly GlideBand[] _bands;

    public GraphicEq(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _bands = Centres.Select(_ => new GlideBand(sampleRate)).ToArray();
    }

    public static string ParamName(int band) => "b" + band;

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        for (var i = 0; i < _bands.Length; i++)
        {
            var g = Math.Clamp(r.P(ParamName(i), 0), -15, 15);
            _bands[i].Set(FilterShape.Peak, Math.Min(Centres[i], SampleRate * 0.45), g, BandQ, Math.Abs(g) > 0.01);
        }
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        foreach (var b in _bands) b.Process(left, right, frames);
    }

    public override void Reset()
    {
        foreach (var b in _bands) b.Reset();
    }
}
