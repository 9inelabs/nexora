using System.Globalization;

namespace Nexora.Audio.Engine;

/// <summary>How much an event matters.</summary>
public enum EventLevel
{
    Info,
    /// <summary>Something to look at after the service (a dropout, a plugin restarted).</summary>
    Warning,
    /// <summary>Something that changed what the room heard (the driver stopped, the X32 link was lost).</summary>
    Problem,
}

public sealed record DeskEvent(DateTime Utc, EventLevel Level, string Text);

/// <summary>
/// What happened on the desk, newest last: driver starts and stops, dropouts, the X32 link, the failsafe, recalls.
/// The last 500 are kept in memory for the Diagnostics page, and every event goes to a file per day
/// (audio/events/2026-11-29.log), so a Sunday can be looked at again on Monday.
/// </summary>
public sealed class EventLog
{
    public const int Keep = 500;
    private readonly string? _dir;
    private readonly LinkedList<DeskEvent> _events = new();
    private readonly object _gate = new();
    private long _count;

    public EventLog(string? dir = null)
    {
        _dir = dir;
        if (dir is not null)
            try { Directory.CreateDirectory(dir); }
            catch (Exception) { }
    }

    public event Action<DeskEvent>? Added;

    /// <summary>How many events since the desk started (goes up even when old ones drop out of memory).</summary>
    public long Count => Interlocked.Read(ref _count);

    public void Add(string text, EventLevel level = EventLevel.Info, DateTime? utc = null)
    {
        var e = new DeskEvent(utc ?? DateTime.UtcNow, level, text);
        lock (_gate)
        {
            _events.AddLast(e);
            while (_events.Count > Keep) _events.RemoveFirst();
            Interlocked.Increment(ref _count);
            if (_dir is not null)
                try
                {
                    var local = e.Utc.ToLocalTime();
                    File.AppendAllText(Path.Combine(_dir, local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"),
                        $"{local:HH:mm:ss}  {Tag(level)}  {text}{Environment.NewLine}");
                }
                catch (Exception) { }
        }
        try { Added?.Invoke(e); } catch { }
    }

    /// <summary>The newest events, newest first.</summary>
    public IReadOnlyList<DeskEvent> Latest(int max = 100)
    {
        lock (_gate) return _events.Reverse().Take(max).ToList();
    }

    public int CountSince(DateTime utc, EventLevel atLeast)
    {
        lock (_gate) return _events.Count(e => e.Utc >= utc && e.Level >= atLeast);
    }

    public static string Tag(EventLevel l) => l switch { EventLevel.Problem => "PROBLEM", EventLevel.Warning => "WARNING", _ => "info   " };

    /// <summary>The day's log files, newest first.</summary>
    public IReadOnlyList<string> Files() =>
        _dir is not null && Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.log").OrderByDescending(f => f).ToList() : [];
}

/// <summary>Counts what goes wrong with the sound over time, for the Diagnostics page.</summary>
public sealed class DropoutWatch
{
    private readonly Queue<(DateTime At, long Late, long Driver)> _samples = new();
    private long _lastLate, _lastDriver;
    private DateTime _lastReport = DateTime.MinValue;
    private long _pending;

    /// <summary>
    /// Takes the running totals (they start again from 0 when the driver is opened again). Returns a line for the event log
    /// when there were new dropouts, at most once every 10 seconds, adding up the ones in between.
    /// </summary>
    public string? Update(long late, long driver, DateTime now)
    {
        lock (_samples) return UpdateLocked(late, driver, now);
    }

    private string? UpdateLocked(long late, long driver, DateTime now)
    {
        if (late < _lastLate || driver < _lastDriver) _lastLate = _lastDriver = 0;
        var newLate = late - _lastLate;
        var newDriver = driver - _lastDriver;
        _lastLate = late;
        _lastDriver = driver;
        _samples.Enqueue((now, newLate, newDriver));
        while (_samples.Count > 0 && now - _samples.Peek().At > TimeSpan.FromMinutes(10)) _samples.Dequeue();
        _pending += newLate + newDriver;
        if (_pending == 0 || now - _lastReport < TimeSpan.FromSeconds(10)) return null;
        var n = _pending;
        _pending = 0;
        _lastReport = now;
        return n == 1 ? "1 dropout (a buffer was late or the driver lost sync)." : $"{n} dropouts (buffers late or the driver lost sync).";
    }

    /// <summary>Dropouts in the last 10 minutes.</summary>
    public long Recent
    {
        get { lock (_samples) return _samples.Sum(s => s.Late + s.Driver); }
    }

    public void Clear()
    {
        lock (_samples)
        {
            _samples.Clear();
            _pending = 0;
        }
    }
}

/// <summary>Measures the sample rate the driver really runs at, from the audio clock and the PC's clock.</summary>
public sealed class ClockWatch
{
    private long _startFrames = -1;
    private DateTime _startAt;

    /// <summary>The measured rate (Hz) over the last ten seconds or more, or 0 while it is still measuring.</summary>
    public double Rate { get; private set; }

    public void Update(long frames, DateTime now)
    {
        if (_startFrames < 0 || frames < _startFrames)
        {
            _startFrames = frames;
            _startAt = now;
            Rate = 0;
            return;
        }
        var secs = (now - _startAt).TotalSeconds;
        if (secs < 10) return;
        Rate = (frames - _startFrames) / secs;
        _startFrames = frames;
        _startAt = now;
    }

    public void Reset() => _startFrames = -1;

    /// <summary>How far the measured rate is from the nominal one, in parts per million (0 while measuring).</summary>
    public double Ppm(double nominal) => Rate > 0 && nominal > 0 ? (Rate - nominal) / nominal * 1e6 : 0;
}
