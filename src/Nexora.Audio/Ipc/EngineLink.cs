using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;
using Nexora.Audio.Plugins;
using Nexora.Audio.Record;
using Nexora.Audio.Remote;
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
/// local pipe. If every screen closes, the sound keeps running. With the admin remote switched on
/// (Remote tab, with a laptop PIN), NEX-Audio on a laptop can connect over the church network too and
/// gets exactly what this PC's own screen gets.
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

    // The admin remote: laptops over the church network.
    private readonly object _laptopGate = new();
    private readonly RemoteAccess.PinGuard _laptopPins = new();
    private TcpListener? _listener;
    private LaptopLink.Answerer? _answerer;
    private CancellationTokenSource? _listenStop;
    private (bool On, int Port, string Pin) _listening;
    private string? _laptopError;
    private int _laptopIds;

    public EngineServer(AudioEngine engine, string pipeName, Action<string>? log = null)
    {
        _engine = engine;
        _pipe = pipeName;
        _log = log ?? (_ => { });
        _engine.Changed += () => Interlocked.Exchange(ref _statusDirty, 1);
        if (_engine.Desk is { } desk)
        {
            desk.Changed += () =>
            {
                Interlocked.Exchange(ref _deskDirty, 1);
                ApplyLaptops();
            };
            desk.MixChanged += (path, value, rev) => Broadcast(new MixChangedMsg(path, value, rev));
            desk.MixReplaced += (mix, rev) => Broadcast(new MixStateMsg(mix, rev));
            desk.Notice += (text, error) => Broadcast(new NoticeMsg(text, error));
        }
        _ = Task.Run(AcceptLoop);
        _meters = new Timer(_ => SendMeters(), null, 33, 33);
        _status = new Timer(_ => SendStatus(false), null, 100, 100);
        ApplyLaptops();
    }

    /// <summary>Laptops on the admin remote now.</summary>
    public IReadOnlyList<LaptopClient> Laptops
    {
        get { lock (_conns) return _conns.Where(c => c.Laptop is not null).Select(c => c.Laptop!).ToList(); }
    }

    public bool LaptopsListening => _listener is not null;

    /// <summary>NEX-Audio's version on this PC (the engine is the same program), as laptops see it.</summary>
    public static string AppVersion { get; } =
        (System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];

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
                var c = new Conn(pipe, null);
                lock (_conns) _conns.Add(c);
                var reader = new StreamReader(c.Pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                _ = Task.Run(() => ReadLoop(c, reader));
                await SendEverything(c);
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

    /// <summary>What a screen gets the moment it connects: the engine, the whole mix and the desk.</summary>
    private async Task SendEverything(Conn c)
    {
        await c.SendAsync(new StatusMsg(_engine.Status));
        if (_engine.Desk is { } d)
        {
            await c.SendAsync(new MixStateMsg(d.Mix, d.Rev));
            await c.SendAsync(new DeskStatusMsg(d.Status));
        }
    }

    private async Task ReadLoop(Conn c, StreamReader reader)
    {
        try
        {
            using var rd = reader;
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
            bool laptop;
            lock (_conns) laptop = _conns.Remove(c) && c.Laptop is not null;
            c.Dispose();
            if (laptop)
            {
                _log($"Admin remote: {c.Laptop!.Name} ({c.Laptop.Address}) left.");
                _engine.Desk?.Note($"The laptop {c.Laptop.Name} left the admin remote.", EventLevel.Info);
                PublishLaptops();
            }
        }
    }

    private async Task Handle(Conn c, AudioMessage msg)
    {
        switch (msg)
        {
            case QuitMsg when c.Laptop is not null:
                await c.SendAsync(new NoticeMsg("The sound engine can only be shut down at the sound PC itself.", true));
                break;
            case DeskCommandMsg { Cmd: "laptop.kick" } k:
                Kick(k.Arg("id") is { } kid && int.TryParse(kid, out var n) ? n : -1);
                break;
            case DeskCommandMsg { Cmd: "file.put" or "file.get" } f when _engine.Desk is { } fd:
                await c.SendAsync(FileCommand(fd, f));
                break;
            case DeskCommandMsg { Cmd: "file.show" } fs when _engine.Desk is { } sd:
            {
                // Save the show for a laptop: kept on the sound PC too, and its contents sent back.
                var name = string.IsNullOrWhiteSpace(fs.Arg("name")) ? "Show" : fs.Arg("name")!.Trim();
                var path = RemoteShowPath(sd.Dir, name);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    sd.SaveShow(path, name, fs.Arg("by") ?? "");
                    await c.SendAsync(FileCommand(sd, new DeskCommandMsg("file.get", new() { ["id"] = fs.Arg("id") ?? "", ["path"] = path })));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    await c.SendAsync(new FileMsg(fs.Arg("id") ?? "", path, null, ex.Message));
                }
                Interlocked.Exchange(ref _deskDirty, 1);
                break;
            }
            case OpenDriverMsg o:
                await _engine.OpenAsync(o.Driver, o.BufferSize, o.SampleRate);
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
            case DeskCommandMsg { Cmd: "test.unplug" } t:
                _engine.PretendUnplug(Bool(t.Args?.GetValueOrDefault("on")));
                break;
            case DeskCommandMsg { Cmd: "diag.report" } when _engine.Desk is { } desk:
            {
                var now = DateTime.UtcNow;
                try
                {
                    var file = DiagReport.Write(desk.Dir, DiagReport.Text(_engine.Status, desk.Status, desk.Events.Latest(EventLog.Keep), now), now);
                    desk.Note("Diagnostics report saved: " + file, EventLevel.Info);
                    await c.SendAsync(new NoticeMsg("Report saved: " + file, false));
                }
                catch (Exception ex) { await c.SendAsync(new NoticeMsg("Could not save the report: " + ex.Message, true)); }
                Interlocked.Exchange(ref _deskDirty, 1);
                break;
            }
            case DeskCommandMsg cmd when _engine.Desk is { } desk:
                await DeskCommand(desk, cmd, c.SendAsync);
                Interlocked.Exchange(ref _deskDirty, 1);
                break;
        }
    }

    // ---- The admin remote: laptops over the church network ----------------------------------------

    /// <summary>Opens or closes the laptop port to match the Remote tab. A new PIN sends every laptop back to sign in again.</summary>
    private void ApplyLaptops()
    {
        if (_engine.Desk is not { } desk) return;
        var r = desk.Settings.Remote;
        var want = (On: r.Laptops && r.LaptopPin.Length >= 4, r.LaptopPort, Pin: r.LaptopPin);
        bool kick;
        lock (_laptopGate)
        {
            if (want == _listening || _stop.IsCancellationRequested) return;
            kick = want.Pin != _listening.Pin || !want.On;
            var portChanged = want.On != _listening.On || want.LaptopPort != _listening.Port;
            _listening = want;
            if (portChanged)
            {
                StopListening();
                _laptopError = null;
                if (want.On)
                {
                    try
                    {
                        var l = new TcpListener(IPAddress.Any, want.LaptopPort);
                        l.Start();
                        _listener = l;
                        _listenStop = new CancellationTokenSource();
                        try { _answerer = new LaptopLink.Answerer(want.LaptopPort, Me); }
                        catch (Exception ex) { _log("Admin remote: laptops cannot find this PC by themselves (" + ex.Message + "). Type its address instead."); }
                        _ = Task.Run(() => LaptopAcceptLoop(l, _listenStop.Token));
                        _log($"Admin remote: listening for laptops on port {want.LaptopPort}.");
                    }
                    catch (Exception ex)
                    {
                        StopListening();
                        _laptopError = $"Network port {want.LaptopPort} is already in use on this PC, so laptops cannot connect. Pick another port. ({ex.Message})";
                        _log("Admin remote: " + _laptopError);
                    }
                }
            }
        }
        if (kick) Kick(null);
        PublishLaptops();
    }

    private FoundDesk Me() => new("", _listening.Port, Environment.MachineName, AppVersion);

    private void StopListening()
    {
        _listenStop?.Cancel();
        _listenStop = null;
        try { _listener?.Stop(); } catch (Exception) { }
        _listener = null;
        _answerer?.Dispose();
        _answerer = null;
    }

    private void PublishLaptops() => _engine.Desk?.SetLaptops(Laptops, _listener is not null, _laptopError);

    /// <summary>Sends one laptop (or, with null, every laptop) away. It has to sign in again.</summary>
    private void Kick(int? id)
    {
        Conn[] gone;
        lock (_conns) gone = _conns.Where(c => c.Laptop is { } l && (id is null || l.Id == id)).ToArray();
        foreach (var c in gone)
            _ = Task.Run(async () =>
            {
                await c.SendAsync(new LaptopResultMsg(false, "The sound PC closed this laptop's link to the desk. Press Connect to sign in again."));
                await Task.Delay(300);
                c.Dispose();
            });
    }

    private async Task LaptopAcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested) return; await Task.Delay(200, CancellationToken.None); continue; }
            _ = Task.Run(() => Greet(tcp));
        }
    }

    /// <summary>A laptop knocked: it must sign the challenge with the laptop PIN within 10 seconds, or it is shut out.</summary>
    private async Task Greet(TcpClient tcp)
    {
        var address = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "?";
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();
            var challenge = LaptopLink.NewChallenge();
            async Task Say(AudioMessage m)
            {
                var bytes = Encoding.UTF8.GetBytes(m.ToLine() + "\n");
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await stream.WriteAsync(bytes, t.Token);
            }
            await Say(new LaptopHelloMsg(challenge, Environment.MachineName, AppVersion));
            var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            string? line;
            using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                line = await reader.ReadLineAsync(wait.Token);
            if (line is null || line.Length > 4096 || AudioMessage.FromLine(line) is not LaptopLoginMsg login) throw new InvalidOperationException("not a NEX-Audio laptop");
            var now = DateTime.UtcNow;
            var pin = _engine.Desk?.Settings.Remote is { Laptops: true } r ? r.LaptopPin : "";
            if (pin.Length < 4)
            {
                await Say(new LaptopResultMsg(false, "The admin remote is switched off on the sound PC."));
                throw new InvalidOperationException("switched off");
            }
            if (_laptopPins.Blocked(address, now))
            {
                await Say(new LaptopResultMsg(false, "Too many wrong PINs from this laptop. Wait a minute and try again."));
                throw new InvalidOperationException("blocked");
            }
            // The PinGuard counts misses; a right proof is passed as the PIN itself so it clears them.
            var ok = LaptopLink.Check(pin, challenge, login.Proof);
            if (!_laptopPins.Check(address, ok ? pin : "", pin, now))
            {
                await Say(new LaptopResultMsg(false, _laptopPins.Blocked(address, now) ? "Too many wrong PINs from this laptop. Wait a minute and try again." : "That laptop PIN is not right."));
                _engine.Desk?.Note($"A laptop at {address} tried the admin remote with the wrong PIN.", EventLevel.Warning);
                throw new InvalidOperationException("wrong PIN");
            }
            var who = new LaptopClient(Interlocked.Increment(ref _laptopIds), address, LaptopLink.SafeName(login.Laptop, "A laptop"),
                LaptopLink.SafeName(login.User, ""), LaptopLink.SafeName(login.Version, "?"), now);
            await Say(new LaptopResultMsg(true));
            var c = new Conn(stream, who);
            lock (_conns) _conns.Add(c);
            _log($"Admin remote: {who.Name} ({address}) connected{(who.User.Length > 0 ? " for " + who.User : "")}.");
            _engine.Desk?.Note($"The laptop {who.Name}{(who.User.Length > 0 ? $" ({who.User})" : "")} is running the desk from {address}.", EventLevel.Info);
            PublishLaptops();
            _ = Task.Run(async () =>
            {
                try { await ReadLoop(c, reader); }
                finally { tcp.Dispose(); }
            });
            await SendEverything(c);
        }
        catch (Exception)
        {
            tcp.Dispose();
        }
    }

    /// <summary>The largest file a laptop can send or fetch (a show with its drum samples, or one sample).</summary>
    public const int MaxFileBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Files over the admin remote. file.put keeps a show or a drum sample the laptop sent in the desk's
    /// "remote" folder and says where; file.get sends back a show saved there. Nothing outside that folder is read.
    /// </summary>
    private static FileMsg FileCommand(Desk desk, DeskCommandMsg m)
    {
        var id = m.Arg("id") ?? "";
        var dir = Path.GetFullPath(Path.Combine(desk.Dir, "remote"));
        try
        {
            if (m.Cmd == "file.put")
            {
                var kind = m.Arg("kind") is "drum" ? "drums" : "shows";
                var data = m.Data is { ValueKind: JsonValueKind.String } d ? Convert.FromBase64String(d.GetString()!) : throw new InvalidOperationException("The file did not arrive.");
                if (data.Length > MaxFileBytes) throw new InvalidOperationException("That file is too big to send (64 MB at most).");
                var folder = Path.Combine(dir, kind, DateTime.Now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, LaptopLink.SafeName(m.Arg("name"), "file"));
                File.WriteAllBytes(path, data);
                return new FileMsg(id, path);
            }
            var want = Path.GetFullPath(m.Arg("path") ?? "");
            if (!want.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only files the admin remote saved can be fetched.");
            var info = new FileInfo(want);
            if (!info.Exists) throw new InvalidOperationException("That file is not on the sound PC.");
            if (info.Length > MaxFileBytes) throw new InvalidOperationException("That file is too big to fetch (64 MB at most).");
            return new FileMsg(id, want, Convert.ToBase64String(File.ReadAllBytes(want)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or ArgumentException)
        {
            return new FileMsg(id, m.Arg("path") ?? "", null, ex.Message);
        }
    }

    /// <summary>Where a show saved from a laptop is kept on the sound PC before it is sent back to the laptop.</summary>
    public static string RemoteShowPath(string deskDir, string name) =>
        Path.Combine(deskDir, "remote", "shows", "saved", LaptopLink.SafeName(name, "Show") + ShowFiles.Extension);

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
                desk.SavePreset(m.Arg("strip") ?? "", m.Arg("name") ?? "", m.Arg("folder"));
                break;
            case "preset.move":
                if (!desk.MovePreset(m.Arg("id") ?? "", m.Arg("folder"), m.Arg("name")))
                    throw new InvalidOperationException("Only your own presets can be moved or renamed.");
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
            case "desk.offline":
                await desk.SetOfflineAsync(m.Arg("on") == "true");
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
            case "surface.set":
            {
                var n = m.Data is { } d ? d.Deserialize<DeskSettings>(MixState.Json) : null;
                if (n is null) break;
                desk.ConfigureSurfaces(n.X32Surface, n.MidiIn, n.MidiOut, n.Remote);
                break;
            }
            case "surface.select":
                desk.Select(m.Args?.GetValueOrDefault("strip"), fromSurface: false);
                break;
            case "midi.refresh":
                desk.RefreshMidi();
                break;
            case "trigger.save":
                if (m.Data?.Deserialize<ShowTrigger>(ShowState.Json) is { } trig) desk.SaveTrigger(trig);
                break;
            case "trigger.delete":
                desk.DeleteTrigger(m.Args?.GetValueOrDefault("id") ?? "");
                break;
            case "trigger.follow":
                desk.FollowNexora(Bool(m.Args?.GetValueOrDefault("on")));
                break;
            case "live":
                if (Enum.TryParse<TriggerWhen>(m.Args?.GetValueOrDefault("when"), out var when))
                    desk.Live(new LiveEvent(when, m.Args?.GetValueOrDefault("name") ?? ""));
                break;
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
            case "drums.import":
                desk.ImportDrums((m.Arg("files") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries), m.Arg("kit") ?? "");
                return true;
            case "drums.delete":
                desk.DeleteDrum(m.Arg("path") ?? "");
                return true;
            case "drums.learn":
                desk.LearnTrigger(m.Arg("strip") ?? "");
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
            tick % 10 == 0 ? mix.TakeCosts() : null,
            [mix.MainPhase.Value, mix.StreamPhase.Value],
            m.Select(x => x.Trigger).ToArray(), _engine.Desk.TriggerLive(mix.AnalyseId));
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
        lock (_laptopGate) StopListening();
        _meters.Dispose();
        _status.Dispose();
        lock (_conns)
        {
            foreach (var c in _conns) c.Dispose();
            _conns.Clear();
        }
    }

    private sealed class Conn(Stream pipe, LaptopClient? laptop) : IDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private int _meterSkip;
        public Stream Pipe { get; } = pipe;
        /// <summary>Set for a laptop on the admin remote; null for a screen on this PC.</summary>
        public LaptopClient? Laptop { get; } = laptop;

        public async Task SendAsync(AudioMessage msg)
        {
            if (msg is MetersMsg or MixMetersMsg)
            {
                // A slow screen skips meter frames rather than holding the engine up.
                if (_write.CurrentCount == 0) return;
                // Over the network, meters go 15 times a second rather than 30: just as smooth to the eye, half the Wi-Fi.
                // (The engine meters and the mix meters go together, so every other pair is skipped.)
                if (Laptop is not null)
                {
                    if (msg is MetersMsg) _meterSkip ^= 1;
                    if (_meterSkip == 1) return;
                }
            }
            await _write.WaitAsync();
            try
            {
                var bytes = Encoding.UTF8.GetBytes(msg.ToLine() + "\n");
                if (Laptop is null)
                {
                    await Pipe.WriteAsync(bytes);
                    await Pipe.FlushAsync();
                }
                else
                {
                    // A laptop whose Wi-Fi has gone quiet is let go after 10 seconds; it reconnects by itself.
                    using var late = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await Pipe.WriteAsync(bytes, late.Token);
                    await Pipe.FlushAsync(late.Token);
                }
            }
            catch (Exception)
            {
                if (Laptop is not null) Dispose();
            }
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

/// <summary>Where a NEX-Audio screen finds its engine: on this PC, or on a sound PC across the church network.</summary>
public sealed record EngineTarget(string? Pipe, string? Host = null, int Port = 0, string Pin = "", string Laptop = "", string User = "", string Version = "")
{
    public bool IsRemote => Host is not null;
    public static EngineTarget Local(string pipe) => new(pipe);
    public static EngineTarget Remote(string host, int port, string pin, string laptop, string user, string version) => new(null, host, port, pin, laptop, user, version);
}

/// <summary>
/// The NEX-Audio screen's side of the link. Reconnects by itself if the engine restarts, or (on the admin
/// remote) if the Wi-Fi drops. <see cref="Retarget"/> moves it between this PC's engine and a sound PC's.
/// </summary>
public sealed class EngineClient : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile EngineTarget _target;
    private Stream? _conn;
    private CancellationTokenSource _round = new();
    /// <summary>The sound PC said no (wrong PIN, sent away): wait for the user rather than knock again.</summary>
    private volatile bool _held;

    public EngineClient(string pipeName) : this(EngineTarget.Local(pipeName)) { }

    public EngineClient(EngineTarget target)
    {
        _target = target;
        _ = Task.Run(Loop);
    }

    public bool Connected { get; private set; }
    public EngineTarget Target => _target;
    /// <summary>The sound PC's name, once a laptop is in (admin remote).</summary>
    public string? RemoteName { get; private set; }
    /// <summary>Why the admin remote is not in just now (wrong PIN, cannot reach the PC…), or null.</summary>
    public string? Problem { get; private set; }
    /// <summary>The sound PC runs a different NEX-Audio version (it still connects, with a warning).</summary>
    public string? RemoteVersion { get; private set; }

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
    public event Action<FileMsg>? File;
    /// <summary>The admin remote's state changed: <see cref="Problem"/>, <see cref="RemoteName"/>.</summary>
    public event Action? LinkChanged;

    /// <summary>Drops the link now and connects to another engine (this PC's, or a sound PC's).</summary>
    public void Retarget(EngineTarget target)
    {
        _target = target;
        _held = false;
        Problem = null;
        RemoteName = null;
        RemoteVersion = null;
        LinkChanged?.Invoke();
        var old = _round;
        _round = new CancellationTokenSource();
        old.Cancel();
        try { _conn?.Dispose(); }
        catch (Exception) { }
    }

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var target = _target;
            var round = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _round.Token);
            Stream? stream = null;
            TcpClient? tcp = null;
            var wait = 500;
            if (_held && target.IsRemote)
            {
                try { await Task.Delay(Timeout.Infinite, round.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { }
                round.Dispose();
                continue;
            }
            try
            {
                StreamReader reader;
                if (target.IsRemote)
                {
                    tcp = new TcpClient { NoDelay = true };
                    using (var t = CancellationTokenSource.CreateLinkedTokenSource(round.Token))
                    {
                        t.CancelAfter(3000);
                        try { await tcp.ConnectAsync(target.Host!, target.Port, t.Token); }
                        catch (Exception) when (!round.IsCancellationRequested)
                        {
                            SetProblem($"Cannot reach {target.Host} on port {target.Port}. Check the sound PC is on, NEX-Audio's admin remote is switched on there, and both are on the church network.");
                            throw;
                        }
                    }
                    stream = tcp.GetStream();
                    reader = new StreamReader(stream, new UTF8Encoding(false), false, 65536, leaveOpen: true);
                    if (!await SignIn(stream, reader, target, round.Token))
                    {
                        wait = 5000;
                        throw new OperationCanceledException();
                    }
                }
                else
                {
                    var pipe = new NamedPipeClientStream(".", target.Pipe!, PipeDirection.InOut, PipeOptions.Asynchronous);
                    stream = pipe;
                    await pipe.ConnectAsync(1000, round.Token);
                    reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, leaveOpen: true);
                }
                _conn = stream;
                SetConnected(true);
                using (reader)
                    while (!round.IsCancellationRequested && await reader.ReadLineAsync(round.Token) is { } line)
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
                            case FileMsg f: File?.Invoke(f); break;
                            case LaptopResultMsg { Ok: false } no:
                                _held = true;
                                SetProblem(no.Error ?? "The sound PC closed the link.");
                                break;
                        }
                    }
                if (target.IsRemote && !round.IsCancellationRequested && !_held) SetProblem($"The link to {RemoteName ?? target.Host} dropped. Trying again…");
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                if (target.IsRemote && Connected && !round.IsCancellationRequested && !_held) SetProblem($"The link to {RemoteName ?? target.Host} dropped. Trying again…");
            }
            finally
            {
                _conn = null;
                SetConnected(false);
                if (stream is not null) await stream.DisposeAsync();
                tcp?.Dispose();
            }
            try { await Task.Delay(wait, round.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { }
            round.Dispose();
        }
    }

    /// <summary>Answers the sound PC's challenge with the laptop PIN. False (and <see cref="Problem"/>) when it says no.</summary>
    private async Task<bool> SignIn(Stream stream, StreamReader reader, EngineTarget t, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(10000);
        if (AudioMessage.FromLine(await reader.ReadLineAsync(wait.Token) ?? "") is not LaptopHelloMsg hello)
        {
            SetProblem($"{t.Host} answered, but it is not a NEX-Audio sound PC with the admin remote on (or it runs a much older NEX-Audio).");
            return false;
        }
        var login = new LaptopLoginMsg(Remote.LaptopLink.Proof(t.Pin, hello.Challenge), t.Laptop, t.User, t.Version);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(login.ToLine() + "\n"), wait.Token);
        if (AudioMessage.FromLine(await reader.ReadLineAsync(wait.Token) ?? "") is not LaptopResultMsg result)
        {
            SetProblem($"{hello.PcName} did not answer the sign-in.");
            return false;
        }
        if (!result.Ok)
        {
            _held = true;
            SetProblem(result.Error ?? "The sound PC said no.");
            return false;
        }
        RemoteName = hello.PcName;
        RemoteVersion = hello.Version.Length > 0 && t.Version.Length > 0 && hello.Version.Split('+')[0] != t.Version.Split('+')[0] ? hello.Version.Split('+')[0] : null;
        Problem = null;
        LinkChanged?.Invoke();
        return true;
    }

    private void SetProblem(string text)
    {
        Problem = text;
        LinkChanged?.Invoke();
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
