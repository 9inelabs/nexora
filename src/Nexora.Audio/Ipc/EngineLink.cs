using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;
using Nexora.Audio.Plugins;
using Nexora.Audio.Record;
using Nexora.Audio.Show;

namespace Nexora.Audio.Ipc;

public static class EngineNames
{
    /// <summary>Unique per data folder, so two copies of NEXORA on one PC (for testing) keep apart.</summary>
    public static string For(string dataRoot) =>
        "nexora-audio-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot.ToLowerInvariant())))[..12].ToLowerInvariant();
}

/// <summary>
/// The engine's side of the link: any number of NEX-Audio screens on this PC can connect over a
/// local pipe. Nothing leaves the PC. If every screen closes, the sound keeps running.
/// </summary>
public sealed class EngineServer : IDisposable
{
    private readonly AudioEngine _engine;
    private readonly string _pipe;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Conn> _conns = [];
    private readonly Timer _meters, _status;
    private readonly float[] _in = new float[64], _out = new float[64];
    private readonly bool[] _inClip = new bool[64], _outClip = new bool[64];
    private int _statusDirty = 1;
    private int _deskDirty = 1;
    private int _meterTick;
    private DateTime _deskSent;

    public EngineServer(AudioEngine engine, string pipeName, Action<string>? log = null)
    {
        _engine = engine;
        _pipe = pipeName;
        _log = log ?? (_ => { });
        _engine.Changed += () => Interlocked.Exchange(ref _statusDirty, 1);
        if (_engine.Desk is { } desk)
        {
            desk.Changed += () => Interlocked.Exchange(ref _deskDirty, 1);
            desk.MixChanged += (path, value, rev) => Broadcast(new MixChangedMsg(path, value, rev));
            desk.MixReplaced += (mix, rev) => Broadcast(new MixStateMsg(mix, rev));
            desk.Notice += (text, error) => Broadcast(new NoticeMsg(text, error));
        }
        _ = Task.Run(AcceptLoop);
        _meters = new Timer(_ => SendMeters(), null, 33, 33);
        _status = new Timer(_ => SendStatus(false), null, 100, 100);
    }

