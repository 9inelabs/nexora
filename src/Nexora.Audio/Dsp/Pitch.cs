using Nexora.Audio.Mix;

namespace Nexora.Audio.Dsp;

/// <summary>
/// Finds the pitch of a voice with the YIN method (de Cheveigné and Kawahara, 2002): the delay at which
/// the sound best matches itself is one period. Works on a half-rate copy to keep the cost low.
/// </summary>
public sealed class PitchDetector
{
    private readonly double _rate;
    private readonly float[] _ring;
    private readonly float[] _buf;
    private readonly float[] _d;
    private readonly int _window, _maxLag, _minLag;
    private long _count;

    public PitchDetector(double analysisRate, double minHz = 70, double maxHz = 1000, double windowMs = 21)
    {
        _rate = analysisRate;
        _window = (int)(windowMs * 0.001 * analysisRate);
        _maxLag = (int)(analysisRate / minHz) + 2;
        _minLag = Math.Max(2, (int)(analysisRate / maxHz));
        _buf = new float[_window + _maxLag + 2];
        var size = 1;
        while (size < _buf.Length) size <<= 1;
        _ring = new float[size];
        _d = new float[_maxLag + 2];
    }

    public int Needed => _buf.Length;

    public void Push(float x)
    {
        _ring[_count & (_ring.Length - 1)] = x;
        _count++;
    }

    public bool Full => _count >= _buf.Length;

    /// <summary>Adds many samples at once (for tests and analysis).</summary>
    public void PushMany(ReadOnlySpan<float> xs)
    {
        foreach (var x in xs) Push(x);
    }

    /// <summary>The pitch in Hz, or 0 when the sound has no clear pitch (breath, consonants, silence).</summary>
    public double Detect(out double clarity, double threshold = 0.15)
    {
        clarity = 0;
        if (!Full) return 0;
        // Oldest first, into a straight buffer.
        for (var i = 0; i < _buf.Length; i++) _buf[i] = _ring[(_count - _buf.Length + i) & (_ring.Length - 1)];
        var w = _window;
        var energy = 0f;
        for (var j = 0; j < w; j++) energy += _buf[j] * _buf[j];
        if (energy < 1e-6f * w) return 0;
        _d[0] = 1;
        double running = 0;
        var best = -1;
        for (var tau = 1; tau <= _maxLag; tau++)
        {
            var s = 0f;
            var span = _buf.AsSpan();
            for (var j = 0; j < w; j++)
            {
                var diff = span[j] - span[j + tau];
                s += diff * diff;
            }
            running += s;
            _d[tau] = running <= 0 ? 1 : (float)(s * tau / running);
            if (tau >= _minLag + 1 && best < 0 && _d[tau - 1] < threshold && _d[tau] >= _d[tau - 1])
            {
                best = tau - 1;
                break;
            }
        }
        if (best < 0)
        {
            // No dip under the threshold: take the lowest one if it is still fairly clear.
            var min = float.MaxValue;
            for (var tau = _minLag; tau <= _maxLag; tau++)
                if (_d[tau] < min && _d[tau] != 0)
                {
                    min = _d[tau];
                    best = tau;
                }
            if (best < 0 || min > 0.35f) return 0;
        }
        // Parabolic interpolation around the dip for a finer answer.
        double t = best;
        if (best > 1 && best < _maxLag)
        {
            var a = _d[best - 1];
            var b = _d[best];
            var c = _d[best + 1];
            var den = a + c - 2 * b;
            if (Math.Abs(den) > 1e-9) t = best + 0.5 * (a - c) / den;
        }
        clarity = 1 - _d[best];
        return t > 0 ? _rate / t : 0;
    }
}

/// <summary>Musical keys and scales for pitch correction.</summary>
public static class Scales
{
    public static readonly string[] Keys = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    public static readonly IReadOnlyDictionary<string, int[]> Steps = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["chromatic"] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
        ["major"] = [0, 2, 4, 5, 7, 9, 11],
        ["minor"] = [0, 2, 3, 5, 7, 8, 10],
        ["pentatonic"] = [0, 2, 4, 7, 9],
    };

    public static double Midi(double hz) => 69 + 12 * Math.Log2(hz / 440.0);
    public static double Hz(double midi) => 440.0 * Math.Pow(2, (midi - 69) / 12);

    /// <summary>The nearest note of this key and scale, as a MIDI note number.</summary>
    public static int Nearest(double midi, string key, string scale)
    {
        var root = Array.FindIndex(Keys, k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (root < 0) root = 0;
        var steps = Steps.TryGetValue(scale, out var s) ? s : Steps["chromatic"];
        var bestNote = (int)Math.Round(midi);
        var bestDist = double.MaxValue;
        var octave = (int)Math.Floor(midi / 12) - 1;
        for (var o = octave; o <= octave + 2; o++)
            foreach (var st in steps)
            {
                var note = o * 12 + root + st;
                var dist = Math.Abs(note - midi);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestNote = note;
                }
            }
        return bestNote;
    }
}

