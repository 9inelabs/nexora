using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace Nexora.Audio.Plugins;

/// <summary>A running plugin host, however it was started.</summary>
public interface IPluginHostConnection : IDisposable
{
    void Send(string line);
    event Action<string>? Line;
    bool HasExited { get; }
    void Kill();
}

/// <summary>Starts plugin hosts.</summary>
public interface IPluginHostLauncher
{
    IPluginHostConnection Start(string shmPath, string eventName, int block, double sampleRate);
}

/// <summary>Starts each plugin host as its own process ("NexAudio.exe --plugin-host ...").</summary>
public sealed class ProcessHostLauncher : IPluginHostLauncher
{
    private readonly string _exe;
    public ProcessHostLauncher(string exe) => _exe = exe;

    public IPluginHostConnection Start(string shmPath, string eventName, int block, double sampleRate)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "--plugin-host", shmPath, eventName, block.ToString(), sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the plugin host.");
        try { p.PriorityClass = ProcessPriorityClass.High; } catch { }
        return new Conn(p);
    }

    private sealed class Conn : IPluginHostConnection
    {
        private readonly Process _p;
        public Conn(Process p)
        {
            _p = p;
            _p.OutputDataReceived += (_, e) => { if (e.Data != null) Line?.Invoke(e.Data); };
            _p.BeginOutputReadLine();
        }
        public event Action<string>? Line;
        public bool HasExited { get { try { return _p.HasExited; } catch { return true; } } }
        public void Send(string line)
        {
            try { _p.StandardInput.WriteLine(line); _p.StandardInput.Flush(); } catch { }
        }
        public void Kill() { try { _p.Kill(true); } catch { } }
        public void Dispose()
        {
            Send("{\"cmd\":\"quit\"}");
            if (!_p.WaitForExit(1500)) Kill();
            _p.Dispose();
        }
    }
}

/// <summary>Runs plugin hosts on threads inside this process (for tests and for PCs that cannot start processes).</summary>
public sealed class InProcessHostLauncher : IPluginHostLauncher
{
    public IPluginHostConnection Start(string shmPath, string eventName, int block, double sampleRate) => new Conn(shmPath, eventName, block, sampleRate);

    private sealed class Conn : IPluginHostConnection
    {
        private readonly BlockingCollection<string?> _in = new();
        private volatile bool _exited;
        public Conn(string shm, string ev, int block, double sr)
        {
            var reader = new QueueReader(_in);
            var writer = new LineWriter(l => Line?.Invoke(l));
            new Thread(() =>
            {
                try { PluginHost.Run(shm, ev, block, sr, reader, writer, () => _exited = true); }
                catch { }
                _exited = true;
            }) { IsBackground = true, Name = "Plugin host (in process)" }.Start();
        }
        public event Action<string>? Line;
        public bool HasExited => _exited;
        public void Send(string line) { if (!_in.IsAddingCompleted) _in.Add(line); }
        public void Kill() { if (!_in.IsAddingCompleted) _in.CompleteAdding(); }
        public void Dispose()
        {
            Send("{\"cmd\":\"quit\"}");
            Kill();
        }
    }

    private sealed class QueueReader(BlockingCollection<string?> q) : TextReader
    {
        public override string? ReadLine()
        {
            try { return q.Take(); } catch { return null; }
        }
    }

    private sealed class LineWriter(Action<string> onLine) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) { if (value != null) onLine(value); }
    }
}

/// <summary>
/// A plugin in a NEX-Audio rack. The plugin itself runs in its own host process; this hands it sound
/// through shared memory without ever waiting. The price is a fixed delay of two blocks, which the mix
/// lines up automatically. If the host crashes, the sound passes through unchanged and the host is restarted.
/// </summary>
public sealed class PluginProcessor : Processor, IDisposable
{
    private static int _counter;
    private readonly IPluginHostLauncher _launcher;
    private readonly string _shmPath;
    private readonly string _eventName;
    private readonly PluginShm _shm;
    private readonly PluginSignal _signal;
    private readonly Action<string>? _log;
    private readonly int _block;
    private readonly float[] _inL, _inR, _outL, _outR, _prevL, _prevR;
    private int _pos, _seq, _lastPosted = -1;
    private IPluginHostConnection? _host;
    private PluginRef _plugin;
    private volatile PluginRef? _wanted;
    private readonly List<DateTime> _restarts = new();
    private int _lastBeat;
    private DateTime _beatSeen = DateTime.UtcNow;
    private int _stateId;
    private readonly Dictionary<int, TaskCompletionSource<(string?, string?)>> _stateWaits = new();

