using Nexora.Audio.Mix;

namespace Nexora.Audio.Dsp;

/// <summary>A Schroeder all-pass: smears sound in time without colouring it, used to thicken reverbs.</summary>
internal sealed class AllPassDiffuser
{
    private readonly float[] _buf;
    private int _pos, _len;
    public float Gain = 0.7f;

    public AllPassDiffuser(int maxLen)
    {
        _buf = new float[maxLen + 1];
        _len = maxLen;
    }

    public void SetLength(int len) => _len = Math.Clamp(len, 1, _buf.Length - 1);

    public float Tick(float x)
    {
        if (_pos >= _len) _pos = 0;
        var d = _buf[_pos];
        var v = x + d * Gain;
        _buf[_pos] = v;
        _pos++;
        return d - v * Gain;
    }

    public void Clear() => Array.Clear(_buf);
}

/// <summary>
/// NEX-Audio's reverb: a feedback delay network of eight delay lines mixed through a Householder matrix
/// (a standard published structure), with all-pass diffusion in front, early reflections, gentle
/// modulation against metallic ringing and high-frequency damping. Three styles: room, hall and plate.
/// </summary>
public sealed class Reverb : Processor
{
    private const int Lines = 8;
    // Delay lengths at 48 kHz for a size of 100%, chosen to share no common factors.
    private static readonly int[] BaseLengths = [1949, 2137, 2411, 2633, 2851, 3089, 3301, 3529];
    private static readonly int[] DiffuseLengths = [229, 173, 449, 337];
    private static readonly float[] EarlyTimes = [0.0043f, 0.0097f, 0.0151f, 0.0213f, 0.0277f, 0.0331f];
    private static readonly float[] EarlyGains = [0.62f, 0.51f, 0.44f, 0.35f, 0.29f, 0.22f];

    private readonly DelayLine[] _lines = new DelayLine[Lines];
    private readonly float[] _len = new float[Lines];
    private readonly float[] _fb = new float[Lines];
    private readonly float[] _damp = new float[Lines];
    private readonly float[] _lp = new float[Lines];
    private readonly AllPassDiffuser[] _diff = new AllPassDiffuser[4];
    private readonly DelayLine _pre;
    private readonly float[] _dryL, _dryR;
    private int _preDelay;
    private float _wet = 1, _dry, _width = 1, _early = 0.5f;
    private double _lfo;
    private double _lfoStep;
    private Biquad _lowCut = Biquad.Identity, _highCut = Biquad.Identity;
    private BiquadState _lcL, _lcR, _hcL, _hcR;
    private readonly float[] _x = new float[Lines], _y = new float[Lines];
    private float _scale;

    public Reverb(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        var sr = sampleRate / 48000.0;
        for (var i = 0; i < Lines; i++) _lines[i] = new DelayLine((int)(BaseLengths[i] * 1.6 * sr) + 64);
        for (var i = 0; i < 4; i++) _diff[i] = new AllPassDiffuser((int)(DiffuseLengths[i] * 1.6 * sr) + 4);
        _pre = new DelayLine((int)(0.25 * sampleRate) + (int)(0.04 * sampleRate));
        _dryL = new float[maxFrames];
        _dryR = new float[maxFrames];
        Apply(Effects.Default("reverb", "hall"));
    }

    public record Style(double Size, double Decay, double Damping, double Diffusion, double Early, double ModDepth);

