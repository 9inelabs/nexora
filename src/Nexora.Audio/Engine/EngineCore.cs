using System.Diagnostics;

namespace Nexora.Audio.Engine;

/// <summary>
/// The part of NEX-Audio that touches every sample. The driver fills <see cref="In"/>, calls
/// <see cref="Process"/>, then sends <see cref="Out"/> back to the X32.
/// Rule for everything in here: no allocations, no locks and no waiting while audio runs,
/// so nothing can make the sound stutter. Settings arrive by swapping whole arrays.
/// </summary>
public sealed class EngineCore
{
    /// <summary>A patch value: this output is silent.</summary>
    public const int Off = -1;
    /// <summary>A patch value: this output plays a steady 1 kHz test tone at -18 dBFS.</summary>
    public const int Tone = -2;

    private const float ToneLevel = 0.125893f; // -18 dBFS
    private const float ClipLevel = 0.989f;    // about -0.1 dBFS

    private int[] _patch;
    private readonly float[] _inPeak, _outPeak;
    private readonly int[] _inClip, _outClip;
    private double _tonePhase;
    private long _clock;

    // Latency test, run on the audio thread, read by the control thread.
    private volatile int _ltState; // 0 idle, 1 measuring noise, 2 waiting to fire, 3 listening
    private int _ltOut, _ltIn, _ltRound;
    private long _ltFiredAt, _ltPhaseStart;
    private float _ltNoise;
    private readonly long[] _ltResults = new long[LatencyRounds];
    private volatile int _ltDone;

    public const int LatencyRounds = 5;

    public EngineCore(int inputs, int outputs, int maxFrames, double sampleRate)
    {
        Inputs = inputs;
        Outputs = outputs;
        MaxFrames = maxFrames;
        SampleRate = sampleRate;
        In = new float[inputs][];
        Out = new float[outputs][];
        for (var i = 0; i < inputs; i++) In[i] = new float[maxFrames];
        for (var i = 0; i < outputs; i++) Out[i] = new float[maxFrames];
        _inPeak = new float[inputs];
        _outPeak = new float[outputs];
        _inClip = new int[inputs];
        _outClip = new int[outputs];
        _patch = StraightThrough(inputs, outputs);
    }

    public int Inputs { get; }
    public int Outputs { get; }
    public int MaxFrames { get; }
    public double SampleRate { get; }
    public float[][] In { get; }
    public float[][] Out { get; }

    /// <summary>Samples processed since the engine started.</summary>
    public long Clock => Interlocked.Read(ref _clock);

    /// <summary>Every output plays the input with the same number (output 1 = input 1, and so on).</summary>
    public static int[] StraightThrough(int inputs, int outputs)
    {
        var p = new int[outputs];
        for (var o = 0; o < outputs; o++) p[o] = o < inputs ? o : Off;
        return p;
    }

    /// <summary>What each output plays: an input number from 0, <see cref="Off"/> or <see cref="Tone"/>.</summary>
    public int[] Patch => (int[])Volatile.Read(ref _patch).Clone();

    public void SetPatch(IReadOnlyList<int> patch)
    {
        var p = new int[Outputs];
        for (var o = 0; o < Outputs; o++)
        {
            var v = o < patch.Count ? patch[o] : Off;
            p[o] = v == Tone || (v >= 0 && v < Inputs) ? v : Off;
        }
        Volatile.Write(ref _patch, p);
    }

    /// <summary>Runs once per driver buffer, on the audio thread.</summary>
    public void Process(int frames)
    {
        if (frames > MaxFrames) frames = MaxFrames;
        var patch = Volatile.Read(ref _patch);

        for (var i = 0; i < Inputs; i++)
        {
            var peak = Peak(In[i], frames);
            if (peak > _inPeak[i]) _inPeak[i] = peak;
            if (peak >= ClipLevel) _inClip[i] = 1;
        }

        var toneUsed = false;
        var phaseStep = 2 * Math.PI * 1000.0 / SampleRate;
        for (var o = 0; o < Outputs; o++)
        {
            var dst = Out[o];
            var src = patch[o];
            if (src >= 0)
                Array.Copy(In[src], dst, frames);
            else if (src == Tone)
            {
                var ph = _tonePhase;
                for (var n = 0; n < frames; n++) dst[n] = ToneLevel * (float)Math.Sin(ph + n * phaseStep);
                toneUsed = true;
            }
            else
                Array.Clear(dst, 0, frames);
        }
        if (toneUsed) _tonePhase = (_tonePhase + frames * phaseStep) % (2 * Math.PI);

        if (_ltState != 0) LatencyStep(frames);

        for (var o = 0; o < Outputs; o++)
        {
            var peak = Peak(Out[o], frames);
            if (peak > _outPeak[o]) _outPeak[o] = peak;
            if (peak >= ClipLevel) _outClip[o] = 1;
        }

        Interlocked.Add(ref _clock, frames);
    }

