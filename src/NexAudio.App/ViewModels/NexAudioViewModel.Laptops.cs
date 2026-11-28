using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexAudio.App.Services;
using Nexora.Audio.Engine;
using Nexora.Audio.Ipc;
using Nexora.Audio.Remote;

namespace NexAudio.App.ViewModels;

/// <summary>
/// The admin remote, on the Remote page. On the sound PC: let laptops in, with their own PIN, and see who is on.
/// On a laptop: connect to the sound PC and run its whole desk from here, every page, exactly as at the desk.
/// </summary>
public sealed partial class NexAudioViewModel
{
    // ---- The sound PC: letting laptops in -------------------------------------------------------------

    [ObservableProperty] private bool _laptopsOn;
    [ObservableProperty] private string _laptopPin = "";
    [ObservableProperty] private string _laptopPort = LaptopLink.DefaultPort.ToString();
    [ObservableProperty] private string _laptopFirewallText = "";
    private bool _editingLaptops;
    public ObservableCollection<LaptopRow> LaptopRows { get; } = [];

    public bool LaptopsRunning => DeskStatus.LaptopsRunning;
    public bool HasLaptops => LaptopRows.Count > 0;

    public string LaptopsStateText => DeskStatus.LaptopsError
        ?? (DeskStatus.LaptopsRunning
            ? $"On. On the laptop, open NEX-Audio, go to Remote and press Find (or type {DeskAddress})."
            : LaptopsOn && DeskStatus.Settings.Remote.LaptopPin.Length < 4
                ? "Set a laptop PIN (4 to 8 digits) and press Save. No laptop can connect without one."
                : "Off. No laptop can reach this desk.");

    private string DeskAddress => DeskStatus.Addresses.Count > 0 ? string.Join(" or ", DeskStatus.Addresses) : DeskStatus.PcName;

    public string LaptopsListText => LaptopRows.Count == 0 ? "No laptops on the desk just now." : "";

    private void ApplyLaptops(DeskStatus d)
    {
        var r = d.Settings.Remote;
        LaptopsOn = r.Laptops;
        if (!_editingLaptops)
        {
            LaptopPin = r.LaptopPin;
            LaptopPort = r.LaptopPort.ToString();
        }
        var key = string.Join(",", d.Laptops.Select(l => l.Id));
        if (string.Join(",", LaptopRows.Select(l => l.Id)) != key)
        {
            LaptopRows.Clear();
            foreach (var l in d.Laptops) LaptopRows.Add(new LaptopRow(l, this));
        }
        foreach (var p in new[] { nameof(LaptopsRunning), nameof(LaptopsStateText), nameof(HasLaptops), nameof(LaptopsListText) })
            OnPropertyChanged(p);
    }

    partial void OnLaptopsOnChanged(bool value)
    {
        if (value && DeskStatus.Settings.Remote.LaptopPin.Length < 4 && new string((LaptopPin ?? "").Where(char.IsDigit).ToArray()).Length < 4)
            Notice = "Type a laptop PIN (4 to 8 digits) and press Save, then laptops can connect.";
        SaveSurfaces();
    }

    partial void OnLaptopPinChanged(string value) => _editingLaptops = true;
    partial void OnLaptopPortChanged(string value) => _editingLaptops = true;

    [RelayCommand]
    private void SaveLaptops()
    {
        var pin = new string((LaptopPin ?? "").Where(char.IsDigit).ToArray());
        if (pin.Length is > 0 and < 4 || pin.Length > 8)
        {
            Notice = "The laptop PIN needs 4 to 8 digits.";
            return;
        }
        if (pin.Length > 0 && pin == DeskStatus.Settings.Remote.Pin)
        {
            Notice = "Use a different PIN from the phones' engineer PIN, so a phone PIN can never open the whole desk on a laptop.";
            return;
        }
        LaptopPin = pin;
        _editingLaptops = false;
        SaveSurfaces();
    }

