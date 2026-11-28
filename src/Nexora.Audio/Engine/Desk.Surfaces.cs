using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Nexora.Audio.Mix;
using Nexora.Audio.Remote;
using Nexora.Audio.Show;
using Nexora.Audio.Surface;
using Nexora.Audio.X32;

namespace Nexora.Audio.Engine;

/// <summary>Control surfaces and remotes: the X32's buttons, a MIDI fader box, tablets and phones, and NEXORA's triggers.</summary>
public sealed partial class Desk
{
    private bool _network;
    private readonly TouchGuard _x32Touch = new();
    private volatile string? _selected;
    private long _selectSeq;
    private IReadOnlyDictionary<string, float> _peaks = new Dictionary<string, float>();
    private string? _triggerNote;

    // The fader box.
    private readonly McuSurface _mcu = new();
    private readonly ConcurrentQueue<byte[]> _midiQueue = new();
    private readonly SemaphoreSlim _midiSignal = new(0);
    private IMidiIn? _midiInPort;
    private IMidiOut? _midiOutPort;
    private string? _midiOpenIn, _midiOpenOut;
    private IReadOnlyList<string> _midiInputs = [], _midiOutputs = [];
    private CancellationTokenSource? _midiCts;
    private string? _midiNote;

    // The tablet remote.
    private RemoteServer? _remote;
    private RemoteSettings? _remoteRunning;
    private string? _remoteError;
    private bool _remoteBusy;

    // The admin remote (laptops). The engine's link to the screens runs it; the desk only shows who is on it.
    private IReadOnlyList<LaptopClient> _laptops = [];
    private bool _laptopsRunning;
    private string? _laptopsError;

    /// <summary>The engine's link reports the laptops on the admin remote, and whether it is listening.</summary>
    public void SetLaptops(IReadOnlyList<LaptopClient> laptops, bool running, string? error)
    {
        var changed = running != _laptopsRunning || error != _laptopsError || !laptops.SequenceEqual(_laptops);
        _laptops = laptops;
        _laptopsRunning = running;
        _laptopsError = error;
        if (changed) Changed?.Invoke();
    }

    /// <summary>Where MIDI ports come from (the system's own on Windows; tests use their own).</summary>
    public IMidiPorts MidiPorts { get; set; } = new WinMidi();

    // ---- Selection, shared by the screen and every surface ----------------------------------------

    /// <summary>The strip selected on the screen or on a surface.</summary>
    public string? Selected => _selected;

    /// <summary>
    /// Selects a strip. From the screen, the X32's SELECT light and the fader box follow; from a surface,
    /// the screen follows too (it watches <see cref="DeskStatus.SurfaceSelectSeq"/>).
    /// </summary>
    public void Select(string? id, bool fromSurface)
    {
        if (id is not null && Mix_Find(id) is null) return;
        if (_selected == id && !fromSurface) return;
        _selected = id;
        if (fromSurface) Interlocked.Increment(ref _selectSeq);
        if (id is not null && Settings.X32Surface && _x32.Connected && !fromSurface)
        {
            int index;
            lock (_gate) index = X32Surface.SelectIndex(_mix, id);
            if (index >= 0)
            {
                _x32Touch.Moved("/-stat/selidx");
                _ = _x32.SetAsync("/-stat/selidx", index);
            }
        }
        _midiDirty = true;
        Changed?.Invoke();
    }

    private Strip? Mix_Find(string id)
    {
        lock (_gate) return _mix.Find(id);
    }

    // ---- Start and stop -----------------------------------------------------------------------------

    private void StartSurfaces()
    {
        RefreshMidi();
        ApplySurfaces();
    }

    /// <summary>Opens or closes the fader box and the remote to match the settings. Call after any change.</summary>
    public void ApplySurfaces()
    {
        var s = Settings;
        ApplyMidi(s);
        if (_network) _ = ApplyRemoteAsync(s);
    }

