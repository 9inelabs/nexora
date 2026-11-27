using System.Text.Json;
using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;
using Nexora.Audio.Plugins;
using Nexora.Audio.X32;

namespace Nexora.Audio.Engine;

/// <summary>The desk's own settings (not part of a scene). Saved to audio/desk.json.</summary>
public sealed record DeskSettings
{
    public string? X32Host { get; init; }
    /// <summary>Keep the X32's own faders, mutes and sends matching NEX-Audio, ready for the failsafe.</summary>
    public bool ShadowMix { get; init; } = true;
    /// <summary>Moving a fader or mute on the X32 moves it in NEX-Audio too.</summary>
    public bool FollowDesk { get; init; }
    /// <summary>The Live Output PC: where the stream mix and the heartbeat go.</summary>
    public string? LiveOutputHost { get; init; }
    public bool Armed { get; init; }
    /// <summary>The X32 scene (0 to 99) that routes the room through NEX-Audio.</summary>
    public int DeskScene { get; init; }
    /// <summary>The X32 scene (0 to 99) where the X32 runs the room by itself.</summary>
    public int ConsoleScene { get; init; } = 1;
    public int TimeoutMs { get; init; } = 500;
    public bool HandedOver { get; init; }
    public int Epoch { get; init; }
    /// <summary>The scene last recalled or saved, shown on the screen.</summary>
    public string? CurrentSceneId { get; init; }
}

public sealed record X32SceneName(int Number, string Name);
public sealed record PluginSlotStatus(string RackId, string StripId, string Name, string Status, string? Message, int LatencySamples, bool HasEditor, int Misses);

/// <summary>Everything about the desk that the NEX-Audio screens show, besides the mix itself.</summary>
public sealed record DeskStatus(
    long Rev,
    DeskSettings Settings,
    bool X32Connected,
    string? X32Name,
    IReadOnlyList<X32SceneName> X32Scenes,
    int? X32CurrentScene,
    int ShadowPending,
    long StreamPackets,
    string? FailsafeNote,
    IReadOnlyList<SavedMixInfo> Scenes,
    IReadOnlyList<SavedMixInfo> Snapshots,
    IReadOnlyList<PluginInfo> Plugins,
    int PluginFiles,
    bool Scanning,
    string? ScanNote,
    IReadOnlyList<PluginSlotStatus> PluginSlots,
    double MixLatencyMs,
    double SceneFade)
{
    /// <summary>What Undo and Redo would do, or null.</summary>
    public string? UndoText { get; init; }
    public string? RedoText { get; init; }
    public IReadOnlyList<PresetInfo> Presets { get; init; } = [];

    public static DeskStatus Empty { get; } = new(0, new DeskSettings(), false, null, [], null, 0, 0, null, [], [], [], 0, false, null, [], 0, 0);
}

/// <summary>Loudness of the stream mix, in LUFS and dB true peak.</summary>
public sealed record LoudnessReading(double Momentary, double ShortTerm, double Integrated, double TruePeak, double MaxTruePeak);

/// <summary>
/// NEX-Audio's desk: the mix, its scenes and snapshots, the link to the X32 (shadow mix, follow desk,
/// failsafe scenes), the stream to the Live Output PC, the heartbeat and the plugins.
/// It lives in the engine process, so it keeps going if every screen closes.
/// </summary>
public sealed class Desk : IDisposable
{
    private readonly string _dir;
    private readonly string _mixFile, _settingsFile, _pluginsFile;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly SceneStore _store;
    private readonly PresetStore _presets;
    private readonly MixHistory _history = new();
    private readonly X32Client _x32;
    private readonly HeartbeatSender? _heartbeat;
    private readonly StreamSender? _stream;
    private readonly Timer _timer;
    private readonly IPluginHostLauncher _launcher;
    private readonly List<PluginProcessor> _plugins = [];
    private readonly string _id = Guid.NewGuid().ToString("n")[..8];

