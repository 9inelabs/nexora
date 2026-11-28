using Nexora.Audio.Mix;

namespace Nexora.Audio.Dsp;

/// <summary>A drum sample ready to play: mono, at the desk's sample rate. Made on the control thread, never changed after.</summary>
public sealed class DrumSample(string name, float[] data)
{
    public string Name { get; } = name;
    public float[] Data { get; } = data;
}

/// <summary>What the trigger hands the audio thread: the settings and the samples, already loaded.</summary>
public sealed class TriggerConfig
{
    public required TriggerSettings Settings { get; init; }
    /// <summary>The layers that have samples, softest first. Empty: nothing to play, so the natural mic is heard.</summary>
    public required DrumSample[][] Layers { get; init; }
}

/// <summary>What happened on one hit, for the hit history.</summary>
public enum HitKind : byte
{
    /// <summary>Fired a sample.</summary>
    Fired,
    /// <summary>Close to the threshold but under it, so nothing played.</summary>
    TooSoft,
}

/// <summary>One hit the trigger heard. Level is the hit's peak in dB after the spill filter; Layer counts from 0.</summary>
public readonly record struct TriggerHit(long Sample, HitKind Kind, float Level, byte Layer, float Strength);

/// <summary>
/// The drum trigger: listens to the mic (or another channel) through a spill filter, spots each hit above the threshold,
/// measures how hard it was over the first millisecond, and plays a sample from the matching layer at that strength.
/// The natural mic and the samples are blended; with some natural mic in, an optional gate lets it through only around hits.
/// Nothing on the audio thread allocates: up to eight samples ring at once, the oldest fading out to make room.
/// </summary>
public sealed class DrumTrigger : Processor, IKeyed
{
    /// <summary>The hit is measured for this long before the sample starts: the extra delay of a triggered drum.</summary>
    public const double ScanMs = 1.5;
    /// <summary>A new hit must jump this much above the sound still ringing from the last one (so a ringing drum never re-fires).</summary>
    public const double RiseDb = 4;
    /// <summary>Hits this far under the threshold are shown in the hit history as too soft.</summary>
    public const double NearDb = 6;
    /// <summary>A hit this far over the threshold counts as the hardest.</summary>
    public const double SpanDb = 30;
    /// <summary>At full dynamics, the softest hit plays this much quieter than the hardest.</summary>
    public const double DynamicsDb = 24;
    private const int Voices = 8;
    private const int FadeSamples = 64;
    private const int HistorySize = 64;

    private readonly float[] _key;
    private readonly Voice[] _voices = new Voice[Voices + 1];
    private readonly TriggerHit[] _history = new TriggerHit[HistorySize];
    private readonly int[] _takeTurn = new int[TriggerSettings.MaxLayers];
    private readonly float[] _learnPeaks = new float[LearnHits];
    private readonly long[] _learnTimes = new long[LearnHits];

    private TriggerSettings _s = new();
    private DrumSample[][] _layers = [];
    private Biquad _hp = Biquad.Identity, _lp = Biquad.Identity;
    private BiquadState _hpS, _lpS;
    private float _thr, _near, _gain, _blendS, _blendN, _gateTarget, _gate;
    private double _rate = 1, _gateRelease;
    private float _fast, _slow, _fastRel, _slowAtt, _slowRel, _rise;
    private int _scanLen, _holdLen, _gateLen;

    // Detection state.
    private int _scan = -1, _hold, _gateLeft;
    private float _peak;
    private long _clock;
    private uint _seq;
    private int _historyWrite;
    private int _hits;

    // Learn: 8 hits at a low threshold.
    public const int LearnHits = 8;
    private volatile bool _learning;
    private int _learnCount;
    private float _learnFloor;

    public DrumTrigger(double sampleRate, int maxFrames) : base(sampleRate, maxFrames)
    {
        _key = new float[maxFrames];
        for (var i = 0; i < _voices.Length; i++) _voices[i] = new Voice();
        Apply(new TriggerConfig { Settings = new TriggerSettings(), Layers = [] });
    }

    public float[]? KeyInput { get; set; }

    /// <summary>Hits that fired since the start (the hit light flashes when this goes up).</summary>
    public int Hits => Volatile.Read(ref _hits);

    /// <summary>The strength of the last hit that fired, 0 to 1.</summary>
    public float LastStrength { get; private set; }

    /// <summary>Something to play: at least one layer has a sample.</summary>
    public bool HasSamples => _layers.Length > 0;

    /// <summary>The newest hits, oldest first, with a number that goes up with each (to read only new ones).</summary>
    public int ReadHistory(Span<TriggerHit> dst, out uint seq)
    {
        seq = Volatile.Read(ref _seq);
        var n = (int)Math.Min(Math.Min(seq, (uint)HistorySize), (uint)dst.Length);
        var end = Volatile.Read(ref _historyWrite);
        for (var i = 0; i < n; i++) dst[i] = _history[(end - n + i + HistorySize) % HistorySize];
        return n;
    }

