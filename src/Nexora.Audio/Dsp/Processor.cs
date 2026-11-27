namespace Nexora.Audio.Dsp;

/// <summary>
/// Something that changes sound on the audio thread: an EQ, a compressor, a reverb, a plugin.
/// Settings are handed over from the control thread with <see cref="Configure"/>; the audio thread picks
/// them up at the start of its next buffer, so nothing ever waits.
/// </summary>
public abstract class Processor
{
    private object? _pending;

    protected Processor(double sampleRate, int maxFrames)
    {
        SampleRate = sampleRate;
        MaxFrames = maxFrames;
    }

    public double SampleRate { get; }
    public int MaxFrames { get; }

    /// <summary>The delay this processor adds, in samples, so the rest of the mix can be lined up with it.</summary>
    public virtual int Latency => 0;

    /// <summary>Gain reduction in dB (positive) for the meter, where it means something.</summary>
    public float Reduction { get; protected set; }

    /// <summary>Hands new settings to the audio thread (any thread may call this).</summary>
    public void Configure(object settings) => Volatile.Write(ref _pending, settings);

    /// <summary>Processes one buffer in place. <paramref name="right"/> is null for mono strips.</summary>
    public void Run(float[] left, float[]? right, int frames)
    {
        Update();
        Process(left, right, frames);
    }

    /// <summary>Takes any settings waiting from the control thread (on the audio thread).</summary>
    protected void Update()
    {
        var p = Interlocked.Exchange(ref _pending, null);
        if (p is not null) Apply(p);
    }

    /// <summary>Takes new settings, on the audio thread.</summary>
    protected abstract void Apply(object settings);

    protected abstract void Process(float[] left, float[]? right, int frames);

    public virtual void Reset() { }
}

/// <summary>A fixed delay in whole samples, for lining up paths and speakers. No allocation after creation.</summary>
public sealed class DelayLine
{
    private readonly float[] _buf;
    private readonly int _mask;
    private int _write;

    public DelayLine(int maxDelay)
    {
        var size = 1;
        while (size < maxDelay + 8192) size <<= 1;
        _buf = new float[size];
        _mask = size - 1;
    }

    public int MaxDelay => _buf.Length - 8192;

    /// <summary>Delays the buffer in place by this many samples.</summary>
    public void Process(float[] buf, int frames, int delay)
    {
        delay = Math.Clamp(delay, 0, MaxDelay);
        if (delay == 0)
        {
            // Keep the memory current so a later change of delay does not replay old sound.
            for (var n = 0; n < frames; n++) _buf[(_write + n) & _mask] = buf[n];
            _write = (_write + frames) & _mask;
            return;
        }
        for (var n = 0; n < frames; n++)
        {
            _buf[_write] = buf[n];
            buf[n] = _buf[(_write - delay) & _mask];
            _write = (_write + 1) & _mask;
        }
    }

    public void Write(float x)
    {
        _buf[_write] = x;
        _write = (_write + 1) & _mask;
    }

    /// <summary>The sample written <paramref name="delay"/> samples ago (1 is the last one written).</summary>
    public float Read(int delay) => _buf[(_write - delay) & _mask];

    /// <summary>Reads between samples (linear), for chorus and pitch.</summary>
    public float ReadFrac(double delay)
    {
        var i = (int)delay;
        var f = (float)(delay - i);
        var a = _buf[(_write - i) & _mask];
        var b = _buf[(_write - i - 1) & _mask];
        return a + (b - a) * f;
    }

    /// <summary>Cubic (Hermite) interpolation for smoother modulated delays.</summary>
    public float ReadCubic(double delay)
    {
        var i = (int)delay;
        var f = (float)(delay - i);
        var xm1 = _buf[(_write - i + 1) & _mask];
        var x0 = _buf[(_write - i) & _mask];
        var x1 = _buf[(_write - i - 1) & _mask];
        var x2 = _buf[(_write - i - 2) & _mask];
        var c = (x1 - xm1) * 0.5f;
        var v = x0 - x1;
        var w = c + v;
        var a = w + v + (x2 - x0) * 0.5f;
        var b = w + a;
        return ((a * f - b) * f + c) * f + x0;
    }

    public void Clear() => Array.Clear(_buf);
}

/// <summary>A gain that glides to its target over one buffer, so moves never click.</summary>
public struct SmoothGain
{
    public float Current;

    public void Apply(float[] buf, int frames, float target)
    {
        var start = Current;
        if (start == target)
        {
            if (target == 1f) return;
            if (target == 0f)
            {
                Array.Clear(buf, 0, frames);
                return;
            }
            for (var n = 0; n < frames; n++) buf[n] *= target;
            return;
        }
        var step = (target - start) / frames;
        var g = start;
        for (var n = 0; n < frames; n++)
        {
            g += step;
            buf[n] *= g;
        }
        Current = target;
    }

    /// <summary>Adds src times the gliding gain into dst (for sends and mixing).</summary>
    public void Mix(float[] src, float[] dst, int frames, float target)
    {
        var start = Current;
        Current = target;
        if (start == 0f && target == 0f) return;
        if (start == target)
        {
            for (var n = 0; n < frames; n++) dst[n] += src[n] * target;
            return;
        }
        var step = (target - start) / frames;
        var g = start;
        for (var n = 0; n < frames; n++)
        {
            g += step;
            dst[n] += src[n] * g;
        }
    }
}

public static class Dsp
{
    public static float Peak(float[] buf, int frames)
    {
        var m = 0f;
        for (var n = 0; n < frames; n++)
        {
            var v = buf[n];
            if (v < 0) v = -v;
            if (v > m) m = v;
        }
        return m;
    }

    /// <summary>A one-pole smoothing coefficient for this time constant in milliseconds.</summary>
    public static double Coef(double ms, double sampleRate) =>
        ms <= 0 ? 0 : Math.Exp(-1.0 / (ms * 0.001 * sampleRate));

    public static float Lin2Db(float x) => x <= 1e-9f ? -180f : 20f * MathF.Log10(x);
    public static float Db2Lin(float db) => MathF.Pow(10f, db / 20f);
}