    private MixState _mix;
    private DeskSettings _settings;
    private MixEngine? _engine;
    private long _rev;
    private bool _mixDirty;
    private DateTime _mixChangedAt;
    private PluginList _pluginList;
    private bool _scanning;
    private string? _scanNote;
    private string? _failsafeNote;
    private long _hbSeq;
    private Func<(string State, bool Flowing)> _health = () => ("stopped", false);
    private DateTime _notFlowingSince = DateTime.MinValue;
    private int _shadowPending;
    private bool _syncBusy;
    private int _tick;
    private volatile bool _reloadPdc;
    private double _rate = 48000;
    private int _frames = 128;

    // A scene fading in.
    private MixState? _fadeFrom, _fadeTo;
    private DateTime _fadeStart;
    private double _fadeSeconds;

    public Desk(string dataDir, Action<string>? log = null, IPluginHostLauncher? launcher = null, bool network = true)
    {
        _dir = dataDir;
        Directory.CreateDirectory(dataDir);
        _mixFile = Path.Combine(dataDir, "mix.json");
        _settingsFile = Path.Combine(dataDir, "desk.json");
        _pluginsFile = Path.Combine(dataDir, "plugins.json");
        _log = log ?? (_ => { });
        _store = new SceneStore(dataDir);
        _presets = new PresetStore(dataDir);
        _launcher = launcher ?? new ProcessHostLauncher(Environment.ProcessPath ?? "NexAudio.exe");
        _settings = LoadJson<DeskSettings>(_settingsFile) ?? new DeskSettings();
        _mix = (File.Exists(_mixFile) ? MixState.FromJson(File.ReadAllText(_mixFile)) : null) ?? MixState.CreateDefault();
        _pluginList = PluginScanner.Load(_pluginsFile);
        _x32 = new X32Client();
        _x32.ValueChanged += OnDeskValue;
        _x32.Changed += () => Changed?.Invoke();
        if (network)
        {
            _heartbeat = new HeartbeatSender(MakeHeartbeat) { Host = _settings.LiveOutputHost };
            _stream = new StreamSender(() => _engine?.StreamTap) { Host = StreamHost() };
        }
        _timer = new Timer(_ => Tick(), null, 100, 100);
        if (_settings.X32Host is { Length: > 0 } h) _ = ConnectX32Async(h);
    }

    /// <summary>Something on the desk status changed (scenes, plugins, X32 link, failsafe).</summary>
    public event Action? Changed;
    /// <summary>A setting in the mix changed: the path and value (for the other screens).</summary>
    public event Action<string, JsonElement, long>? MixChanged;
    /// <summary>The whole mix changed shape (a bus or effect added, a scene recalled).</summary>
    public event Action<MixState, long>? MixReplaced;
    public event Action<string, bool>? Notice;

    public long Rev => Interlocked.Read(ref _rev);
    public DeskSettings Settings { get { lock (_gate) return _settings; } }
    public X32Client X32 => _x32;

    /// <summary>A copy of the whole mix.</summary>
    public MixState Mix { get { lock (_gate) return _mix.Clone(); } }

    /// <summary>Tells the desk how the sound engine is doing (for the heartbeat and the failsafe).</summary>
    public void SetHealth(Func<(string State, bool Flowing)> health) => _health = health;

    // ---- The sound engine ----------------------------------------------------------------------

    /// <summary>Makes the mixing engine for a newly opened driver.</summary>
    public MixEngine Attach(int inputs, int outputs, int frames, double sampleRate)
    {
        DisposePlugins();
        _rate = sampleRate;
        _frames = frames;
        var engine = new MixEngine(inputs, outputs, frames, sampleRate, MakePlugin);
        lock (_gate) engine.Load(_mix);
        _engine = engine;
        Changed?.Invoke();
        return engine;
    }

    public void Detach()
    {
        _engine = null;
        DisposePlugins();
        Changed?.Invoke();
    }

    public MixEngine? Engine => _engine;