    public void RefreshMidi()
    {
        try
        {
            _midiInputs = MidiPorts.Inputs();
            _midiOutputs = MidiPorts.Outputs();
        }
        catch (Exception ex) { _log("MIDI: " + ex.Message); }
        Changed?.Invoke();
    }

    public void ConfigureSurfaces(bool x32Surface, string? midiIn, string? midiOut, RemoteSettings remote)
    {
        SaveSettings(Settings with
        {
            X32Surface = x32Surface,
            MidiIn = string.IsNullOrWhiteSpace(midiIn) ? null : midiIn,
            MidiOut = string.IsNullOrWhiteSpace(midiOut) ? null : midiOut,
            Remote = (remote ?? new RemoteSettings()).Clean(),
        });
        ApplySurfaces();
        Changed?.Invoke();
    }

    private void DisposeSurfaces()
    {
        CloseMidi();
        var r = _remote;
        _remote = null;
        if (r is not null) _ = r.DisposeAsync().AsTask();
    }

    // ---- The fader box (Mackie Control) ---------------------------------------------------------------

    private volatile bool _midiDirty = true;

    private void ApplyMidi(DeskSettings s)
    {
        if (s.MidiIn == _midiOpenIn && s.MidiOut == _midiOpenOut && (s.MidiIn is null || _midiInPort is not null)) return;
        CloseMidi();
        _midiOpenIn = s.MidiIn;
        _midiOpenOut = s.MidiOut;
        if (s.MidiIn is null && s.MidiOut is null) { _midiNote = null; return; }
        try
        {
            if (s.MidiIn is { } i)
            {
                _midiInPort = MidiPorts.OpenIn(i);
                if (_midiInPort is not null) _midiInPort.Received += m => { _midiQueue.Enqueue(m); _midiSignal.Release(); };
            }
            if (s.MidiOut is { } o) _midiOutPort = MidiPorts.OpenOut(o);
        }
        catch (Exception ex) { _log("MIDI: " + ex.Message); }
        _midiNote = s.MidiIn is not null && _midiInPort is null ? $"\"{s.MidiIn}\" is not plugged in, or another program is using it."
            : s.MidiOut is not null && _midiOutPort is null ? $"\"{s.MidiOut}\" cannot be opened: the motor faders and lights will not follow." : null;
        if (_midiInPort is null && _midiOutPort is null) return;
        _mcu.Reset();
        _midiDirty = true;
        var cts = _midiCts = new CancellationTokenSource();
        _ = Task.Run(() => MidiLoop(cts.Token));
        _log($"Fader box: {s.MidiIn ?? "-"} / {s.MidiOut ?? "-"}.");
    }

    private void CloseMidi()
    {
        _midiCts?.Cancel();
        _midiCts = null;
        try { _midiInPort?.Dispose(); } catch { }
        try { _midiOutPort?.Dispose(); } catch { }
        _midiInPort = null;
        _midiOutPort = null;
        _midiOpenIn = _midiOpenOut = null;
    }

    /// <summary>
    /// Reads the fader box as soon as it sends anything, and keeps its motor faders, lights and screen in step
    /// about 25 times a second. A fader moving sends many messages: only the last of each is used.
    /// </summary>
    private async Task MidiLoop(CancellationToken ct)
    {
        var lastRefresh = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try { await _midiSignal.WaitAsync(40, ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                ProcessMidi();
                if (DateTime.UtcNow - lastRefresh > TimeSpan.FromMilliseconds(40))
                {
                    lastRefresh = DateTime.UtcNow;
                    RefreshMidiOut();
                }
            }
            catch (Exception ex) { _log("Fader box: " + ex.Message); }
        }
    }