    internal void KickLaptop(int id) => _ = Command("laptop.kick", new() { ["id"] = id.ToString() });

    [RelayCommand]
    private async Task AllowLaptopFirewall()
    {
        var port = DeskStatus.Settings.Remote.LaptopPort;
        LaptopFirewallText = "Asking Windows…";
        var (_, msg) = await Task.Run(() => Firewall.AllowLaptops(port));
        LaptopFirewallText = msg;
    }

    // ---- A laptop: running another sound PC's desk ------------------------------------------------------

    [ObservableProperty] private string _deskHost = "";
    [ObservableProperty] private string _deskPin = "";
    [ObservableProperty] private string _deskPort = LaptopLink.DefaultPort.ToString();
    [ObservableProperty] private bool _findingDesks;
    [ObservableProperty] private string _findText = "Press Find to look for sound PCs on the church network, or type the address shown on the sound PC's Remote page.";
    public ObservableCollection<FoundDesk> FoundDesks { get; } = [];

    /// <summary>This screen is running another PC's desk (the admin remote), not this PC's own engine.</summary>
    public bool IsRemoteDesk => _engine.Target.IsRemote;
    public bool IsLocalDesk => !IsRemoteDesk;
    public string RemoteDeskName => _engine.RemoteName ?? _engine.Target.Host ?? "";
    public bool RemoteDeskLinked => IsRemoteDesk && EngineLinked;
    public bool RemoteDeskProblem => IsRemoteDesk && !EngineLinked && _engine.Problem is not null;

    public string RemoteDeskChip => !IsRemoteDesk ? "" : EngineLinked ? $"ADMIN REMOTE · {RemoteDeskName}" : $"ADMIN REMOTE · {RemoteDeskName} · NOT LINKED";

    public string RemoteDeskText
    {
        get
        {
            if (!IsRemoteDesk) return "Not connected. This screen runs this PC's own sound engine.";
            if (EngineLinked)
                return $"Running the desk on {RemoteDeskName}. Every page works as it does at the sound PC; the sound itself stays there."
                       + (_engine.RemoteVersion is { } v ? $" That PC runs NEX-Audio {v} and this laptop {DeskServices.Version}: update both to the same version." : "");
            return _engine.Problem ?? $"Connecting to {_engine.Target.Host}…";
        }
    }

    private void HookLaptops()
    {
        _engine.LinkChanged += () => Dispatcher.UIThread.Post(RaiseRemoteDesk);
        _engine.File += f => Dispatcher.UIThread.Post(() => OnFile(f));
        var l = LoadLocal();
        DeskHost = l.DeskHost ?? "";
        DeskPort = (l.DeskPort ?? LaptopLink.DefaultPort).ToString();
        DeskPin = l.DeskPin ?? "";
    }

    /// <summary>Where the screen connects at start-up: the sound PC last used from this laptop, or this PC's own engine.</summary>
    private static EngineTarget StartTarget()
    {
        var l = LoadLocal();
        return l.DeskHost is { Length: > 0 } h
            ? RemoteTarget(h, l.DeskPort ?? LaptopLink.DefaultPort, l.DeskPin ?? "")
            : EngineTarget.Local(AudioEngineHost.PipeName);
    }

    private static EngineTarget RemoteTarget(string host, int port, string pin) =>
        EngineTarget.Remote(host, port, pin, Environment.MachineName, Live.User?.Name ?? "", DeskServices.Version);

    private void RaiseRemoteDesk()
    {
        foreach (var p in new[]
                 {
                     nameof(IsRemoteDesk), nameof(IsLocalDesk), nameof(RemoteDeskLinked), nameof(RemoteDeskName), nameof(RemoteDeskProblem), nameof(RemoteDeskChip), nameof(RemoteDeskText),
                     nameof(StateText), nameof(MessageText), nameof(CheckNote),
                 })
            OnPropertyChanged(p);
    }