    public PluginProcessor(RackItem item, double sampleRate, int block, string workDir, IPluginHostLauncher launcher, Action<string>? log = null)
        : base(sampleRate, block)
    {
        _plugin = item.Plugin ?? throw new ArgumentException("No plugin was chosen.");
        _launcher = launcher;
        _log = log;
        _block = block;
        var n = Interlocked.Increment(ref _counter);
        _shmPath = Path.Combine(workDir, "plugins-live", $"{Environment.ProcessId}-{n}.shm");
        _eventName = OperatingSystem.IsWindows() ? $"Local\\nexora-plugin-{Environment.ProcessId}-{n}" : "-";
        _shm = new PluginShm(_shmPath, block, true);
        _signal = new PluginSignal(_eventName);
        _inL = new float[block]; _inR = new float[block];
        _outL = new float[block]; _outR = new float[block];
        _prevL = new float[block]; _prevR = new float[block];
        StartHost();
    }

    /// <summary>"starting", "running", "restarting" or "failed".</summary>
    public string Status { get; private set; } = "starting";
    public string? Message { get; private set; }
    public string Name => _plugin.Name;
    public bool HasEditor { get; private set; }
    public int PluginLatency { get; private set; }
    /// <summary>Blocks the host did not finish in time (the sound went through unchanged).</summary>
    public int Misses;

    public override int Latency => 2 * _block + PluginLatency;

    /// <summary>Raised (on a background thread) when the delay changes, so the mix can line up again.</summary>
    public event Action? LatencyChanged;
    /// <summary>Raised when someone changes a knob in the plugin's own window.</summary>
    public event Action? Edited;
    /// <summary>Raised when the plugin's status changes.</summary>
    public event Action? StatusChanged;

    private void StartHost()
    {
        try
        {
            _host?.Kill();
            _host?.Dispose();
        }
        catch { }
        Status = _restarts.Count == 0 ? "starting" : "restarting";
        var host = _launcher.Start(_shmPath, _eventName, _block, SampleRate);
        _host = host;
        host.Line += l => OnLine(host, l);
        var load = new JsonObject
        {
            ["cmd"] = "load", ["path"] = _plugin.Path, ["classId"] = _plugin.ClassId, ["state"] = _plugin.State, ["ctrl"] = _plugin.ControllerState,
        };
        host.Send(load.ToJsonString());
        _beatSeen = DateTime.UtcNow;
        StatusChanged?.Invoke();
    }