    // ---- Learn ---------------------------------------------------------------------------------

    /// <summary>Starts listening for eight hits to set the threshold and hold (any thread).</summary>
    public void StartLearn()
    {
        _learnCount = 0;
        _learning = true;
    }

    public bool Learning => _learning;
    public int LearnCount => Volatile.Read(ref _learnCount);

    /// <summary>After eight hits: a threshold 6 dB under the softest, and a hold of half the shortest gap (20 to 80 ms).</summary>
    public (double Threshold, double Hold)? LearnResult()
    {
        if (_learning || _learnCount < LearnHits) return null;
        return Learned(_learnPeaks, _learnTimes, SampleRate);
    }

    public static (double Threshold, double Hold) Learned(float[] peaks, long[] times, double sampleRate)
    {
        var softest = peaks.Min();
        var thr = Math.Clamp(Math.Round(Dsp.Lin2Db(softest) - NearDb), -60, 0);
        var gap = double.MaxValue;
        for (var i = 1; i < times.Length; i++) gap = Math.Min(gap, (times[i] - times[i - 1]) * 1000.0 / sampleRate);
        var hold = Math.Clamp(Math.Round(gap * 0.5), 20, 80);
        return (thr, hold);
    }

    // ---- Settings ------------------------------------------------------------------------------

    protected override void Apply(object settings)
    {
        if (settings is not TriggerConfig c) return;
        var s = c.Settings;
        _s = s;
        _layers = c.Layers;
        _hp = Biquad.Make(FilterShape.HighPass, s.FocusLow, 0.707, 0, SampleRate);
        _lp = Biquad.Make(FilterShape.LowPass, s.FocusHigh, 0.707, 0, SampleRate);
        _thr = Dsp.Db2Lin((float)s.Threshold);
        _near = Dsp.Db2Lin((float)(s.Threshold - NearDb));
        _gain = Dsp.Db2Lin((float)s.Level);
        _rate = Math.Pow(2, s.Pitch / 12.0);
        _scanLen = Math.Max(1, (int)(ScanMs * 0.001 * SampleRate));
        _holdLen = (int)(s.Hold * 0.001 * SampleRate);
        _gateLen = (int)(s.GateTime * 0.001 * SampleRate);
        _gateRelease = Dsp.Coef(20, SampleRate);
        _fastRel = (float)Dsp.Coef(20, SampleRate);
        _slowAtt = (float)Dsp.Coef(2, SampleRate);
        _slowRel = (float)Dsp.Coef(30, SampleRate);
        _rise = Dsp.Db2Lin((float)RiseDb);
        // Nothing to play (no samples, or one went missing): the natural mic carries on at full level.
        var playing = s.On && _layers.Length > 0 && !s.Compare;
        _blendS = playing ? (float)(s.Blend / 100) : 0;
        _blendN = playing ? 1 - _blendS : 1;
        _gateTarget = playing && s.NaturalGate && _blendN > 0 ? 0 : 1;
        if (!playing)
            foreach (var v in _voices) v.Fade = Math.Min(v.Fade < 0 ? FadeSamples : v.Fade, FadeSamples);
    }

    public override void Reset()
    {
        _hpS.Reset();
        _lpS.Reset();
        _fast = _slow = 0;
        _scan = -1;
        _hold = 0;
        foreach (var v in _voices) v.Sample = null;
    }

    // ---- Audio ---------------------------------------------------------------------------------

    protected override void Process(float[] left, float[]? right, int frames)
    {
        var s = _s;
        if (!s.On && !_learning)
        {
            // Let any sample still ringing finish fading, then nothing to do.
            if (!AnyVoice()) return;
        }
        var src = KeyInput ?? left;
        Array.Copy(src, _key, frames);
        var listen = s.On || _learning;
        var gateOn = _gateTarget < 1;

        for (var n = 0; n < frames; n++)
        {
            if (listen)
            {
                var y = _hpS.Tick(_hp, _key[n]);
                y = _lpS.Tick(_lp, y);
                var e = y < 0 ? -y : y;
                // A fast peak follower (the hit), and a slower one that rises over 2 ms and dies away over 30 ms (what may still be ringing):
                // a new hit is a sudden jump of the fast one above that.
                _fast = e > _fast ? e : _fast * _fastRel;
                var prevSlow = _slow;
                var held = _slow * _slowRel;
                _slow = _fast > held ? _fast + _slowAtt * (held - _fast) : held;
                Detect(_fast, prevSlow, s);
            }

            // The natural mic, gated between hits when asked.
            var nat = left[n];
            if (gateOn)
            {
                if (_gateLeft > 0)
                {
                    _gateLeft--;
                    _gate += (1 - _gate) * 0.25f; // opens in a few samples
                }
                else _gate = (float)(_gateRelease * _gate);
                nat *= _gate;
            }
            var mixed = nat * _blendN;

            // The samples.
            var sum = 0f;
            for (var i = 0; i < _voices.Length; i++)
            {
                var v = _voices[i];
                if (v.Sample is not { } smp) continue;
                var d = smp.Data;
                var p = v.Pos;
                var ip = (int)p;
                if (ip >= d.Length - 1 || v.Fade == 0)
                {
                    v.Sample = null;
                    continue;
                }
                var f = (float)(p - ip);
                var x = d[ip] + (d[ip + 1] - d[ip]) * f;
                var g = v.Gain;
                if (v.Fade > 0) g *= v.Fade-- / (float)FadeSamples;
                sum += x * g;
                v.Pos = p + _rate;
            }
            left[n] = mixed + sum * _blendS;
            _clock++;
        }
        if (right is not null) Array.Copy(left, right, frames);
    }

