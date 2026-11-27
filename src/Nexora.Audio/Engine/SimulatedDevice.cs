using System.Diagnostics;

namespace Nexora.Audio.Engine;

/// <summary>
/// A pretend X32 for trying NEX-Audio without the console: 32 inputs with steady test sounds,
/// and output 32 looped back into input 32 after a realistic delay, so the latency test works too.
/// </summary>
public sealed class SimulatedDevice : IAudioDevice
{
    public const string DeviceName = "Test device (no X32)";
    public const int Channels = 32;
    /// <summary>The pretend round trip: two buffers plus converters and USB (211 samples, 4.4 ms, at a 64 buffer).</summary>
    public static int LoopDelayFor(int buffer) => 2 * buffer + 83;

    private Thread? _thread;
    private volatile bool _run;
    private int _buffer, _delay;
    private readonly float[] _loop = new float[8192];
    private int _loopWrite;
    private long _t;
    private double _rate = 48000;
    public static readonly IReadOnlyList<double> Rates = [44100, 48000, 88200, 96000];

    public string Name => DeviceName;
    public DeviceFormat? Format { get; private set; }
    public bool HasSettingsPanel => false;
    public bool ResetRequested => false;
    public void ShowSettingsPanel() { }

    public void Open(int bufferSize, double sampleRate = 0)
    {
        _buffer = bufferSize is 32 or 64 or 128 or 256 or 512 ? bufferSize : 64;
        _delay = LoopDelayFor(_buffer);
        _rate = Rates.FirstOrDefault(r => Math.Abs(r - sampleRate) < 1, 48000);
        Format = new DeviceFormat(Channels, Channels, _rate, _buffer, _buffer + 40, _buffer + 43, "32-bit (test)",
            [32, 64, 128, 256, 512],
            Enumerable.Range(1, Channels).Select(i => $"Test In {i}").ToList(),
            Enumerable.Range(1, Channels).Select(i => $"Test Out {i}").ToList()) { SampleRates = Rates };
    }

    public void Start(EngineCore core, AudioStats stats)
    {
        if (_thread is not null) return;
        _run = true;
        _thread = new Thread(() => Run(core, stats)) { IsBackground = true, Name = "NEX-Audio test device", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run(EngineCore core, AudioStats stats)
    {
        var frames = _buffer;
        var seconds = frames / _rate;
        var period = (long)(seconds * Stopwatch.Frequency);
        var next = Stopwatch.GetTimestamp();
        while (_run)
        {
            var begin = Stopwatch.GetTimestamp();
            Fill(core, frames);
            core.Process(frames);
            Capture(core, frames);
            stats.Record(begin, Stopwatch.GetTimestamp(), seconds);

            next += period;
            var wait = next - Stopwatch.GetTimestamp();
            if (wait < -period * 8) next = Stopwatch.GetTimestamp(); // fell far behind: start timing again
            else if (wait > 0)
            {
                var ms = (int)(wait * 1000 / Stopwatch.Frequency);
                if (ms > 1) Thread.Sleep(ms - 1);
                while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(50);
            }
        }
    }

    // Inputs 1 to 24 play quiet tones at different pitches and levels, 25 to 31 are silent
    // (like unused channels) and 32 hears output 32 after the loop delay.
    private void Fill(EngineCore core, int frames)
    {
        for (var ch = 0; ch < Channels - 1; ch++)
        {
            var buf = core.In[ch];
            if (ch >= 24)
            {
                Array.Clear(buf, 0, frames);
                continue;
            }
            var freq = 110.0 * (1 + ch % 8);
            var level = (float)Math.Pow(10, (-30 + (ch % 6) * 4) / 20.0);
            var swell = 0.6 + 0.4 * Math.Sin(2 * Math.PI * 0.2 * (_t / _rate) + ch);
            for (var n = 0; n < frames; n++)
                buf[n] = (float)(level * swell * Math.Sin(2 * Math.PI * freq * (_t + n) / _rate));
        }
        var loopIn = core.In[Channels - 1];
        for (var n = 0; n < frames; n++)
        {
            var idx = (_loopWrite + n - _delay) & (_loop.Length - 1);
            loopIn[n] = _loop[idx];
        }
        _t += frames;
    }

    private void Capture(EngineCore core, int frames)
    {
        var src = core.Out[Channels - 1];
        for (var n = 0; n < frames; n++) _loop[(_loopWrite + n) & (_loop.Length - 1)] = src[n];
        _loopWrite = (_loopWrite + frames) & (_loop.Length - 1);
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(1000);
        _thread = null;
    }

    public void Close()
    {
        Stop();
        Format = null;
    }

    public void Dispose() => Close();
}