/// <summary>
/// Pitch correction for live vocals: finds the sung pitch, works out the nearest note in the chosen key
/// and scale, and moves the voice towards it at the chosen speed. The shifting uses two crossfading read
/// heads one pitch period apart, which keeps the added delay to about one period of the voice.
/// </summary>
public sealed class PitchCorrector : Processor
{
    private readonly PitchDetector _detector;
    private readonly DelayLine _line;
    private Biquad _aa;
    private BiquadState _aaState;
    private int _decim, _hopLeft;
    private const int Hop = 256;
    private double _ratio = 1, _targetRatio = 1;
    private double _phase;
    private double _window;       // read-head spacing in samples (two periods)
    private double _speedCoef, _amount = 1;
    private string _key = "C", _scale = "major";
    private double _detected;

    public PitchCorrector(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _detector = new PitchDetector(sampleRate / 2);
        _line = new DelayLine((int)(0.06 * sampleRate));
        _aa = Biquad.Make(FilterShape.LowPass, Math.Min(5000, sampleRate * 0.2), 0.707, 0, sampleRate);
        _window = 0.01 * sampleRate;
        _phase = 0.5;
    }

    /// <summary>The pitch heard last, in Hz (0 when none).</summary>
    public double Detected => _detected;
    /// <summary>How far the voice is being moved, in semitones.</summary>
    public double Shift => 12 * Math.Log2(_ratio);

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        _key = r.T("key", "C");
        _scale = r.T("scale", "major");
        // Speed: 0 ms snaps straight to the note (the obvious "robot" effect); 50 to 150 ms sounds natural.
        var ms = Math.Clamp(r.P("speed", 60), 0, 500);
        _speedCoef = ms <= 0 ? 0 : Math.Exp(-Hop / (ms * 0.001 * SampleRate));
        _amount = Math.Clamp(r.P("amount", 100) / 100.0, 0, 1);
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        for (var n = 0; n < frames; n++)
        {
            var x = right is null ? left[n] : (left[n] + right[n]) * 0.5f;
            // Analysis at half rate after a gentle low-pass.
            var a = _aaState.Tick(_aa, x);
            if ((_decim++ & 1) == 0) _detector.Push(a);
            if (--_hopLeft <= 0)
            {
                _hopLeft = Hop;
                Analyse();
            }
            _ratio = _targetRatio + (_ratio - _targetRatio) * _speedCoefPerSample;

            _line.Write(x);
            // Two read heads, half a window apart, sliding at a speed set by the pitch ratio.
            _phase += (1 - _ratio) / _window;
            if (_phase >= 1) _phase -= 1;
            else if (_phase < 0) _phase += 1;
            var p2 = _phase + 0.5;
            if (p2 >= 1) p2 -= 1;
            var wa = Math.Sin(Math.PI * _phase);
            wa *= wa;
            var y = _line.ReadCubic(_phase * _window + 2) * (float)wa + _line.ReadCubic(p2 * _window + 2) * (float)(1 - wa);
            left[n] = y;
            if (right is not null) right[n] = y;
        }
        Reduction = (float)Math.Abs(Shift);
    }

    private double _speedCoefPerSample => _speedCoef <= 0 ? 0 : Math.Pow(_speedCoef, 1.0 / Hop);

    private void Analyse()
    {
        var hz = _detector.Detect(out var clarity);
        if (hz <= 0 || clarity < 0.6)
        {
            _detected = 0;
            _targetRatio = 1; // no clear pitch: leave the sound alone
            return;
        }
        _detected = hz;
        var midi = Scales.Midi(hz);
        var target = Scales.Nearest(midi, _key, _scale);
        var semis = (target - midi) * _amount;
        _targetRatio = Math.Pow(2, semis / 12);
        // Read heads one period apart keep the two copies in step (no comb filtering).
        var period = SampleRate / hz;
        var w = Math.Clamp(period * 2, 0.004 * SampleRate, 0.028 * SampleRate);
        _window += (w - _window) * 0.5;
    }

    public override void Reset()
    {
        _line.Clear();
        _ratio = _targetRatio = 1;
    }
}