    /// <summary>Someone pressed Shut down engine.</summary>
    public event Action? QuitRequested;

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var c = new Conn(pipe);
                lock (_conns) _conns.Add(c);
                _ = Task.Run(() => ReadLoop(c));
                await c.SendAsync(new StatusMsg(_engine.Status));
                if (_engine.Desk is { } d)
                {
                    await c.SendAsync(new MixStateMsg(d.Mix, d.Rev));
                    await c.SendAsync(new DeskStatusMsg(d.Status));
                }
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _log("Audio link: " + ex.Message);
                await Task.Delay(500);
            }
        }
    }

    private async Task ReadLoop(Conn c)
    {
        try
        {
            using var reader = new StreamReader(c.Pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (!_stop.IsCancellationRequested && await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                if (AudioMessage.FromLine(line) is not { } msg) continue;
                try
                {
                    await Handle(c, msg);
                }
                catch (Exception ex)
                {
                    await c.SendAsync(new NoticeMsg(ex.Message, true));
                }
                Interlocked.Exchange(ref _statusDirty, 1);
            }
        }
        catch (Exception) { }
        finally
        {
            lock (_conns) _conns.Remove(c);
            c.Dispose();
        }
    }

    private async Task Handle(Conn c, AudioMessage msg)
    {
        switch (msg)
        {
            case OpenDriverMsg o:
                await _engine.OpenAsync(o.Driver, o.BufferSize);
                break;
            case CloseDriverMsg:
                await _engine.CloseAsync();
                break;
            case SetPatchMsg p:
                _engine.SetPatch(p.Patch);
                break;
            case LatencyTestMsg l:
                _engine.StartLatencyTest(l.Output, l.Input);
                break;
            case DriverPanelMsg:
                await _engine.ShowDriverPanelAsync();
                break;
            case RefreshDriversMsg:
                _engine.RefreshDrivers();
                break;
            case ResetClipsMsg:
                _engine.ResetClips();
                break;
            case QuitMsg:
                await _engine.CloseAsync();
                QuitRequested?.Invoke();
                break;
            case SetMixMsg set when _engine.Desk is { } desk:
                if (!desk.Set(set.Path, set.Value)) await c.SendAsync(new NoticeMsg($"That setting could not be changed ({set.Path}).", true));
                break;
            case DeskCommandMsg cmd when _engine.Desk is { } desk:
                await DeskCommand(desk, cmd, c.SendAsync);
                Interlocked.Exchange(ref _deskDirty, 1);
                break;
        }
    }

    private static bool Bool(string? v) => v is "1" or "true" or "True";
    private static int Int(string? v, int fallback = 0) => int.TryParse(v, out var n) ? n : fallback;
    private static double Dbl(string? v, double fallback = 0) =>
        double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;

    /// <summary>The desk commands the screens can send.</summary>
    private static async Task DeskCommand(Desk desk, DeskCommandMsg m, Func<AudioMessage, Task> reply)
    {
        if (await ShowCommand(desk, m, reply)) return;
        switch (m.Cmd)
        {
            case "bus.add":
                if (!Enum.TryParse<StripKind>(m.Arg("kind"), true, out var kind)) throw new InvalidOperationException("Pick the kind of bus.");
                var opts = new NewBusOptions(
                    Enum.TryParse<SendTap>(m.Arg("tap"), true, out var tap) ? tap : null,
                    bool.TryParse(m.Arg("stereo"), out var stereo) ? stereo : null,
                    bool.TryParse(m.Arg("toMain"), out var toMain) ? toMain : null,
                    m.Arg("effect"), m.Arg("colour"));
                if (!desk.Change(s => MixParams.AddBus(s, kind, m.Arg("name"), opts) is not null, "add a bus"))
                    throw new InvalidOperationException("There is no room for another bus of that kind.");
                break;
            case "bus.remove":
                desk.Change(s => MixParams.RemoveBus(s, m.Arg("id") ?? ""), "remove a bus");
                break;
            case "rack.add":
            {
                var type = m.Arg("type") ?? "";
                RackItem item;
                if (type == "plugin")
                {
                    var plugin = m.Data is { } d ? d.Deserialize<PluginRef>(MixState.Json) : null;
                    if (plugin is null) throw new InvalidOperationException("Pick a plugin.");
                    item = new RackItem { Type = "plugin", Plugin = plugin };
                }
                else if (Effects.Get(type) is null) throw new InvalidOperationException("That effect is not known.");
                else item = Effects.Default(type, m.Arg("style"));
                string? why = null;
                if (!desk.Change(s =>
                    {
                        if (s.Find(m.Arg("strip") ?? "") is { } st && Effects.WhyNot(s, st, item.Type) is { } w)
                        {
                            why = w;
                            return false;
                        }
                        return MixParams.AddRackItem(s, m.Arg("strip") ?? "", item, Int(m.Arg("index"), -1));
                    }, "add an effect"))
                    throw new InvalidOperationException(why ?? "That rack is full (8 effects at most).");
                break;
            }
            case "rack.remove":
                desk.Change(s => MixParams.RemoveRackItem(s, m.Arg("strip") ?? "", m.Arg("item") ?? ""), "remove an effect");
                break;
            case "rack.move":
                desk.Change(s => MixParams.MoveRackItem(s, m.Arg("strip") ?? "", m.Arg("item") ?? "", Int(m.Arg("index"))), "move an effect");
                break;
            case "copy":
                desk.Change(s => MixParams.CopyProcessing(s, m.Arg("from") ?? "", m.Arg("to") ?? ""), $"copy to {m.Arg("to")}");
                break;
            case "paste":
            {
                var from = m.Data is { } pd ? pd.Deserialize<Strip>(MixState.Json) : null;
                if (from is null) throw new InvalidOperationException("Nothing has been copied yet.");
                desk.Change(s => MixParams.Paste(s, m.Arg("to") ?? "", from, m.Arg("parts") ?? "all"), $"paste on {m.Arg("to")}");
                break;
            }
            case "undo":
                if (!desk.Undo()) throw new InvalidOperationException("There is nothing to undo.");
                break;
            case "redo":
                if (!desk.Redo()) throw new InvalidOperationException("There is nothing to redo.");
                break;
            case "preset.save":
                desk.SavePreset(m.Arg("strip") ?? "", m.Arg("name") ?? "");
                break;
            case "preset.load":
                if (!desk.LoadPreset(m.Arg("strip") ?? "", m.Arg("id") ?? "", m.Arg("parts") ?? "all"))
                    throw new InvalidOperationException("That preset could not be found.");
                break;
            case "preset.delete":
                desk.DeletePreset(m.Arg("id") ?? "");
                break;
            case "routing.save":
                desk.SaveRouting(m.Arg("id"), m.Arg("name") ?? "", m.Arg("notes") ?? "", m.Arg("by") ?? "");
                break;
            case "routing.preview":
            {
                var p = desk.PreviewRouting(m.Arg("id") ?? "");
                await reply(new PreviewMsg(p, p is null ? "That routing layout could not be found." : null));
                break;
            }
            case "routing.recall":
                if (!desk.RecallRouting(m.Arg("id") ?? "")) throw new InvalidOperationException("That routing layout could not be found.");
                break;
            case "routing.delete":
                desk.DeleteRouting(m.Arg("id") ?? "");
                break;
            case "inputs.alt":
                desk.UseAlternateInputs(Bool(m.Arg("on")));
                break;
            case "mix.replace":
            {
                var mix = m.Data is { } d ? MixState.FromJson(d.GetRawText()) : null;
                if (mix is null) throw new InvalidOperationException("That mix could not be read.");
                desk.Change(s =>
                {
                    s.Channels = mix.Channels; s.Buses = mix.Buses; s.Main = mix.Main; s.Mono = mix.Mono; s.Stream = mix.Stream;
                    s.StreamSettings = mix.StreamSettings; s.Solo = mix.Solo; s.Outputs = mix.Outputs;
                    return true;
                });
                break;
            }
            case "solo.clear":
                desk.Change(s =>
                {
                    MixParams.ClearSolos(s);
                    return true;
                });
                break;
            case "solo.only":
                // Auto-solo on select: this strip (and its stereo partner) alone, apart from solo-safe strips.
                desk.Change(s =>
                {
                    if (s.Find(m.Arg("strip") ?? "") is not { Kind: not (StripKind.Main or StripKind.Stream) } st) return false;
                    MixParams.ClearSolos(s);
                    st.Solo = true;
                    if (ControlRules.Partner(s, st) is { } p) p.Solo = true;
                    return true;
                });
                break;
            case "control.add":
            {
                if (!Enum.TryParse<ControlMode>(m.Arg("mode"), true, out var mode)) mode = ControlMode.Vca;
                var members = (m.Arg("members") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!desk.Change(s => MixParams.AddControl(s, mode, m.Arg("name"), m.Arg("colour"), members) is not null, "add a control group"))
                    throw new InvalidOperationException($"All {MixState.MaxControls} control groups are in use.");
                break;
            }
            case "control.remove":
                desk.Change(s => MixParams.RemoveControl(s, m.Arg("id") ?? ""), "remove a control group");
                break;
            case "scene.save":
                await desk.SaveAsync(m.Arg("id"), m.Arg("name") ?? "", m.Arg("notes") ?? "", m.Arg("by") ?? "", Bool(m.Arg("snapshot")),
                    Enum.TryParse<RecallScope>(m.Arg("scope"), true, out var sc) ? sc : RecallScope.All, Dbl(m.Arg("fade")));
                break;
            case "scene.recall":
                if (!desk.Recall(m.Arg("id") ?? "", Bool(m.Arg("snapshot")),
                        Enum.TryParse<RecallScope>(m.Arg("scope"), true, out var rs) ? rs : null,
                        m.Arg("fade") is { } f ? Dbl(f) : null))
                    throw new InvalidOperationException("That scene could not be found.");
                break;
            case "scene.delete":
                desk.Delete(m.Arg("id") ?? "", Bool(m.Arg("snapshot")));
                break;
            case "snap.order":
                desk.ReorderSnapshots(m.Data is { } od ? od.Deserialize<List<string>>() ?? [] : []);
                break;
            case "snap.step":
                desk.StepSnapshot(Int(m.Arg("dir"), 1), m.Arg("current"));
                break;
            case "x32.connect":
                await desk.ConnectX32Async(m.Arg("host") ?? "");
                break;
            case "x32.disconnect":
                desk.DisconnectX32();
                break;
            case "x32.scenes":
                await desk.X32.RequestScenesAsync();
                break;
            case "desk.set":
            {
                var n = m.Data is { } d ? d.Deserialize<DeskSettings>(MixState.Json) : null;
                if (n is null) break;
                desk.Configure(old => old with
                {
                    ShadowMix = n.ShadowMix, FollowDesk = n.FollowDesk, LiveOutputHost = n.LiveOutputHost, Armed = n.Armed,
                    DeskScene = n.DeskScene, ConsoleScene = n.ConsoleScene, TimeoutMs = n.TimeoutMs,
                });
                break;
            }
            case "failsafe.handover":
                await desk.HandOverAsync("The engineer handed the room to the X32.");
                break;
            case "failsafe.return":
                await desk.ReturnAsync();
                break;
            case "plugins.scan":
                desk.ScanPlugins();
                break;
            case "plugin.editor":
                desk.ShowPluginEditor(m.Arg("item") ?? "");
                break;
            case "plugin.retry":
                desk.RetryPlugin(m.Arg("item") ?? "");
                break;
            case "analyse":
                desk.Analyse(m.Arg("strip"));
                break;
            case "loudness.reset":
                desk.ResetLoudness();
                break;
            default:
                throw new InvalidOperationException("Unknown desk command: " + m.Cmd);
        }
    }

    /// <summary>Show control and the recorder. Returns false for any other command.</summary>
    private static async Task<bool> ShowCommand(Desk desk, DeskCommandMsg m, Func<AudioMessage, Task> reply)
    {
        var by = m.Arg("by") ?? "";
        T Data<T>(string what) where T : class =>
            (m.Data is { } d ? d.Deserialize<T>(ShowState.Json) : null) ?? throw new InvalidOperationException($"The {what} could not be read.");
        switch (m.Cmd)
        {
            // Cue list
            case "cue.save":
                desk.SaveCue(Data<Cue>("cue"), Int(m.Arg("index"), -1));
                return true;
            case "cue.delete":
                desk.DeleteCue(m.Arg("id") ?? "");
                return true;
            case "cue.move":
                desk.MoveCue(m.Arg("id") ?? "", Int(m.Arg("dir"), 1));
                return true;
            case "cue.go":
                if (!desk.GoCue(m.Arg("id"))) throw new InvalidOperationException("That was the last cue. Press Back to the top to start again.");
                return true;
            case "cue.back":
                desk.BackCue();
                return true;
            case "cue.reset":
                desk.ResetCues();
                return true;
            case "cue.preview":
            {
                var p = desk.PreviewCue(m.Arg("id"));
                await reply(new PreviewMsg(p, p is null ? "There is no next cue." : null));
            }
                return true;
            case "scene.preview":
            {
                var p = desk.Preview(m.Arg("id") ?? "", Bool(m.Arg("snapshot")), Enum.TryParse<RecallScope>(m.Arg("scope"), true, out var sc) ? sc : null);
                await reply(new PreviewMsg(p, p is null ? "That scene could not be found." : null));
                return true;
            }

            // Macros
            case "macro.save":
                desk.SaveMacro(Data<Macro>("macro"));
                return true;
            case "macro.delete":
                desk.DeleteMacro(m.Arg("id") ?? "");
                return true;
            case "macro.move":
                desk.MoveMacro(m.Arg("id") ?? "", Int(m.Arg("dir"), 1));
                return true;
            case "macro.run":
                if (!await desk.RunMacroAsync(m.Arg("id") ?? "", by)) throw new InvalidOperationException("That macro could not be found.");
                return true;

            // Show files and backups
            case "show.save":
                desk.SaveShow(m.Arg("file") ?? "", m.Arg("name") ?? "", by);
                return true;
            case "show.inspect":
            {
                var f = m.Arg("file") ?? "";
                try { await reply(new ShowInspectMsg(f, desk.InspectShow(f))); }
                catch (InvalidOperationException ex) { await reply(new ShowInspectMsg(f, null, ex.Message)); }
                return true;
            }
            case "show.open":
                desk.OpenShow(m.Arg("file") ?? "", Bool(m.Arg("mix")), by);
                return true;
            case "show.new":
                desk.NewShow(m.Arg("name") ?? "");
                return true;
            case "backup.now":
                if (desk.BackupNow("by hand", by) is null) throw new InvalidOperationException("The backup could not be saved.");
                return true;
            case "backup.restore":
                desk.RestoreBackup(m.Arg("name") ?? "", Bool(m.Arg("mix")), by);
                return true;

            // Recorder and virtual soundcheck
            case "tape.record":
                desk.StartRecording(m.Arg("name"), by, m.Data is { } names ? names.Deserialize<List<string>>() : null);
                return true;
            case "tape.stop":
                if (desk.Tape.Recording) desk.Tape.StopRecording();
                else desk.Tape.Stop();
                return true;
            case "tape.load":
                desk.Tape.Load(m.Arg("id") ?? "");
                return true;
            case "tape.play":
                desk.Tape.Play(m.Arg("id"));
                return true;
            case "tape.pause":
                desk.Tape.Pause();
                return true;
            case "tape.seek":
                desk.Tape.Seek(Dbl(m.Arg("seconds")));
                return true;
            case "tape.playback":
                desk.Tape.SetPlayback(Bool(m.Arg("on")));
                return true;
            case "tape.live":
                desk.Tape.SetLive(Int(m.Arg("ch")), Bool(m.Arg("on")));
                return true;
            case "tape.arm":
                desk.Arm(Int(m.Arg("ch")), Bool(m.Arg("on")));
                return true;
            case "tape.set":
            {
                var n = Data<TapeSettings>("recorder settings");
                desk.Tape.Configure(old => old with { Folder = string.IsNullOrWhiteSpace(n.Folder) ? null : n.Folder.Trim(), Format = n.Format, Tap = n.Tap, RecordMain = n.RecordMain, Loop = n.Loop });
                return true;
            }
            case "tape.rename":
                desk.Tape.Rename(m.Arg("id") ?? "", m.Arg("name") ?? "");
                return true;
            case "tape.delete":
                desk.Tape.Delete(m.Arg("id") ?? "");
                return true;
            case "tape.import":
                desk.Tape.Import(m.Arg("folder") ?? "", m.Arg("name"));
                return true;
        }
        return false;
    }

    private void Broadcast(AudioMessage msg)
    {
        Conn[] conns;
        lock (_conns) conns = [.. _conns];
        foreach (var c in conns) _ = c.SendAsync(msg);
    }

    private void SendMeters()
    {
        Conn[] conns;
        lock (_conns) conns = [.. _conns];
        if (conns.Length == 0) return;
        if (!_engine.TakeMeters(_in, _out, _inClip, _outClip)) return;
        var st = _engine.Status;
        var msg = new MetersMsg(_in[..st.Inputs], _out[..st.Outputs], _inClip[..st.Inputs], _outClip[..st.Outputs]);
        foreach (var c in conns) _ = c.SendAsync(msg);
        if (_engine.Desk?.Engine is not { } mix) return;
        var tick = ++_meterTick;
        var m = mix.TakeMeters();
        var (ml, mr) = mix.MonitorLevel();
        var mm = new MixMetersMsg(
            m.Select(x => x.In).ToArray(), m.Select(x => x.L).ToArray(), m.Select(x => x.R).ToArray(),
            m.Select(x => x.Gate).ToArray(), m.Select(x => x.Comp).ToArray(), m.Select(x => x.Rack).ToArray(), m.Select(x => x.GateClosed).ToArray(),
            ml, mr, tick % 3 == 0 ? _engine.Desk.Loudness() : null, tick % 2 == 0 ? mix.Analyser.Bands() : null, m.Select(x => x.DynEq).ToArray(),
            tick % 10 == 0 ? mix.TakeCosts() : null);
        foreach (var c in conns) _ = c.SendAsync(mm);
    }

    private int _quietTicks;

    private void SendStatus(bool force)
    {
        // Every change at once (at most ten a second), and once a second anyway.
        var dirty = Interlocked.Exchange(ref _statusDirty, 0) == 1;
        if (!dirty && !force && ++_quietTicks < 10) return;
        _quietTicks = 0;
        Conn[] conns;
        lock (_conns) conns = [.. _conns];
        if (conns.Length == 0) return;
        var msg = new StatusMsg(_engine.Status);
        foreach (var c in conns) _ = c.SendAsync(msg);
        if (_engine.Desk is { } desk && (Interlocked.Exchange(ref _deskDirty, 0) == 1 || force || DateTime.UtcNow - _deskSent > TimeSpan.FromSeconds(2)))
        {
            _deskSent = DateTime.UtcNow;
            var dm = new DeskStatusMsg(desk.Status);
            foreach (var c in conns) _ = c.SendAsync(dm);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _meters.Dispose();
        _status.Dispose();
        lock (_conns)
        {
            foreach (var c in _conns) c.Dispose();
            _conns.Clear();
        }
    }

    private sealed class Conn(Stream pipe) : IDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        public Stream Pipe { get; } = pipe;

        public async Task SendAsync(AudioMessage msg)
        {
            // A slow screen skips meter frames rather than holding the engine up.
            if (msg is MetersMsg or MixMetersMsg && _write.CurrentCount == 0) return;
            await _write.WaitAsync();
            try
            {
                var bytes = Encoding.UTF8.GetBytes(msg.ToLine() + "\n");
                await Pipe.WriteAsync(bytes);
                await Pipe.FlushAsync();
            }
            catch (Exception) { }
            finally
            {
                _write.Release();
            }
        }

        public void Dispose()
        {
            try { Pipe.Dispose(); }
            catch (Exception) { }
        }
    }
}