    private void OnLine(IPluginHostConnection from, string line)
    {
        if (from != _host) return;
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; } catch { return; }
        if (o is null) return;
        switch ((string?)o["ev"])
        {
            case "ready":
                HasEditor = (bool?)o["editor"] ?? false;
                SetLatency((int?)o["latency"] ?? 0);
                Status = "running";
                Message = null;
                StatusChanged?.Invoke();
                break;
            case "latency":
                SetLatency((int?)o["latency"] ?? 0);
                break;
            case "edited":
                Edited?.Invoke();
                break;
            case "state":
                var id = (int?)o["id"] ?? 0;
                TaskCompletionSource<(string?, string?)>? tcs;
                lock (_stateWaits)
                    if (_stateWaits.Remove(id, out tcs)) { }
                tcs?.TrySetResult(((string?)o["c"], (string?)o["k"]));
                break;
            case "error":
                Message = (string?)o["message"];
                _log?.Invoke($"Plugin {_plugin.Name}: {Message}");
                if ((bool?)o["fatal"] == true) Status = "restarting";
                StatusChanged?.Invoke();
                break;
        }
    }

    private void SetLatency(int samples)
    {
        if (samples == PluginLatency) return;
        PluginLatency = Math.Max(0, samples);
        LatencyChanged?.Invoke();
    }

    /// <summary>Checks on the host and restarts it if it has crashed (control thread, a few times a second).</summary>
    public void Service()
    {
        var want = _wanted;
        if (want != null)
        {
            _wanted = null;
            if (want.Path != _plugin.Path || want.ClassId != _plugin.ClassId)
            {
                _plugin = want;
                _restarts.Clear();
                PluginLatency = 0;
                StartHost();
                LatencyChanged?.Invoke();
                return;
            }
            _plugin = want with { State = _plugin.State, ControllerState = _plugin.ControllerState };
        }
        if (Status == "failed" || _host is null) return;

        var beat = _shm.Beat;
        if (beat != _lastBeat)
        {
            _lastBeat = beat;
            _beatSeen = DateTime.UtcNow;
        }
        var stuck = Status == "running" && DateTime.UtcNow - _beatSeen > TimeSpan.FromSeconds(3);
        if (!_host.HasExited && !stuck && Status != "restarting") return;
        if (Status == "restarting" && !_host.HasExited && !stuck && DateTime.UtcNow - _beatSeen < TimeSpan.FromSeconds(5)) return;

        _restarts.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(1));
        if (_restarts.Count >= 3)
        {
            _host.Kill();
            Status = "failed";
            Message ??= "The plugin keeps stopping, so it is switched off. The sound goes through without it.";
            _log?.Invoke($"Plugin {_plugin.Name} failed three times in a minute; left off.");
            StatusChanged?.Invoke();
            return;
        }
        _restarts.Add(DateTime.UtcNow);
        _log?.Invoke($"Plugin {_plugin.Name} stopped; starting it again.");
        StartHost();
    }

    /// <summary>Tries again after the plugin was switched off for failing.</summary>
    public void Retry()
    {
        _restarts.Clear();
        Message = null;
        StartHost();
    }

    /// <summary>Asks the plugin for its settings, so they can be saved with the scene.</summary>
    public async Task<(string? Component, string? Controller)> GetStateAsync(TimeSpan timeout)
    {
        if (_host is null || Status != "running") return (_plugin.State, _plugin.ControllerState);
        var id = Interlocked.Increment(ref _stateId);
        var tcs = new TaskCompletionSource<(string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateWaits) _stateWaits[id] = tcs;
        _host.Send(new JsonObject { ["cmd"] = "state", ["id"] = id }.ToJsonString());
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        lock (_stateWaits) _stateWaits.Remove(id);
        if (done != tcs.Task) return (_plugin.State, _plugin.ControllerState);
        var st = tcs.Task.Result;
        _plugin = _plugin with { State = st.Item1, ControllerState = st.Item2 };
        return st;
    }

    public void ShowEditor(string title) => _host?.Send(new JsonObject { ["cmd"] = "editor", ["title"] = title }.ToJsonString());
    public void CloseEditor() => _host?.Send("{\"cmd\":\"close-editor\"}");

    protected override void Apply(object settings)
    {
        if (settings is RackItem { Plugin: { } p }) _wanted = p;
    }

    /// <summary>Sound in, sound out, two blocks later. Never waits for the host.</summary>
    protected override void Process(float[] left, float[]? right, int frames)
    {
        var r = right ?? left;
        for (var n = 0; n < frames; n++)
        {
            _inL[_pos] = left[n];
            _inR[_pos] = r[n];
            left[n] = _outL[_pos];
            if (right != null) right[n] = _outR[_pos];
            else left[n] = (_outL[_pos] + _outR[_pos]) * 0.5f;
            if (++_pos == _block)
            {
                _pos = 0;
                Exchange();
            }
        }
    }

    private unsafe void Exchange()
    {
        var b = _block;
        if (_lastPosted < 0 || _shm.Done == _lastPosted)
        {
            if (_lastPosted >= 0)
            {
                new ReadOnlySpan<float>(_shm.OutL, b).CopyTo(_outL);
                new ReadOnlySpan<float>(_shm.OutR, b).CopyTo(_outR);
            }
            else
            {
                Array.Clear(_outL);
                Array.Clear(_outR);
            }
            new ReadOnlySpan<float>(_inL).CopyTo(new Span<float>(_shm.InL, b));
            new ReadOnlySpan<float>(_inR).CopyTo(new Span<float>(_shm.InR, b));
            _seq++;
            _lastPosted = _seq;
            _shm.Posted = _seq;
            _signal.Set();
        }
        else
        {
            // The host is late or has stopped: play the sound through, two blocks late, so timing holds.
            Misses++;
            _prevL.CopyTo(_outL, 0);
            _prevR.CopyTo(_outR, 0);
        }
        _inL.CopyTo(_prevL, 0);
        _inR.CopyTo(_prevR, 0);
    }

    public override void Reset()
    {
        Array.Clear(_outL);
        Array.Clear(_outR);
    }

    public void Dispose()
    {
        try { _host?.Dispose(); } catch { }
        _host = null;
        _signal.Dispose();
        _shm.Dispose();
        try { File.Delete(_shmPath); } catch { }
    }
}