    private Processor? MakePlugin(RackItem item)
    {
        if (item.Plugin is null) return null;
        try
        {
            var p = new PluginProcessor(item, _rate, _frames, _dir, _launcher, _log);
            p.LatencyChanged += () => _reloadPdc = true;
            p.StatusChanged += () => Changed?.Invoke();
            p.Edited += () => _mixDirty = true;
            lock (_plugins) _plugins.Add(p);
            return p;
        }
        catch (Exception ex)
        {
            Notice?.Invoke($"Could not start the plugin {item.Plugin.Name}: {ex.Message}", true);
            return null;
        }
    }

    private void DisposePlugins()
    {
        List<PluginProcessor> list;
        lock (_plugins)
        {
            list = [.. _plugins];
            _plugins.Clear();
        }
        foreach (var p in list)
            try { p.Dispose(); } catch { }
    }

    // ---- Changing the mix ------------------------------------------------------------------------

    /// <summary>Changes one setting by its path (see <see cref="MixParams"/>).</summary>
    public bool Set(string path, JsonElement value)
    {
        long rev;
        var newStep = false;
        lock (_gate)
        {
            var level = MixHistory.IsLevel(path);
            var before = !level && _history.Wants(path) ? _mix.Clone() : null;
            if (!MixParams.Apply(_mix, path, value)) return false;
            if (before is not null && _history.Before(before, MixHistory.Describe(path), path)) newStep = true;
            else if (!level) _history.Touch(path);
            CancelFade();
            rev = Touch();
        }
        MixChanged?.Invoke(path, value, rev);
        if (newStep) Changed?.Invoke();
        return true;
    }

    /// <summary>Runs a change to the shape of the mix and sends everyone the new mix.</summary>
    public bool Change(Func<MixState, bool> change, string? label = null)
    {
        MixState copy;
        long rev;
        lock (_gate)
        {
            var before = _mix.Clone();
            if (!change(_mix)) return false;
            if (label is not null) _history.Before(before, label);
            _mix.Repair();
            CancelFade();
            rev = Touch();
            copy = _mix.Clone();
        }
        DropUnused();
        MixReplaced?.Invoke(copy, rev);
        if (label is not null) Changed?.Invoke();
        return true;
    }

    // ---- Undo, redo and presets --------------------------------------------------------------------

    public bool Undo() => Restore(_history.Undo);

    public bool Redo() => Restore(_history.Redo);

    private bool Restore(Func<MixState, MixState?> step)
    {
        MixState copy;
        long rev;
        lock (_gate)
        {
            var back = step(_mix);
            if (back is null) return false;
            back.Repair();
            CancelFade();
            _mix = back;
            rev = Touch();
            copy = _mix.Clone();
        }
        DropUnused();
        MixReplaced?.Invoke(copy, rev);
        Changed?.Invoke();
        return true;
    }

    public ChannelPreset SavePreset(string stripId, string name)
    {
        Strip s;
        lock (_gate) s = (_mix.Find(stripId) ?? throw new InvalidOperationException("That strip could not be found.")).Clone();
        var p = _presets.Save(name, s);
        Changed?.Invoke();
        return p;
    }

    public bool LoadPreset(string stripId, string presetId, string parts)
    {
        var p = _presets.Load(presetId);
        if (p is null) return false;
        return Change(m => MixParams.Paste(m, stripId, p.Sound, parts), $"preset \"{p.Name}\" on {stripId}");
    }

    public bool DeletePreset(string id)
    {
        var ok = _presets.Delete(id);
        if (ok) Changed?.Invoke();
        return ok;
    }

    private long Touch()
    {
        _engine?.Load(_mix);
        _mixDirty = true;
        _mixChangedAt = DateTime.UtcNow;
        return Interlocked.Increment(ref _rev);
    }

