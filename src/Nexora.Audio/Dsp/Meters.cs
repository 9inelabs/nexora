namespace Nexora.Audio.Dsp;

/// <summary>
/// Loudness as streaming platforms measure it (ITU-R BS.1770 and EBU R128): K-weighted, in LUFS.
/// The audio thread only adds up energy in 100 ms pieces; the readings are worked out elsewhere.
/// </summary>
public sealed class LoudnessMeter
{
    private readonly Biquad _shelf, _hp;
    private BiquadState _s1l, _s2l, _s1r, _s2r;
    private readonly int _block;
    private double _acc;
    private int _count;
    // Finished 100 ms pieces, written by the audio thread and read by the control thread.
    private readonly double[] _ring = new double[256];
    private long _written;
    private long _read;
    private readonly List<double> _gating = [];
    private readonly Queue<double> _recent = new();
    private float _peak;
    private float _prevL, _prevR, _prev2L, _prev2R;

    public LoudnessMeter(double sampleRate)
    {
        _block = (int)(sampleRate / 10);
        // Stage 1: a high shelf modelling the head. Stage 2: a high-pass (the "RLB" curve).
        {
            const double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
            var k = Math.Tan(Math.PI * f0 / sampleRate);
            var vh = Math.Pow(10, g / 20);
            var vb = Math.Pow(vh, 0.4996667741545416);
            var a0 = 1 + k / q + k * k;
            _shelf = new Biquad((vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0,
                2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }
        {
            const double f0 = 38.13547087602444, q = 0.5003270373238773;
            var k = Math.Tan(Math.PI * f0 / sampleRate);
            var a0 = 1 + k / q + k * k;
            _hp = new Biquad(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }
    }

    /// <summary>On the audio thread.</summary>
    public void Push(float[] left, float[]? right, int frames)
    {
        for (var n = 0; n < frames; n++)
        {
            var l = left[n];
            var r = right is null ? l : right[n];
            var kl = _s2l.TickD(_hp, _s1l.TickD(_shelf, l));
            var kr = _s2r.TickD(_hp, _s1r.TickD(_shelf, r));
            _acc += kl * kl + (right is null ? 0 : kr * kr);
            // Peak between samples too (a simple 4x look between neighbours), close to "true peak".
            TruePeak(l, ref _prevL, ref _prev2L);
            if (right is not null) TruePeak(r, ref _prevR, ref _prev2R);
            if (++_count >= _block)
            {
                _ring[_written & (_ring.Length - 1)] = _acc / _block;
                Volatile.Write(ref _written, _written + 1);
                _acc = 0;
                _count = 0;
            }
        }
    }

    private void TruePeak(float x, ref float p1, ref float p2)
    {
        var a = Math.Abs(x);
        if (a > _peak) _peak = a;
        // Points between the last two samples (Catmull-Rom), so peaks between samples are caught too.
        for (var i = 1; i < 4; i++)
        {
            var t = i / 4f;
            var v = p1 + 0.5f * t * ((x - p2) + t * ((2 * p2 - 4 * p1 + 2 * x) + t * (-p2 + 2 * p1 - x)));
            v = Math.Abs(v);
            if (v > _peak) _peak = v;
        }
        p2 = p1;
        p1 = x;
    }

    public double Momentary { get; private set; } = double.NegativeInfinity;
    public double ShortTerm { get; private set; } = double.NegativeInfinity;
    public double Integrated { get; private set; } = double.NegativeInfinity;
    public double TruePeakDb { get; private set; } = double.NegativeInfinity;
    public double MaxTruePeakDb { get; private set; } = double.NegativeInfinity;

    public static double ToLufs(double meanSquare) => meanSquare <= 1e-15 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(meanSquare);

    /// <summary>Works out the readings from what the audio thread has added up (call a few times a second).</summary>
    public void Update()
    {
        var w = Volatile.Read(ref _written);
        if (w - _read > _ring.Length) _read = w - _ring.Length;
        while (_read < w)
        {
            var e = _ring[_read & (_ring.Length - 1)];
            _read++;
            _recent.Enqueue(e);
            while (_recent.Count > 30) _recent.Dequeue();
            if (_recent.Count >= 4)
            {
                var arr = _recent.ToArray();
                var block = (arr[^1] + arr[^2] + arr[^3] + arr[^4]) / 4;
                _gating.Add(block);
            }
        }
        var r = _recent.ToArray();
        if (r.Length >= 4) Momentary = ToLufs((r[^1] + r[^2] + r[^3] + r[^4]) / 4);
        if (r.Length > 0) ShortTerm = ToLufs(r.Average());
        Integrated = Gate(_gating);
        var pk = _peak;
        _peak = 0;
        TruePeakDb = pk <= 0 ? double.NegativeInfinity : 20 * Math.Log10(pk);
        if (TruePeakDb > MaxTruePeakDb) MaxTruePeakDb = TruePeakDb;
    }

    /// <summary>The integrated reading with the absolute (-70 LUFS) and relative (-10 LU) gates.</summary>
    public static double Gate(IReadOnlyList<double> blocks)
    {
        var abs = blocks.Where(b => ToLufs(b) > -70).ToList();
        if (abs.Count == 0) return double.NegativeInfinity;
        var rel = ToLufs(abs.Average()) - 10;
        var kept = abs.Where(b => ToLufs(b) > rel).ToList();
        return kept.Count == 0 ? double.NegativeInfinity : ToLufs(kept.Average());
    }

    public void ResetIntegrated()
    {
        _gating.Clear();
        MaxTruePeakDb = double.NegativeInfinity;
    }
}

/// <summary>A spectrum analyser: the audio thread copies samples in, the control thread looks at them.</summary>
public sealed class Analyser
{
    public const int Size = 4096;
    private readonly float[] _ring = new float[Size * 2];
    private long _write;
    private readonly double _sr;
    private readonly double[] _re = new double[Size], _im = new double[Size];
    private readonly double[] _win = new double[Size];
    private double[] _smooth = [];

    public Analyser(double sampleRate)
    {
        _sr = sampleRate;
        for (var i = 0; i < Size; i++) _win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1));
    }

    /// <summary>On the audio thread.</summary>
    public void Push(float[] left, float[]? right, int frames)
    {
        var w = _write;
        for (var n = 0; n < frames; n++)
            _ring[(w + n) & (_ring.Length - 1)] = right is null ? left[n] : (left[n] + right[n]) * 0.5f;
        Volatile.Write(ref _write, w + frames);
    }

    /// <summary>The level in each band (dB, 0 = full scale), on log-spaced bands from 20 Hz to 20 kHz.</summary>
    public float[] Bands(int count = 64)
    {
        var w = Volatile.Read(ref _write);
        for (var i = 0; i < Size; i++)
        {
            _re[i] = _ring[(w - Size + i) & (_ring.Length - 1)] * _win[i];
            _im[i] = 0;
        }
        Fft.Transform(_re, _im);
        var bins = new double[Size / 2];
        var norm = 2.0 / (Size * 0.5); // Hann window loses half the amplitude
        for (var k = 0; k < bins.Length; k++) bins[k] = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * norm;
        var result = new float[count];
        if (_smooth.Length != count) _smooth = Enumerable.Repeat(-120.0, count).ToArray();
        for (var b = 0; b < count; b++)
        {
            var f0 = 20 * Math.Pow(1000, (double)b / count);
            var f1 = 20 * Math.Pow(1000, (double)(b + 1) / count);
            var k0 = Math.Max(1, (int)(f0 / _sr * Size));
            var k1 = Math.Max(k0 + 1, (int)(f1 / _sr * Size));
            var m = 0.0;
            for (var k = k0; k < k1 && k < bins.Length; k++) m = Math.Max(m, bins[k]);
            var db = m <= 1e-9 ? -120 : 20 * Math.Log10(m);
            // Falls back gently so it is readable.
            _smooth[b] = db > _smooth[b] ? db : _smooth[b] - 2.5;
            result[b] = (float)_smooth[b];
        }
        return result;
    }

    public static double BandCentre(int band, int count = 64) => 20 * Math.Pow(1000, (band + 0.5) / count);
}

public static class Fft
{
    /// <summary>In-place radix-2 FFT (length must be a power of two).</summary>
    public static void Transform(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var j = 0; j < len / 2; j++)
                {
                    var ur = re[i + j];
                    var ui = im[i + j];
                    var vr = re[i + j + len / 2] * cr - im[i + j + len / 2] * ci;
                    var vi = re[i + j + len / 2] * ci + im[i + j + len / 2] * cr;
                    re[i + j] = ur + vr;
                    im[i + j] = ui + vi;
                    re[i + j + len / 2] = ur - vr;
                    im[i + j + len / 2] = ui - vi;
                    var nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
    }
}

/// <summary>
/// How alike left and right are, from +1 (the same: safe in mono) through 0 (unrelated: wide) to -1
/// (opposite: the sound cancels in mono, on a phone or a mono sub). It follows the music over about a third of a second.
/// Written on the audio thread, read on the control thread.
/// </summary>
public sealed class CorrelationMeter
{
    private readonly double _keep;
    private double _lr, _ll, _rr;

    public CorrelationMeter(double sampleRate, double seconds = 0.3) => _keep = Math.Exp(-1.0 / (Math.Max(0.01, seconds) * sampleRate));

    public void Push(float[] l, float[] r, int frames)
    {
        double lr = _lr, ll = _ll, rr = _rr, k = _keep, g = 1 - _keep;
        for (var n = 0; n < frames; n++)
        {
            double a = l[n], b = r[n];
            lr = lr * k + a * b * g;
            ll = ll * k + a * a * g;
            rr = rr * k + b * b * g;
        }
        // Denormals would slow the audio thread down in long silences.
        if (ll < 1e-20) ll = 0;
        if (rr < 1e-20) rr = 0;
        if (Math.Abs(lr) < 1e-20) lr = 0;
        _lr = lr;
        _ll = ll;
        _rr = rr;
    }

    /// <summary>The correlation now (-1 to +1), or NaN when it is too quiet to tell (below about -70 dB).</summary>
    public float Value
    {
        get
        {
            double ll = _ll, rr = _rr, lr = _lr;
            if (ll < 1e-7 || rr < 1e-7) return float.NaN;
            return (float)Math.Clamp(lr / Math.Sqrt(ll * rr), -1, 1);
        }
    }
}