    public static Style StyleFor(string name) => name.ToLowerInvariant() switch
    {
        "room" => new Style(0.42, 0.9, 0.45, 0.62, 0.8, 3),
        "plate" => new Style(0.62, 1.7, 0.25, 0.78, 0.15, 6),
        _ => new Style(1.0, 2.3, 0.5, 0.7, 0.45, 9),
    };

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        var style = StyleFor(r.T("style", "hall"));
        var size = Math.Clamp(r.P("size", 100) / 100.0, 0.2, 1.6) * style.Size;
        var decay = Math.Clamp(r.P("decay", style.Decay), 0.1, 12);
        var damping = Math.Clamp(r.P("damping", style.Damping * 100) / 100.0, 0, 0.95);
        var sr = SampleRate / 48000.0;
        for (var i = 0; i < Lines; i++)
        {
            _len[i] = (float)Math.Max(64, BaseLengths[i] * size * sr);
            // Each line loses 60 dB over the decay time.
            _fb[i] = (float)Math.Pow(10, -3.0 * _len[i] / (decay * SampleRate));
            // High frequencies die away faster (more with more damping, and in longer lines).
            _damp[i] = (float)Math.Clamp(damping * (0.6 + 0.4 * _len[i] / (BaseLengths[^1] * sr)), 0, 0.97);
        }
        for (var i = 0; i < 4; i++)
        {
            _diff[i].SetLength((int)Math.Max(8, DiffuseLengths[i] * Math.Sqrt(size) * sr));
            _diff[i].Gain = (float)style.Diffusion;
        }
        _preDelay = (int)(Math.Clamp(r.P("predelay", 20), 0, 250) * 0.001 * SampleRate);
        var mix = Math.Clamp(r.P("mix", 100) / 100.0, 0, 1);
        _wet = (float)mix;
        _dry = (float)(1 - mix);
        _width = (float)Math.Clamp(r.P("width", 100) / 100.0, 0, 1);
        _early = (float)style.Early;
        _lfoStep = 2 * Math.PI * 0.7 / SampleRate;
        _modDepth = (float)style.ModDepth;
        var low = r.P("lowcut", 120);
        var high = r.P("highcut", 9000);
        _lowCut = low > 20 ? Biquad.Make(FilterShape.HighPass1, low, 0.707, 0, SampleRate) : Biquad.Identity;
        _highCut = high < 19000 ? Biquad.Make(FilterShape.LowPass1, high, 0.707, 0, SampleRate) : Biquad.Identity;
        _scale = 0.35f;
    }

    private float _modDepth = 8;

    public override int Latency => 0;

    protected override void Process(float[] left, float[]? right, int frames)
    {
        var stereo = right is not null;
        if (_dry > 0)
        {
            Array.Copy(left, _dryL, frames);
            if (stereo) Array.Copy(right!, _dryR, frames);
        }
        var earlyTaps = new Span<int>(_earlyTap, 0, EarlyTimes.Length);
        for (var i = 0; i < EarlyTimes.Length; i++) earlyTaps[i] = _preDelay + (int)(EarlyTimes[i] * SampleRate * _len[0] / BaseLengths[0]) + 1;
        for (var n = 0; n < frames; n++)
        {
            var input = stereo ? (left[n] + right![n]) * 0.5f : left[n];
            _pre.Write(input);
            var x = _pre.Read(_preDelay + 1);
            for (var d = 0; d < 4; d++) x = _diff[d].Tick(x);

            // Early reflections, spread left and right.
            float eL = 0, eR = 0;
            for (var i = 0; i < earlyTaps.Length; i++)
            {
                var s = _pre.Read(earlyTaps[i]) * EarlyGains[i];
                if ((i & 1) == 0) eL += s; else eR += s;
            }

            // Read every line (two of them gently modulated), damp, and apply its decay.
            _lfo += _lfoStep;
            if (_lfo > 2 * Math.PI) _lfo -= 2 * Math.PI;
            var mod = (float)Math.Sin(_lfo) * _modDepth;
            var sum = 0f;
            for (var i = 0; i < Lines; i++)
            {
                var delay = _len[i] + (i == 1 ? mod : i == 5 ? -mod : 0f);
                var v = i == 1 || i == 5 ? _lines[i].ReadFrac(delay) : _lines[i].Read((int)delay);
                _lp[i] = v + (_lp[i] - v) * _damp[i];
                v = _lp[i] * _fb[i];
                _y[i] = v;
                sum += v;
            }
            // Householder mix: every line feeds every other, energy is kept.
            var h = sum * (2f / Lines);
            for (var i = 0; i < Lines; i++)
            {
                var fbv = _y[i] - h + ((i & 1) == 0 ? x : -x);
                _lines[i].Write(Math.Abs(fbv) < 1e-20f ? 0 : fbv);
            }
            var outL = (_y[0] + _y[2] + _y[4] + _y[6] - _y[1] * 0.5f) * _scale + eL * _early * 0.5f;
            var outR = (_y[1] + _y[3] + _y[5] + _y[7] - _y[6] * 0.5f) * _scale + eR * _early * 0.5f;
            outL = _lcL.Tick(_lowCut, outL);
            outR = _lcR.Tick(_lowCut, outR);
            outL = _hcL.Tick(_highCut, outL);
            outR = _hcR.Tick(_highCut, outR);
            var mid = (outL + outR) * 0.5f;
            outL = mid + (outL - mid) * _width;
            outR = mid + (outR - mid) * _width;
            if (stereo)
            {
                left[n] = outL * _wet;
                right![n] = outR * _wet;
            }
            else left[n] = (outL + outR) * 0.5f * _wet;
        }
        if (_dry > 0)
        {
            for (var n = 0; n < frames; n++) left[n] += _dryL[n] * _dry;
            if (stereo) for (var n = 0; n < frames; n++) right![n] += _dryR[n] * _dry;
        }
    }

    private readonly int[] _earlyTap = new int[8];

    public override void Reset()
    {
        foreach (var l in _lines) l.Clear();
        foreach (var d in _diff) d.Clear();
        _pre.Clear();
        Array.Clear(_lp);
    }
}

/// <summary>A stereo delay with tempo sync, filtered repeats and ping-pong.</summary>
public sealed class Echo : Processor
{
    private readonly DelayLine _l, _r;
    private readonly int _max;
    private double _time, _target;
    private float _fb = 0.35f, _wet = 1, _dry;
    private bool _ping;
    private Biquad _lc = Biquad.Identity, _hc = Biquad.Identity;
    private BiquadState _lcl, _lcr, _hcl, _hcr;