    /// <summary>Takes in what the fader box sent. Public for tests.</summary>
    public void ProcessMidi()
    {
        var sets = new Dictionary<string, object>();
        string? select = null;
        var bank = _mcu.Bank;
        while (_midiQueue.TryDequeue(out var m))
        {
            IReadOnlyList<SurfaceAction> actions;
            lock (_gate) actions = _mcu.Receive(m, _mix);
            foreach (var a in actions)
                switch (a)
                {
                    case SetAction set: sets[set.Path] = set.Value; break;
                    case SelectAction sel: select = sel.StripId; break;
                }
        }
        if (bank != _mcu.Bank) _midiDirty = true;
        foreach (var (path, value) in sets) Set(path, JsonSerializer.SerializeToElement(value, value.GetType()));
        if (select is not null) Select(select, fromSurface: true);
    }

    /// <summary>Feeds the fader box a MIDI message as if it came from the box (for tests and the screen's test buttons).</summary>
    public void InjectMidi(byte[] msg) => _midiQueue.Enqueue(msg);

    /// <summary>Sends the fader box whatever changed. Public for tests.</summary>
    public IReadOnlyList<byte[]> RefreshMidiOut()
    {
        IReadOnlyList<byte[]> msgs;
        lock (_gate) msgs = _mcu.Refresh(_mix, _selected, MixState.Label);
        var peaks = _peaks;
        var list = msgs.ToList();
        if (_tick % 2 == 0)
            for (var f = 0; f < McuSurface.Width; f++)
                if (_mcu.StripAt(f) is { } id) list.Add(McuSurface.Meter(f, peaks.GetValueOrDefault(id)));
        if (_midiOutPort is { } o) foreach (var m in list) o.Send(m);
        _midiDirty = false;
        return list;
    }

    private string? MidiText()
    {
        if (_midiNote is { } n) return n;
        if (_midiInPort is null && _midiOutPort is null) return null;
        string Name(string? id)
        {
            if (id is null) return "-";
            lock (_gate) return _mix.Find(id) is { } st ? MixState.Label(st) : "-";
        }
        return $"Faders 1 to 8: {Name(_mcu.StripAt(0))} to {Name(_mcu.StripAt(7) ?? _mcu.StripAt(0))}. Use the box's bank buttons to move along.";
    }

    private void SurfaceTick()
    {
        // The fader box is refreshed on its own loop; this only catches a change made while nothing came in.
        if (_midiDirty && _midiCts is not null) _midiSignal.Release();
    }

    // ---- The tablet and phone remote ------------------------------------------------------------------

    private async Task ApplyRemoteAsync(DeskSettings s)
    {
        var want = s.Remote.On && !s.Offline ? s.Remote : null;
        if (_remoteBusy) return;
        _remoteBusy = true;
        try
        {
            if (want is null)
            {
                if (_remote is { } r)
                {
                    _remote = null;
                    await r.DisposeAsync();
                    _log("Remote: stopped.");
                }
                _remoteRunning = null;
                _remoteError = null;
                return;
            }
            if (_remote is { Running: true } && _remoteRunning?.Port == want.Port) { _remoteRunning = want; return; }
            _remote ??= new RemoteServer(this, _log);
            _remoteError = await _remote.StartAsync(want.Port) ? null : _remote.Error;
            _remoteRunning = want;
        }
        finally
        {
            _remoteBusy = false;
            Changed?.Invoke();
        }
    }

    MixState IRemoteDesk.MixNow() => Mix;
    RemoteSettings IRemoteDesk.Remote => Settings.Remote;
    IReadOnlyDictionary<string, float> IRemoteDesk.Peaks => _peaks;
    string? IRemoteDesk.NextCueName
    {
        get { lock (_showGate) return _show.Next?.Name; }
    }

    public static IReadOnlyList<string> LanAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address.ToString());
            }
        }
        catch (Exception) { }
        return list;
    }

    // ---- NEXORA ----------------------------------------------------------------------------------------

    /// <summary>The screens reported something going live: fire the matching triggers.</summary>
    public void Live(LiveEvent e)
    {
        var done = OnLive(e);
        if (done.Count > 0)
        {
            _triggerNote = $"{DateTime.Now:HH:mm} · {done[^1]}";
            Changed?.Invoke();
        }
    }
}