    private static float Peak(float[] buf, int frames)
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

    /// <summary>
    /// Copies the loudest level on each channel since the last call (0 to 1) and starts a new period.
    /// Clip flags stay set until <see cref="ResetClips"/>.
    /// </summary>
    public void TakeMeters(float[] inPeaks, float[] outPeaks, bool[] inClips, bool[] outClips)
    {
        for (var i = 0; i < Inputs && i < inPeaks.Length; i++)
        {
            inPeaks[i] = _inPeak[i];
            _inPeak[i] = 0;
            inClips[i] = _inClip[i] != 0;
        }
        for (var o = 0; o < Outputs && o < outPeaks.Length; o++)
        {
            outPeaks[o] = _outPeak[o];
            _outPeak[o] = 0;
            outClips[o] = _outClip[o] != 0;
        }
    }

    public void ResetClips()
    {
        Array.Clear(_inClip);
        Array.Clear(_outClip);
    }

    // ---- Round-trip latency test ----------------------------------------------------------
    // NEX-Audio sends a short click out of one output and listens for it on one input.
    // With a cable from that X32 output into that X32 input, the time between the two is the
    // full round trip: X32 converters, USB both ways, the driver and NEX-Audio.

    public bool LatencyTestRunning => _ltState != 0;

    public void StartLatencyTest(int output, int input)
    {
        if (output < 0 || output >= Outputs || input < 0 || input >= Inputs) throw new ArgumentOutOfRangeException(nameof(output));
        _ltOut = output;
        _ltIn = input;
        _ltRound = 0;
        _ltNoise = 0;
        _ltDone = 0;
        Array.Clear(_ltResults);
        _ltPhaseStart = Clock;
        _ltState = 1;
    }

    public void CancelLatencyTest() => _ltState = 0;

    /// <summary>The measured round trips in samples (-1 = the click never came back), once the test has finished.</summary>
    public long[]? LatencyResults => _ltDone == 1 ? (long[])_ltResults.Clone() : null;

    private void LatencyStep(int frames)
    {
        var start = _clock;
        var outBuf = Out[_ltOut];
        var inBuf = In[_ltIn];
        var sr = (long)SampleRate;
        // The test output carries only the clicks while the test runs.
        Array.Clear(outBuf, 0, frames);

        switch (_ltState)
        {
            case 1: // 0.3 s of listening to learn the noise level on the input
                _ltNoise = Math.Max(_ltNoise, Peak(inBuf, frames));
                if (start + frames - _ltPhaseStart >= sr * 3 / 10)
                {
                    _ltState = 2;
                    _ltPhaseStart = start + frames;
                }
                break;
            case 2: // fire a click at the start of this buffer
                for (var n = 0; n < 6 && n < frames; n++) outBuf[n] = 0.7f;
                _ltFiredAt = start;
                _ltState = 3;
                break;
            case 3:
                var threshold = Math.Max(0.05f, _ltNoise * 4f);
                for (var n = 0; n < frames; n++)
                {
                    var v = inBuf[n];
                    if (v < 0) v = -v;
                    if (v >= threshold && start + n > _ltFiredAt)
                    {
                        NextRound(start + n - _ltFiredAt, start + frames);
                        return;
                    }
                }
                if (start + frames - _ltFiredAt > sr) NextRound(-1, start + frames); // nothing within a second
                break;
        }
    }

    private void NextRound(long result, long now)
    {
        _ltResults[_ltRound] = result;
        _ltRound++;
        if (_ltRound >= LatencyRounds)
        {
            _ltDone = 1;
            _ltState = 0;
            return;
        }
        // Listen again for 0.3 s (the last click dies away), then fire the next one.
        _ltPhaseStart = now;
        _ltNoise = 0;
        _ltState = 1; // listen to the input again before each click
    }

    /// <summary>The middle value of the rounds that came back, or null when none did.</summary>
    public static long? Median(IEnumerable<long> rounds)
    {
        var ok = rounds.Where(r => r >= 0).OrderBy(r => r).ToArray();
        return ok.Length == 0 ? null : ok[ok.Length / 2];
    }

    /// <summary>Time in whole ticks for load measuring.</summary>
    public static long Now() => Stopwatch.GetTimestamp();
}