    [RelayCommand]
    private async Task FindDesks()
    {
        FindingDesks = true;
        FindText = "Looking for sound PCs on the church network…";
        FoundDesks.Clear();
        var port = int.TryParse(DeskPort, out var p) ? p : LaptopLink.DefaultPort;
        var found = await LaptopLink.FindAsync(port);
        foreach (var f in found.Where(f => !(f.Name == Environment.MachineName && IsLocalDesk && f.Address == "127.0.0.1"))) FoundDesks.Add(f);
        FindText = FoundDesks.Count switch
        {
            0 => "No sound PC answered. On the sound PC, switch on \"Laptops can run the whole desk\" on the Remote page and let laptops in through Windows Firewall, or type its address.",
            1 => $"Found {FoundDesks[0].Name}. Type the laptop PIN and press Connect.",
            _ => $"Found {FoundDesks.Count} sound PCs. Pick one.",
        };
        if (FoundDesks.Count == 1) UseDesk(FoundDesks[0]);
        FindingDesks = false;
    }

    [RelayCommand]
    private void UseDesk(FoundDesk d)
    {
        DeskHost = d.Address;
        DeskPort = d.Port.ToString();
    }

    [RelayCommand]
    private void ConnectDesk()
    {
        var host = DeskHost.Trim();
        var pin = new string((DeskPin ?? "").Where(char.IsDigit).ToArray());
        if (host.Length == 0)
        {
            Notice = "Type the sound PC's address, or press Find.";
            return;
        }
        if (pin.Length < 4)
        {
            Notice = "Type the laptop PIN set on the sound PC's Remote page (4 to 8 digits).";
            return;
        }
        var port = int.TryParse(DeskPort, out var p) && p is >= 1024 and <= 65535 ? p : LaptopLink.DefaultPort;
        SaveLocal(LoadLocal() with { DeskHost = host, DeskPort = port, DeskPin = pin });
        _ = SwitchTo(RemoteTarget(host, port, pin));
    }

    /// <summary>Back to this PC's own sound engine.</summary>
    [RelayCommand]
    private void DisconnectDesk()
    {
        SaveLocal(LoadLocal() with { DeskHost = null });
        S.Audio.Start();
        _ = SwitchTo(EngineTarget.Local(AudioEngineHost.PipeName));
    }

    private async Task SwitchTo(EngineTarget target)
    {
        // Anything still waiting to go to the old desk is dropped; the new desk sends its whole picture on connect.
        Flush();
        await Task.Yield();
        _engine.Retarget(target);
        _x32Linked = false;
        _mixReceived = false;
        RaiseRemoteDesk();
    }

    private bool _x32Linked;

    /// <summary>On a laptop: link to the same X32 as the sound PC (for the channel names), without touching the engine's own link.</summary>
    private void LinkRemoteX32(DeskStatus d)
    {
        if (!IsRemoteDesk || _x32Linked || d.Settings.X32Host is not { Length: > 0 } host) return;
        _x32Linked = true;
        if (_x32.Connected && string.Equals(_x32.Address, host, StringComparison.OrdinalIgnoreCase)) return;
        X32Address = host;
        _ = ConnectConsole();
    }

    // ---- Files over the admin remote --------------------------------------------------------------------

    private readonly Dictionary<string, TaskCompletionSource<FileMsg>> _files = [];
    private int _fileIds;

    private void OnFile(FileMsg f)
    {
        if (_files.Remove(f.Id, out var wait)) wait.TrySetResult(f);
    }

