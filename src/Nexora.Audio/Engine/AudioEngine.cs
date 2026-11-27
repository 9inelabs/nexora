using System.Text.Json;
using Nexora.Audio.Asio;
using Nexora.Audio.Ipc;
using Nexora.Audio.Mix;

namespace Nexora.Audio.Engine;

/// <summary>What the engine remembers between restarts, so the sound comes back by itself.</summary>
public sealed record EngineSettings
{
    public string? Driver { get; init; }
    public int BufferSize { get; init; }
    public int[]? Patch { get; init; }
    /// <summary>True while someone wants the sound running. The engine starts it again after a restart.</summary>
    public bool Running { get; init; }
}

/// <summary>
/// Runs one sound driver with the <see cref="EngineCore"/>: opens and starts it, keeps an eye on it,
/// opens it again when the driver asks or stops sending audio, and remembers the settings.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const string Version = "1";

    private readonly string _settingsFile;
    private readonly Action<string> _log;
    private readonly Func<string, IAudioDevice?> _makeDevice;
    private readonly DeviceThread _thread;
    private readonly AudioStats _stats = new();
    private readonly object _gate = new();

    private EngineSettings _settings;
    private IAudioDevice? _device;
    private EngineCore? _core;
    private EngineState _state = EngineState.Stopped;
    private string _message = "The sound engine is ready. Choose the X32's USB driver to start.";
    private int _restarts;
    private DateTime _runningSince = DateTime.MinValue, _lastAttempt = DateTime.MinValue, _silentSince = DateTime.MinValue;
    private double _loadAvg, _loadPeak;
    private LatencyResult? _latency;
    private bool _latencyRunning;
    private int _latOut, _latIn;
    private IReadOnlyList<string> _drivers = [];

    private readonly Desk? _desk;

    public AudioEngine(string dataDir, Action<string>? log = null, Func<string, IAudioDevice?>? makeDevice = null, Desk? desk = null)
    {
        _desk = desk;
        Directory.CreateDirectory(dataDir);
        _settingsFile = Path.Combine(dataDir, "engine.json");
        _log = log ?? (_ => { });
        _makeDevice = makeDevice ?? MakeDevice;
        _settings = LoadSettings(_settingsFile);
        _drivers = FindDrivers();
        if (_desk is not null)
        {
            _desk.ImportPatch(_settings.Patch);
            _desk.SetHealth(() => (
                _state switch { EngineState.Running => "running", EngineState.Stopped => "stopped", _ => "problem" },
                _core is not null && _stats.Flowing(0.25)));
        }
        _thread = new DeviceThread(Tick, TimeSpan.FromMilliseconds(250));
        if (_settings.Running && _settings.Driver is { } d)
        {
            _message = $"Starting {d} again…";
            _ = _thread.Run(() => OpenOnThread(d, _settings.BufferSize, quiet: true));
        }
    }

    /// <summary>Called on the engine's own thread whenever something on the status changes.</summary>
    public event Action? Changed;

    public EngineSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    public static IReadOnlyList<string> FindDrivers() =>
        AsioDriverList.Find().Select(d => d.Name).Append(SimulatedDevice.DeviceName).ToList();

    private static IAudioDevice? MakeDevice(string name)
    {
        if (name == SimulatedDevice.DeviceName) return new SimulatedDevice();
        if (!OperatingSystem.IsWindows()) return null;
        var info = AsioDriverList.Find().FirstOrDefault(d => d.Name == name);
        return info is null ? null : new AsioDevice(info);
    }

    // ---- Commands (from the screen) ------------------------------------------------------

    public Task OpenAsync(string driver, int bufferSize)
    {
        Save(_settings with { Driver = driver, BufferSize = bufferSize, Running = true });
        return _thread.Run(() => OpenOnThread(driver, bufferSize, quiet: false));
    }

    public Task CloseAsync()
    {
        Save(_settings with { Running = false });
        return _thread.Run(() =>
        {
            CloseOnThread();
            SetState(EngineState.Stopped, "The sound engine is stopped. Outputs fed from NEX-Audio are silent.");
        });
    }

    /// <summary>The desk (mix, scenes, X32 link, failsafe, plugins), when this engine has one.</summary>
    public Desk? Desk => _desk;

    public void SetPatch(int[] patch)
    {
        if (_desk is not null)
        {
            _desk.Change(m =>
            {
                for (var o = 0; o < m.Outputs.Count && o < patch.Length; o++)
                    m.Outputs[o].Source = patch[o] switch { EngineCore.Tone => "tone", >= 0 and var i => $"in:{i + 1}", _ => "off" };
                return true;
            });
        }
        var core = _core;
        if (core is not null)
        {
            core.SetPatch(patch);
            Save(_settings with { Patch = core.Patch });
        }
        else Save(_settings with { Patch = patch });
        Changed?.Invoke();
    }

    public Task ShowDriverPanelAsync() => _thread.Run(() => _device?.ShowSettingsPanel());

    public void RefreshDrivers()
    {
        _drivers = FindDrivers();
        Changed?.Invoke();
    }

    public void ResetClips() => _core?.ResetClips();

    public void StartLatencyTest(int output, int input)
    {
        var core = _core ?? throw new InvalidOperationException("Start the sound engine first.");
        if (output < 0 || output >= core.Outputs || input < 0 || input >= core.Inputs)
            throw new InvalidOperationException("Pick an output and an input the driver has.");
        _latOut = output;
        _latIn = input;
        core.StartLatencyTest(output, input);
        _latencyRunning = true;
        Changed?.Invoke();
    }

    public bool TakeMeters(float[] inPeaks, float[] outPeaks, bool[] inClips, bool[] outClips)
    {
        var core = _core;
        if (core is null) return false;
        core.TakeMeters(inPeaks, outPeaks, inClips, outClips);
        return true;
    }

    public EngineStatus Status
    {
        get
        {
            var f = _device?.Format;
            var core = _core;
            return new EngineStatus(
                _state, _message, _settings.Driver, _drivers,
                f?.SampleRate ?? 0, f?.BufferSize ?? 0, f?.BufferSizes ?? [],
                f?.Inputs ?? 0, f?.Outputs ?? 0, f?.InputNames ?? [], f?.OutputNames ?? [],
                f?.InputLatency ?? 0, f?.OutputLatency ?? 0, f?.SampleType ?? "",
                _loadAvg, _loadPeak, _stats.LateBuffers, _stats.DriverProblems, _restarts,
                _device?.HasSettingsPanel == true,
                core?.Patch ?? _settings.Patch ?? [],
                _latencyRunning, _latency, Version, _runningSince);
        }
    }

    // ---- On the driver thread -------------------------------------------------------------

    private void OpenOnThread(string driver, int bufferSize, bool quiet)
    {
        CloseOnThread();
        _lastAttempt = DateTime.UtcNow;
        SetState(EngineState.Starting, $"Opening {driver}…");
        try
        {
            var device = _makeDevice(driver) ?? throw new AudioDeviceException(
                $"The driver \"{driver}\" is not on this PC. Install the X32 USB driver (X-USB ASIO) and plug in the X32.");
            device.Open(bufferSize);
            var f = device.Format!;
            var core = new EngineCore(f.Inputs, f.Outputs, f.BufferSize, f.SampleRate);
            if (_settings.Patch is { Length: > 0 } p) core.SetPatch(p);
            if (_desk is not null) core.Mix = _desk.Attach(f.Inputs, f.Outputs, f.BufferSize, f.SampleRate);
            _stats.Reset();
            device.Start(core, _stats);
            _device = device;
            _core = core;
            _runningSince = DateTime.UtcNow;
            _silentSince = DateTime.MinValue;
            var rateNote = Math.Abs(f.SampleRate - 48000) < 1 ? "" : $" The X32 is set to {f.SampleRate / 1000:0.#} kHz; NEX-Audio works best at 48 kHz.";
            SetState(EngineState.Running, $"Running on {driver}: {f.Inputs} in, {f.Outputs} out, {f.BufferSize} samples.{rateNote}");
            _log($"Audio engine running on {driver}: {f.Inputs}x{f.Outputs}, {f.SampleRate} Hz, buffer {f.BufferSize}, latency in {f.InputLatency} out {f.OutputLatency}");
        }
        catch (Exception ex)
        {
            CloseOnThread();
            var text = ex is AudioDeviceException ? ex.Message : $"Could not start {driver}: {ex.Message}";
            SetState(EngineState.Problem, text + " NEX-Audio keeps trying every few seconds.");
            if (!quiet) _log("Audio engine: " + ex);
        }
    }

    private void CloseOnThread()
    {
        var d = _device;
        _device = null;
        _core?.CancelLatencyTest();
        _core = null;
        _latencyRunning = false;
        _runningSince = DateTime.MinValue;
        if (d is null)
        {
            _desk?.Detach();
            return;
        }
        try { d.Close(); }
        catch (Exception ex) { _log("Audio engine close: " + ex.Message); }
        try { d.Dispose(); }
        catch (Exception) { }
        _desk?.Detach(); // after the driver has stopped calling the mix
    }

    /// <summary>Four times a second: health, driver requests, the latency test, and trying again after a failure.</summary>
    private void Tick()
    {
        var changed = false;
        var s = Settings;
        if (_device is { } dev && _core is { } core)
        {
            var seconds = core.MaxFrames / core.SampleRate;
            var (avg, peak) = _stats.TakeLoad(seconds);
            _loadAvg = avg;
            _loadPeak = Math.Max(peak, _loadPeak * 0.8);
            changed = true;

            if (dev.ResetRequested)
            {
                _restarts++;
                _log("Audio driver asked to be opened again (settings changed in the driver).");
                OpenOnThread(s.Driver!, s.BufferSize, quiet: false);
                Changed?.Invoke();
                return;
            }

            if (!_stats.Flowing(1.0) && DateTime.UtcNow - _runningSince > TimeSpan.FromSeconds(2))
            {
                if (_silentSince == DateTime.MinValue) _silentSince = DateTime.UtcNow;
                if (_state == EngineState.Running)
                    SetState(EngineState.Problem, "No sound is coming from the driver. Check the X32 is on and the USB cable is plugged in.");
                if (DateTime.UtcNow - _silentSince > TimeSpan.FromSeconds(5))
                {
                    _restarts++;
                    _log("Audio driver stopped sending audio; opening it again.");
                    OpenOnThread(s.Driver!, s.BufferSize, quiet: true);
                }
            }
            else if (_state == EngineState.Problem && _stats.Flowing(0.5))
            {
                _silentSince = DateTime.MinValue;
                SetState(EngineState.Running, $"Running on {s.Driver} again.");
            }

            if (_latencyRunning && core.LatencyResults is { } rounds)
            {
                _latencyRunning = false;
                _latency = new LatencyResult(_latOut, _latIn, rounds, EngineCore.Median(rounds), core.SampleRate, DateTime.UtcNow);
                _log($"Latency test out {_latOut + 1} to in {_latIn + 1}: {string.Join(", ", rounds)} samples");
            }
        }
        else if (s.Running && s.Driver is { } d && DateTime.UtcNow - _lastAttempt > TimeSpan.FromSeconds(4))
        {
            _restarts++;
            OpenOnThread(d, s.BufferSize, quiet: true);
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    private void SetState(EngineState state, string message)
    {
        _state = state;
        _message = message;
        Changed?.Invoke();
    }

    // ---- Settings -----------------------------------------------------------------------------

    private void Save(EngineSettings s)
    {
        lock (_gate)
        {
            _settings = s;
            try
            {
                var tmp = _settingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, AudioMessage.Json));
                File.Move(tmp, _settingsFile, true);
            }
            catch (Exception ex)
            {
                _log("Could not save audio settings: " + ex.Message);
            }
        }
    }

    private static EngineSettings LoadSettings(string file)
    {
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(file), AudioMessage.Json) ?? new EngineSettings();
        }
        catch (Exception) { }
        return new EngineSettings();
    }

    public void Dispose()
    {
        try { _thread.Run(CloseOnThread).Wait(3000); }
        catch (Exception) { }
        _thread.Dispose();
    }
}