    public Echo(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _max = (int)(2.1 * sampleRate);
        _l = new DelayLine(_max);
        _r = new DelayLine(_max);
        _time = _target = 0.375 * sampleRate;
    }

    /// <summary>The delay in milliseconds for these settings (tempo sync wins when a tempo is set).</summary>
    public static double TimeMs(RackItem r)
    {
        var bpm = r.P("bpm", 0);
        if (bpm >= 30) return Math.Min(2000, 60000.0 / bpm * Math.Clamp(r.P("beats", 0.75), 0.0625, 4));
        return Math.Clamp(r.P("time", 375), 1, 2000);
    }

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        _target = Math.Clamp(TimeMs(r) * 0.001 * SampleRate, 1, _max - 4);
        if (_time <= 1) _time = _target;
        _fb = (float)Math.Clamp(r.P("feedback", 35) / 100.0, 0, 0.95);
        var mix = Math.Clamp(r.P("mix", 100) / 100.0, 0, 1);
        _wet = (float)mix;
        _dry = (float)(1 - mix);
        _ping = r.P("pingpong", 0) >= 0.5;
        var low = r.P("lowcut", 150);
        var high = r.P("highcut", 6000);
        _lc = low > 20 ? Biquad.Make(FilterShape.HighPass1, low, 0.707, 0, SampleRate) : Biquad.Identity;
        _hc = high < 19000 ? Biquad.Make(FilterShape.LowPass1, high, 0.707, 0, SampleRate) : Biquad.Identity;
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        var stereo = right is not null;
        for (var n = 0; n < frames; n++)
        {
            // Time changes glide, like a tape delay, so nothing clicks.
            _time += (_target - _time) * 0.0005;
            var inL = left[n];
            var inR = stereo ? right![n] : inL;
            var dl = _l.ReadFrac(_time);
            var dr = _r.ReadFrac(_time);
            var fl = _hcl.Tick(_hc, _lcl.Tick(_lc, dl)) * _fb;
            var fr = _hcr.Tick(_hc, _lcr.Tick(_lc, dr)) * _fb;
            if (_ping)
            {
                _l.Write((inL + inR) * 0.5f + fr);
                _r.Write(fl);
            }
            else
            {
                _l.Write(inL + fl);
                _r.Write(inR + fr);
            }
            if (stereo)
            {
                left[n] = inL * _dry + dl * _wet;
                right![n] = inR * _dry + dr * _wet;
            }
            else left[n] = inL * _dry + (dl + dr) * 0.5f * _wet;
        }
    }

    public override void Reset()
    {
        _l.Clear();
        _r.Clear();
    }
}

/// <summary>A chorus: copies of the sound, slightly delayed and gently wobbling in pitch, for width and shimmer.</summary>
public sealed class Chorus : Processor
{
    private readonly DelayLine _l, _r;
    private double _phase, _step;
    private float _base, _depth, _wet = 0.5f, _dry = 0.5f;

    public Chorus(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _l = new DelayLine((int)(0.05 * sampleRate));
        _r = new DelayLine((int)(0.05 * sampleRate));
    }

    protected override void Apply(object settings)
    {
        if (settings is not RackItem r) return;
        _step = 2 * Math.PI * Math.Clamp(r.P("rate", 0.8), 0.05, 8) / SampleRate;
        _base = (float)(Math.Clamp(r.P("delay", 12), 3, 30) * 0.001 * SampleRate);
        _depth = (float)(Math.Clamp(r.P("depth", 30) / 100.0, 0, 1) * 0.004 * SampleRate);
        var mix = Math.Clamp(r.P("mix", 50) / 100.0, 0, 1);
        _wet = (float)mix;
        _dry = (float)(1 - mix * 0.5);
    }

    protected override void Process(float[] left, float[]? right, int frames)
    {
        var stereo = right is not null;
        for (var n = 0; n < frames; n++)
        {
            _phase += _step;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            var inL = left[n];
            var inR = stereo ? right![n] : inL;
            _l.Write(inL);
            _r.Write(inR);
            // Two voices per side, a quarter of a turn apart; left and right move in opposite ways.
            var s1 = (float)Math.Sin(_phase);
            var s2 = (float)Math.Cos(_phase);
            var wl = (_l.ReadCubic(_base + _depth * (1 + s1)) + _l.ReadCubic(_base * 1.37f + _depth * (1 + s2))) * 0.5f;
            var wr = (_r.ReadCubic(_base + _depth * (1 - s1)) + _r.ReadCubic(_base * 1.37f + _depth * (1 - s2))) * 0.5f;
            if (stereo)
            {
                left[n] = inL * _dry + wl * _wet;
                right![n] = inR * _dry + wr * _wet;
            }
            else left[n] = inL * _dry + (wl + wr) * 0.5f * _wet;
        }
    }

    public override void Reset()
    {
        _l.Clear();
        _r.Clear();
    }
}