    private async Task<FileMsg?> FileRequest(string cmd, Dictionary<string, string> args, object? data = null, int seconds = 60)
    {
        var id = "f" + Interlocked.Increment(ref _fileIds);
        args["id"] = id;
        var wait = new TaskCompletionSource<FileMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
        _files[id] = wait;
        var el = data is null ? (System.Text.Json.JsonElement?)null : System.Text.Json.JsonSerializer.SerializeToElement(data);
        if (!await _engine.SendAsync(new DeskCommandMsg(cmd, args, el)))
        {
            _files.Remove(id);
            Notice = $"Not linked to {RemoteDeskName} just now.";
            return null;
        }
        var done = await Task.WhenAny(wait.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        _files.Remove(id);
        if (done != wait.Task)
        {
            Notice = $"{RemoteDeskName} did not answer in time.";
            return null;
        }
        if (wait.Task.Result.Error is { } e)
        {
            Notice = e;
            return null;
        }
        return wait.Task.Result;
    }

    /// <summary>Sends a file from this laptop to the sound PC. Its path there, or null (and a notice).</summary>
    private async Task<string?> SendFile(string localPath, string kind)
    {
        try
        {
            var info = new FileInfo(localPath);
            if (info.Length > EngineServer.MaxFileBytes)
            {
                Notice = $"{info.Name} is too big to send (64 MB at most).";
                return null;
            }
            var bytes = await File.ReadAllBytesAsync(localPath);
            var r = await FileRequest("file.put", new() { ["name"] = info.Name, ["kind"] = kind }, Convert.ToBase64String(bytes), 120);
            return r?.Path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = "Could not read the file: " + ex.Message;
            return null;
        }
    }

    /// <summary>Show, Save: on a laptop the show is saved on the sound PC (a copy stays there) and brought back here.</summary>
    private async Task SaveShowToLaptop(string file)
    {
        if (!CanMix) return;
        Notice = $"Saving the show on {RemoteDeskName}…";
        var name = Path.GetFileNameWithoutExtension(file);
        var r = await FileRequest("file.show", new() { ["name"] = name, ["by"] = Live.User?.Name ?? "" }, null, 120);
        if (r?.Data is null) return;
        try
        {
            if (!file.EndsWith(Nexora.Audio.Show.ShowFiles.Extension, StringComparison.OrdinalIgnoreCase)) file += Nexora.Audio.Show.ShowFiles.Extension;
            await File.WriteAllBytesAsync(file, Convert.FromBase64String(r.Data));
            Notice = $"Saved the show to {file} on this laptop. A copy is kept on {RemoteDeskName} too.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = "Could not save it on this laptop: " + ex.Message;
        }
    }

    /// <summary>Show, Open: on a laptop the file goes to the sound PC first, then opens there like any show.</summary>
    private async Task InspectShowFromLaptop(string file)
    {
        if (!CanMix) return;
        Notice = $"Sending the show to {RemoteDeskName}…";
        if (await SendFile(file, "show") is { } there)
        {
            Notice = "";
            _ = Command("show.inspect", new() { ["file"] = there });
        }
    }

    private async Task ImportDrumsFromLaptop(IReadOnlyList<string> files)
    {
        var there = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            Notice = $"Sending sample {i + 1} of {files.Count} to {RemoteDeskName}…";
            if (await SendFile(files[i], "drum") is { } p) there.Add(p);
            else return;
        }
        Notice = $"Adding {there.Count} sample{(there.Count == 1 ? "" : "s")} to \"{Nexora.Audio.Drums.DrumLibrary.Clean(ImportKit)}\"…";
        await Command("drums.import", new() { ["files"] = string.Join('|', there), ["kit"] = ImportKit });
    }

    /// <summary>The PC check looks at the PC this screen is on.</summary>
    public string CheckNote => IsRemoteDesk
        ? $"This checks this laptop, not {RemoteDeskName}. To check the sound PC, run the check at the sound PC itself."
        : "";
}

/// <summary>A laptop on the admin remote, as the sound PC lists it.</summary>
public sealed class LaptopRow(LaptopClient l, NexAudioViewModel owner)
{
    public int Id { get; } = l.Id;
    public string Name { get; } = l.User.Length > 0 ? $"{l.Name} · {l.User}" : l.Name;
    public string Detail { get; } = $"{l.Address} · since {l.SinceUtc.ToLocalTime():HH:mm} · NEX-Audio {l.Version}";
    public IRelayCommand KickCommand { get; } = new RelayCommand(() => owner.KickLaptop(l.Id));
}