    private void DropUnused()
    {
        var engine = _engine;
        if (engine is null) return;
        MixState m;
        lock (_gate) m = _mix;
        foreach (var p in engine.DropUnused(m).OfType<PluginProcessor>())
        {
            lock (_plugins) _plugins.Remove(p);
            // Let the audio thread finish its current buffer with it first.
            _ = Task.Delay(500).ContinueWith(_ => p.Dispose());
        }
    }

    /// <summary>Sets the strip the spectrum analyser listens to (null = the stream mix).</summary>
    public void Analyse(string? stripId)
    {
        if (_engine is not { } e) return;
        e.AnalyseId = stripId;
        lock (_gate) e.Load(_mix);
    }

    // ---- Scenes and snapshots ------------------------------------------------------------------

    public async Task<SavedMix> SaveAsync(string? id, string name, string notes, string by, bool snapshot, RecallScope scope, double fade)
    {
        await CollectPluginStatesAsync();
        var m = new SavedMix
        {
            Id = id ?? Guid.NewGuid().ToString("n")[..10], Name = name, Notes = notes, SavedBy = by, Scope = scope,
            FadeSeconds = Math.Clamp(fade, 0, 30), SavedUtc = DateTime.UtcNow, Mix = Mix,
        };
        var saved = _store.Save(m, snapshot);
        if (!snapshot) SaveSettings(Settings with { CurrentSceneId = saved.Id });
        Changed?.Invoke();
        return saved;
    }

    public bool Recall(string id, bool snapshot, RecallScope? scope = null, double? fade = null)
    {
        var saved = _store.Load(id, snapshot);
        if (saved is null) return false;
        var sc = scope ?? saved.Scope;
        var seconds = fade ?? saved.FadeSeconds;
        MixState target, copy;
        long rev;
        lock (_gate)
        {
            target = SceneRecall.Apply(_mix, saved.Mix, sc);
            _history.Before(_mix, $"recall \"{saved.Name}\"");
            if (seconds > 0.05)
            {
                _fadeFrom = _mix.Clone();
                _fadeTo = target;
                _fadeStart = DateTime.UtcNow;
                _fadeSeconds = seconds;
                // Everything except the levels jumps now; the levels glide from where they were.
                _mix = SceneRecall.Blend(_fadeFrom, _fadeTo, 0);
            }
            else
            {
                CancelFade();
                _mix = target;
            }
            rev = Touch();
            copy = _mix.Clone();
        }
        DropUnused();
        if (!snapshot) SaveSettings(Settings with { CurrentSceneId = id });
        MixReplaced?.Invoke(copy, rev);
        Changed?.Invoke();
        _log($"Recalled {(snapshot ? "snapshot" : "scene")} \"{saved.Name}\".");
        return true;
    }

    public bool Delete(string id, bool snapshot)
    {
        var ok = _store.Delete(id, snapshot);
        Changed?.Invoke();
        return ok;
    }

    public void ReorderSnapshots(IReadOnlyList<string> ids)
    {
        _store.Reorder(ids);
        Changed?.Invoke();
    }

    /// <summary>Recalls the next (or previous) snapshot in the running order.</summary>
    public bool StepSnapshot(int direction, string? currentId)
    {
        var list = _store.Snapshots;
        if (list.Count == 0) return false;
        var i = currentId is null ? -1 : list.ToList().FindIndex(s => s.Id == currentId);
        var next = Math.Clamp(i + Math.Sign(direction), 0, list.Count - 1);
        if (i >= 0 && next == i) return false;
        return Recall(list[next].Id, true);
    }

    private void CancelFade()
    {
        _fadeFrom = _fadeTo = null;
    }

    private void FadeStep()
    {
        MixState copy;
        long rev;
        lock (_gate)
        {
            if (_fadeFrom is null || _fadeTo is null) return;
            var t = (DateTime.UtcNow - _fadeStart).TotalSeconds / _fadeSeconds;
            _mix = SceneRecall.Blend(_fadeFrom, _fadeTo, Math.Clamp(t, 0, 1));
            if (t >= 1) CancelFade();
            _engine?.Load(_mix);
            _mixDirty = true;
            rev = Interlocked.Increment(ref _rev);
            copy = _mix.Clone();
        }
        MixReplaced?.Invoke(copy, rev);
    }