    private bool AnyVoice()
    {
        foreach (var v in _voices) if (v.Sample is not null) return true;
        return false;
    }

    private void Detect(float e, float ringing, TriggerSettings s)
    {
        if (_learning)
        {
            LearnDetect(e, ringing);
            return;
        }
        if (_hold > 0)
        {
            _hold--;
            return;
        }
        if (_scan < 0)
        {
            if (e < _near || e < ringing * _rise) return;
            _scan = 0;
            _peak = e;
            // The natural mic opens with the hit itself, not a millisecond late.
            if (e >= _thr) _gateLeft = _gateLen;
            return;
        }
        if (e > _peak) _peak = e;
        if (_peak >= _thr && _gateLeft == 0) _gateLeft = _gateLen;
        if (++_scan < _scanLen) return;
        _scan = -1;
        _hold = _holdLen;
        var db = Dsp.Lin2Db(_peak);
        if (_peak < _thr)
        {
            Record(HitKind.TooSoft, db, 0, 0);
            return;
        }
        var strength = (float)Math.Clamp((db - s.Threshold) / SpanDb, 0, 1);
        Fire(strength, db, s);
    }

    private void Fire(float strength, float db, TriggerSettings s)
    {
        LastStrength = strength;
        Interlocked.Increment(ref _hits);
        if (_layers.Length == 0)
        {
            Record(HitKind.Fired, db, 0, strength);
            return;
        }
        var layer = Math.Min(_layers.Length - 1, (int)(strength * _layers.Length));
        var takes = _layers[layer];
        var take = 0;
        if (takes.Length > 1)
        {
            take = s.RoundRobin ? _takeTurn[layer] % takes.Length : 0;
            _takeTurn[layer] = (take + 1) % takes.Length;
        }
        var gainDb = (strength - 1) * DynamicsDb * (s.Dynamics / 100);
        var gain = _gain * Dsp.Db2Lin((float)gainDb);
        Start(takes[take], gain);
        Record(HitKind.Fired, db, (byte)layer, strength);
    }

    private void Start(DrumSample smp, float gain)
    {
        Voice? free = null, oldest = null;
        var active = 0;
        foreach (var v in _voices)
        {
            if (v.Sample is null) { free ??= v; continue; }
            if (v.Fade < 0)
            {
                active++;
                if (oldest is null || v.Started < oldest.Started) oldest = v;
            }
        }
        // At most eight ringing at once: the oldest fades out quickly to make room.
        if (active >= Voices && oldest is not null) oldest.Fade = FadeSamples;
        if (free is null)
        {
            // Every slot busy (some still fading): take the quietest-to-lose, the oldest.
            free = oldest ?? _voices[0];
        }
        free.Sample = smp;
        free.Pos = 0;
        free.Gain = gain;
        free.Fade = -1;
        free.Started = _clock;
    }

    private void Record(HitKind kind, float db, byte layer, float strength)
    {
        var i = _historyWrite;
        _history[i] = new TriggerHit(_clock, kind, db, layer, strength);
        Volatile.Write(ref _historyWrite, (i + 1) % HistorySize);
        Volatile.Write(ref _seq, _seq + 1);
    }

    private void LearnDetect(float e, float ringing)
    {
        if (_learnCount == 0 && _scan < 0 && _hold == 0) _learnFloor = Dsp.Db2Lin(-50);
        if (_hold > 0)
        {
            _hold--;
            return;
        }
        if (_scan < 0)
        {
            if (e < _learnFloor || e < ringing * _rise) return;
            _scan = 0;
            _peak = e;
            return;
        }
        if (e > _peak) _peak = e;
        if (++_scan < _scanLen) return;
        _scan = -1;
        _hold = (int)(0.06 * SampleRate);
        var i = _learnCount;
        if (i < LearnHits)
        {
            _learnPeaks[i] = _peak;
            _learnTimes[i] = _clock;
            Volatile.Write(ref _learnCount, i + 1);
            Record(HitKind.Fired, Dsp.Lin2Db(_peak), 0, 1);
        }
        if (i + 1 >= LearnHits) _learning = false;
    }

    private sealed class Voice
    {
        public DrumSample? Sample;
        public double Pos;
        public float Gain;
        /// <summary>Samples left of a quick fade-out, or -1 while playing normally.</summary>
        public int Fade = -1;
        public long Started;
    }
}