/// <summary>The NEX-Audio screen's side of the link. Reconnects by itself if the engine restarts.</summary>
public sealed class EngineClient : IDisposable
{
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private NamedPipeClientStream? _conn;

    public EngineClient(string pipeName)
    {
        _pipe = pipeName;
        _ = Task.Run(Loop);
    }

    public bool Connected { get; private set; }
    public event Action<EngineStatus>? Status;
    public event Action<MetersMsg>? Meters;
    public event Action<NoticeMsg>? Notice;
    public event Action<bool>? ConnectionChanged;
    public event Action<MixStateMsg>? MixState;
    public event Action<MixChangedMsg>? MixChanged;
    public event Action<DeskStatus>? Desk;
    public event Action<MixMetersMsg>? MixMeters;
    public event Action<PreviewMsg>? Preview;
    public event Action<ShowInspectMsg>? ShowInfo;

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(1000, _stop.Token);
                _conn = pipe;
                SetConnected(true);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, leaveOpen: true);
                while (!_stop.IsCancellationRequested && await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    switch (AudioMessage.FromLine(line))
                    {
                        case StatusMsg s: Status?.Invoke(s.Status); break;
                        case MetersMsg m: Meters?.Invoke(m); break;
                        case NoticeMsg n: Notice?.Invoke(n); break;
                        case MixStateMsg ms: MixState?.Invoke(ms); break;
                        case MixChangedMsg mc: MixChanged?.Invoke(mc); break;
                        case DeskStatusMsg ds: Desk?.Invoke(ds.Desk); break;
                        case MixMetersMsg mm: MixMeters?.Invoke(mm); break;
                        case PreviewMsg pv: Preview?.Invoke(pv); break;
                        case ShowInspectMsg si: ShowInfo?.Invoke(si); break;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) { }
            finally
            {
                _conn = null;
                SetConnected(false);
                await pipe.DisposeAsync();
            }
            try { await Task.Delay(500, _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SetConnected(bool value)
    {
        if (Connected == value) return;
        Connected = value;
        ConnectionChanged?.Invoke(value);
    }

    public async Task<bool> SendAsync(AudioMessage msg)
    {
        var c = _conn;
        if (c is null) return false;
        await _write.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(msg.ToLine() + "\n");
            await c.WriteAsync(bytes);
            await c.FlushAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _conn?.Dispose(); }
        catch (Exception) { }
    }
}