    private async Task CollectPluginStatesAsync()
    {
        var engine = _engine;
        if (engine is null) return;
        List<(string strip, RackItem item)> items;
        lock (_gate) items = _mix.AllStrips().SelectMany(s => s.Rack.Where(r => r.Plugin != null).Select(r => (s.Id, r))).ToList();
        foreach (var (_, item) in items)
        {
            if (engine.ProcessorOf(item.Id) is not PluginProcessor p) continue;
            var (c, k) = await p.GetStateAsync(TimeSpan.FromSeconds(2));
            lock (_gate)
            {
                var live = _mix.AllStrips().SelectMany(s => s.Rack).FirstOrDefault(r => r.Id == item.Id);
                if (live?.Plugin != null) live.Plugin = live.Plugin with { State = c, ControllerState = k };
            }
        }
    }

    // ---- Plugins ---------------------------------------------------------------------------------

    public IReadOnlyList<PluginInfo> Plugins => _pluginList.Plugins;

    public void ScanPlugins()
    {
        if (_scanning) return;
        _scanning = true;
        _scanNote = "Looking for plugins…";
        Changed?.Invoke();
        _ = Task.Run(() =>
        {
            try
            {
                var exe = Environment.ProcessPath ?? "NexAudio.exe";
                var list = PluginScanner.Scan(_pluginList, exe, null, null, name =>
                {
                    _scanNote = $"Checking {name}…";
                    Changed?.Invoke();
                });
                _pluginList = list;
                PluginScanner.Save(_pluginsFile, list);
                _scanNote = list.Plugins.Count == 0
                    ? "No VST3 plugins were found. Install them into C:\\Program Files\\Common Files\\VST3."
                    : $"Found {list.Plugins.Count} plugins" + (list.Failed.Count > 0 ? $" ({list.Failed.Count} files could not be opened)." : ".");
            }
            catch (Exception ex)
            {
                _scanNote = "Could not look for plugins: " + ex.Message;
            }
            _scanning = false;
            Changed?.Invoke();
        });
    }

    private PluginProcessor? Slot(string rackId) => _engine?.ProcessorOf(rackId) as PluginProcessor;

    public void ShowPluginEditor(string rackId)
    {
        var p = Slot(rackId) ?? throw new InvalidOperationException("That plugin is not running. Start the sound engine first.");
        p.ShowEditor(p.Name);
    }

    public void RetryPlugin(string rackId) => Slot(rackId)?.Retry();

    // ---- X32 link ---------------------------------------------------------------------------------

    public async Task<bool> ConnectX32Async(string host)
    {
        SaveSettings(Settings with { X32Host = host });
        var info = await _x32.ConnectAsync(host);
        if (info is null)
        {
            Notice?.Invoke($"No X32 answered at {host}. Check the address on the X32's Setup > Network page.", true);
            return false;
        }
        await _x32.RequestScenesAsync();
        // Ask for the desk's current values, so the shadow mix only sends what differs.
        foreach (var addr in ShadowMix.Desired(Mix).Keys) await _x32.SendAsync(new OscMessage(addr));
        Changed?.Invoke();
        return true;
    }

    public void DisconnectX32()
    {
        SaveSettings(Settings with { X32Host = null });
        _x32.Disconnect();
    }

    private void OnDeskValue(string address, float value)
    {
        if (!Settings.FollowDesk) return;
        (string Path, object Value)? change;
        lock (_gate) change = ShadowMix.FromDesk(_mix, address, value);
        if (change is not { } c) return;
        var el = JsonSerializer.SerializeToElement(c.Value, c.Value.GetType());
        Set(c.Path, el);
    }

    // ---- Failsafe ---------------------------------------------------------------------------------

