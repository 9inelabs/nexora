using System.Diagnostics;

namespace Nexora.Audio.Engine;

/// <summary>What the driver gave NEX-Audio when it opened.</summary>
public sealed record DeviceFormat(
    int Inputs,
    int Outputs,
    double SampleRate,
    int BufferSize,
    int InputLatency,
    int OutputLatency,
    string SampleType,
    IReadOnlyList<int> BufferSizes,
    IReadOnlyList<string> InputNames,
    IReadOnlyList<string> OutputNames);

/// <summary>A sound driver NEX-Audio can run on: the X32's ASIO driver, or the test device.</summary>
public interface IAudioDevice : IDisposable
{
    string Name { get; }
    DeviceFormat? Format { get; }

    /// <summary>Opens the driver. <paramref name="bufferSize"/> 0 means the size set in the driver's own settings.</summary>
    void Open(int bufferSize);

    void Start(EngineCore core, AudioStats stats);
    void Stop();
    void Close();

    bool HasSettingsPanel { get; }
    void ShowSettingsPanel();

    /// <summary>The driver asked to be opened again (for example its buffer size changed in its own settings).</summary>
    bool ResetRequested { get; }
}

/// <summary>
/// Health of the audio thread. Written on the audio thread without locks, read by the control thread.
/// </summary>
public sealed class AudioStats
{
    private long _callbacks, _late, _overloads, _resyncs;
    private long _lastStart, _sumTicks, _sumCount;
    private double _peakLoad;

    public long Callbacks => Interlocked.Read(ref _callbacks);
    /// <summary>Buffers that took longer to process than the time the buffer lasts.</summary>
    public long LateBuffers => Interlocked.Read(ref _late);
    /// <summary>Times the driver itself reported an overload or lost sync.</summary>
    public long DriverProblems => Interlocked.Read(ref _overloads) + Interlocked.Read(ref _resyncs);

    public void Reset()
    {
        Interlocked.Exchange(ref _callbacks, 0);
        Interlocked.Exchange(ref _late, 0);
        Interlocked.Exchange(ref _overloads, 0);
        Interlocked.Exchange(ref _resyncs, 0);
        Interlocked.Exchange(ref _sumTicks, 0);
        Interlocked.Exchange(ref _sumCount, 0);
        _peakLoad = 0;
        _lastStart = 0;
    }

    public void Overload() => Interlocked.Increment(ref _overloads);
    public void Resync() => Interlocked.Increment(ref _resyncs);

    /// <summary>Call with the time processing started and ended, and how long the buffer lasts.</summary>
    public void Record(long startTicks, long endTicks, double bufferSeconds)
    {
        Interlocked.Increment(ref _callbacks);
        var used = (endTicks - startTicks) / (double)Stopwatch.Frequency;
        var load = used / bufferSeconds;
        if (load > _peakLoad) _peakLoad = load;
        if (load > 1.0) Interlocked.Increment(ref _late);
        Interlocked.Add(ref _sumTicks, endTicks - startTicks);
        Interlocked.Increment(ref _sumCount);
        _lastStart = startTicks;
    }

    /// <summary>Average and highest share of each buffer's time spent processing since the last call (0 to 1+).</summary>
    public (double Average, double Peak) TakeLoad(double bufferSeconds)
    {
        var ticks = Interlocked.Exchange(ref _sumTicks, 0);
        var count = Interlocked.Exchange(ref _sumCount, 0);
        var peak = _peakLoad;
        _peakLoad = 0;
        var avg = count == 0 ? 0 : ticks / (double)count / Stopwatch.Frequency / bufferSeconds;
        return (avg, peak);
    }

    /// <summary>True when buffers have been arriving recently.</summary>
    public bool Flowing(double withinSeconds)
    {
        var last = _lastStart;
        return last != 0 && (Stopwatch.GetTimestamp() - last) / (double)Stopwatch.Frequency < withinSeconds;
    }
}