    public void Configure(Func<DeskSettings, DeskSettings> change)
    {
        var s = change(Settings);
        s = s with
        {
            DeskScene = Math.Clamp(s.DeskScene, 0, 99), ConsoleScene = Math.Clamp(s.ConsoleScene, 0, 99),
            TimeoutMs = Math.Clamp(s.TimeoutMs, 200, 5000),
        };
        SaveSettings(s);
        if (_heartbeat != null) _heartbeat.Host = s.LiveOutputHost;
        if (_stream != null) _stream.Host = StreamHost();
        Changed?.Invoke();
    }

    private string? StreamHost()
    {
        var s = Settings;
        bool send;
        lock (_gate) send = _mix.StreamSettings.SendToLiveOutput;
        return send ? s.LiveOutputHost : null;
    }

    /// <summary>Hands the room to the X32 now (the "Console only" scene).</summary>
    public async Task HandOverAsync(string reason)
    {
        var s = Settings;
        SaveSettings(s with { HandedOver = true });
        _failsafeNote = $"{reason} The X32 runs the room on scene {s.ConsoleScene + 1}.";
        _log("Failsafe: " + _failsafeNote);
        if (_x32.Connected) await _x32.RecallSceneAsync(s.ConsoleScene);
        else if (s.X32Host is { } h) await X32Client.RecallSceneAsync(h, s.ConsoleScene);
        Changed?.Invoke();
    }

    /// <summary>Brings the room back to NEX-Audio (the "NEX-Audio desk" scene), at a quiet moment.</summary>
    public async Task<bool> ReturnAsync()
    {
        var (state, flowing) = _health();
        if (state != "running" || !flowing)
        {
            Notice?.Invoke("NEX-Audio's sound is not running yet, so the room stays on the X32.", true);
            return false;
        }
        var s = Settings;
        if (!_x32.Connected)
        {
            Notice?.Invoke("Connect to the X32 first (X32 link).", true);
            return false;
        }
        await ShadowMix.SyncAsync(_x32, Mix, 2000);
        await _x32.RecallSceneAsync(s.DeskScene);
        SaveSettings(s with { HandedOver = false, Epoch = s.Epoch + 1 });
        _failsafeNote = null;
        _notFlowingSince = DateTime.MinValue;
        _log("Room returned to NEX-Audio.");
        Changed?.Invoke();
        return true;
    }

    private Heartbeat MakeHeartbeat()
    {
        var s = Settings;
        var (state, flowing) = _health();
        return new Heartbeat(_id, Environment.MachineName, Interlocked.Increment(ref _hbSeq), state, flowing, s.Armed, s.HandedOver, s.Epoch,
            s.X32Host, s.DeskScene, s.ConsoleScene, s.TimeoutMs);
    }

    // ---- Housekeeping ---------------------------------------------------------------------------

    private void Tick()
    {
        try
        {
            _tick++;
            FadeStep();

            if (_reloadPdc)
            {
                _reloadPdc = false;
                lock (_gate) _engine?.Load(_mix);
                Changed?.Invoke();
            }
            List<PluginProcessor> plugs;
            lock (_plugins) plugs = [.. _plugins];
            foreach (var p in plugs) p.Service();

            // The engine itself also hands over if its own sound stops while armed (the Live Output PC may be off).
            var s = Settings;
            var (state, flowing) = _health();
            if (s.Armed && !s.HandedOver && _engine != null)
            {
                if (flowing) _notFlowingSince = DateTime.MinValue;
                else if (_notFlowingSince == DateTime.MinValue) _notFlowingSince = DateTime.UtcNow;
                else if ((DateTime.UtcNow - _notFlowingSince).TotalMilliseconds > s.TimeoutMs)
                    _ = HandOverAsync("NEX-Audio's sound stopped.");
            }

            // Keep the X32's own mix in step, a few times a second.
            if (s.ShadowMix && _x32.Connected && !_syncBusy && _tick % 2 == 0)
            {
                _syncBusy = true;
                var m = Mix;
                _ = Task.Run(async () =>
                {
                    try { _shadowPending = await ShadowMix.SyncAsync(_x32, m, 200); }
                    catch { }
                    finally { _syncBusy = false; }
                });
            }

            if (_stream != null) _stream.Host = StreamHost();

            // Save the mix a moment after it stops changing.
            if (_mixDirty && DateTime.UtcNow - _mixChangedAt > TimeSpan.FromSeconds(1)) SaveMix();
        }
        catch (Exception ex)
        {
            _log("Desk tick: " + ex.Message);
        }
    }

    public DeskStatus Status
    {
        get
        {
            var s = Settings;
            var engine = _engine;
            List<PluginSlotStatus> slots = [];
            if (engine != null)
            {
                MixState m;
                lock (_gate) m = _mix;
                foreach (var st in m.AllStrips())
                    foreach (var r in st.Rack.Where(r => r.Type == "plugin"))
                        if (engine.ProcessorOf(r.Id) is PluginProcessor p)
                            slots.Add(new PluginSlotStatus(r.Id, st.Id, p.Name, p.Status, p.Message, p.Latency, p.HasEditor, p.Misses));
            }
            return new DeskStatus(
                Rev, s, _x32.Connected, _x32.Info?.Name,
                _x32.Scenes.OrderBy(k => k.Key).Select(k => new X32SceneName(k.Key, k.Value)).ToList(), _x32.CurrentScene,
                _shadowPending, _stream?.PacketsSent ?? 0, _failsafeNote,
                _store.Scenes, _store.Snapshots, _pluginList.Plugins, _pluginList.Seen.Count, _scanning, _scanNote, slots,
                engine is null ? 0 : engine.MixLatency * 1000.0 / engine.SampleRate,
                _fadeTo != null ? Math.Clamp((DateTime.UtcNow - _fadeStart).TotalSeconds / _fadeSeconds, 0, 1) : 0)
            {
                UndoText = _history.UndoLabel, RedoText = _history.RedoLabel, Presets = _presets.List(),
            };
        }
    }

    public LoudnessReading? Loudness()
    {
        var e = _engine;
        if (e is null) return null;
        e.Loudness.Update();
        var l = e.Loudness;
        return new LoudnessReading(l.Momentary, l.ShortTerm, l.Integrated, l.TruePeakDb, l.MaxTruePeakDb);
    }

    public void ResetLoudness() => _engine?.Loudness.ResetIntegrated();

    // ---- Files -------------------------------------------------------------------------------------

    private void SaveMix()
    {
        string json;
        lock (_gate)
        {
            _mixDirty = false;
            json = JsonSerializer.Serialize(_mix, MixState.Json);
        }
        try
        {
            var tmp = _mixFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _mixFile, true);
        }
        catch (Exception ex) { _log("Could not save the mix: " + ex.Message); }
    }

    /// <summary>Brings a Phase 1 patch (output = input number, off or tone) into the mix's outputs, the first time.</summary>
    public void ImportPatch(int[]? patch)
    {
        if (patch is null || File.Exists(_mixFile)) return;
        Change(m =>
        {
            for (var o = 0; o < m.Outputs.Count && o < patch.Length; o++)
                m.Outputs[o].Source = patch[o] switch { EngineCore.Tone => "tone", >= 0 and var i => $"in:{i + 1}", _ => "off" };
            return true;
        });
        SaveMix();
    }

    private void SaveSettings(DeskSettings s)
    {
        lock (_gate)
        {
            _settings = s;
            try
            {
                var tmp = _settingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, MixState.Json));
                File.Move(tmp, _settingsFile, true);
            }
            catch (Exception ex) { _log("Could not save desk settings: " + ex.Message); }
        }
    }

    private static T? LoadJson<T>(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), MixState.Json) : default; }
        catch { return default; }
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (_mixDirty) SaveMix();
        _heartbeat?.Dispose();
        _stream?.Dispose();
        _x32.Dispose();
        DisposePlugins();
    }
}
