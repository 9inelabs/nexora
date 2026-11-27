using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexAudio.App.Services;
using Nexora.Audio.Engine;
using Nexora.Audio.Ipc;
using Nexora.Audio.Mix;
using Nexora.Audio.Remote;
using Nexora.Audio.Show;
using Nexora.Core.Net;

namespace NexAudio.App.ViewModels;

/// <summary>The Remote page: the X32 as a surface, a MIDI fader box, tablets and phones, and NEXORA's triggers.</summary>
public sealed partial class NexAudioViewModel
{
    private const string NoPort = "";
    private bool _applyingSurfaces;
    private long _seenSelectSeq;
    private string _surfaceKey = "";

    [ObservableProperty] private bool _x32SurfaceOn;
    public ObservableCollection<Choice> MidiInChoices { get; } = [];
    public ObservableCollection<Choice> MidiOutChoices { get; } = [];
    [ObservableProperty] private Choice? _midiIn;
    [ObservableProperty] private Choice? _midiOut;
    [ObservableProperty] private bool _remoteOn;
    [ObservableProperty] private string _remotePort = RemoteSettings.DefaultPort.ToString();
    [ObservableProperty] private string _remotePin = "";
    [ObservableProperty] private string _firewallText = "";
    public ObservableCollection<RemoteMixRow> RemoteMixRows { get; } = [];

    public string X32SurfaceText => !DeskStatus.X32Connected
        ? "Link the X32 first (Setup > X32 link)."
        : X32SurfaceOn
            ? $"{DeskStatus.X32Name ?? "The X32"} is a control surface: faders, mutes, pans, SELECT and SOLO work NEX-Audio, and its motor faders follow."
            : "Off. The X32 only follows NEX-Audio (the shadow mix).";

    public string MidiStateText => DeskStatus.MidiText
        ?? (DeskStatus.MidiInputs.Count == 0 ? "No MIDI fader box is plugged in. Plug it in by USB, then press Look again." : "Pick the fader box below.");
    public bool MidiConnected => DeskStatus.MidiConnected;

    public string RemoteAddressText => DeskStatus.RemoteRunning
        ? string.Join("   ", (DeskStatus.Addresses.Count > 0 ? DeskStatus.Addresses : ["this-pc"]).Select(a => $"http://{a}:{DeskStatus.Settings.Remote.Port}"))
        : "";
    public bool RemoteRunning => DeskStatus.RemoteRunning;
    public string RemoteStateText => DeskStatus.RemoteError
        ?? (DeskStatus.RemoteRunning ? "On. Open this address on a phone or tablet on the church Wi-Fi:" : RemoteOn ? "Starting…" : "Off. Nobody can reach the desk from a phone or tablet.");
    public string RemoteClientsText => DeskStatus.RemoteClients.Count == 0
        ? "No phones or tablets on the remote just now."
        : string.Join("\n", DeskStatus.RemoteClients.Select(c => c.Role switch
        {
            RemoteRole.Engineer => $"{c.Address} · the whole desk",
            RemoteRole.Musician => $"{c.Address} · {(c.Mix is { } m && _mix.Find(m) is { } s ? NameOf(s) : "a mix")}",
            _ => $"{c.Address} · choosing",
        }));
    public bool RemoteHasMixes => RemoteMixRows.Count > 0;

    private void ApplySurfaces(DeskStatus d)
    {
        _applyingSurfaces = true;
        var s = d.Settings;
        X32SurfaceOn = s.X32Surface;
        var key = string.Join("|", d.MidiInputs) + "#" + string.Join("|", d.MidiOutputs);
        if (key != _surfaceKey)
        {
            _surfaceKey = key;
            MidiInChoices.Clear();
            MidiOutChoices.Clear();
            MidiInChoices.Add(new(NoPort, "No fader box"));
            MidiOutChoices.Add(new(NoPort, "Nothing (no motor faders or lights)"));
            foreach (var p in d.MidiInputs) MidiInChoices.Add(new(p, p));
            foreach (var p in d.MidiOutputs) MidiOutChoices.Add(new(p, p));
            // A box set up before but not plugged in now still shows, so the setting is not lost.
            if (s.MidiIn is { } i && !d.MidiInputs.Contains(i)) MidiInChoices.Add(new(i, i + " (not plugged in)"));
            if (s.MidiOut is { } o && !d.MidiOutputs.Contains(o)) MidiOutChoices.Add(new(o, o + " (not plugged in)"));
        }
        MidiIn = MidiInChoices.FirstOrDefault(c => c.Value == (s.MidiIn ?? NoPort));
        MidiOut = MidiOutChoices.FirstOrDefault(c => c.Value == (s.MidiOut ?? NoPort));
        RemoteOn = s.Remote.On;
        if (!_editingRemote)
        {
            RemotePort = s.Remote.Port.ToString();
            RemotePin = s.Remote.Pin;
        }
        BuildRemoteMixes(s.Remote);
        ApplyTriggers(d);
        _applyingSurfaces = false;

        // A press of SELECT on the X32 or the fader box: the screen follows.
        if (d.SurfaceSelectSeq != _seenSelectSeq)
        {
            var first = _seenSelectSeq == 0;
            _seenSelectSeq = d.SurfaceSelectSeq;
            if (!first && d.SurfaceSelected is { } sel && sel != SelectedId && _mix.Find(sel) is not null) SelectStrip(sel, false);
        }
        foreach (var p in new[]
                 {
                     nameof(X32SurfaceText), nameof(MidiStateText), nameof(MidiConnected), nameof(RemoteAddressText), nameof(RemoteRunning),
                     nameof(RemoteStateText), nameof(RemoteClientsText), nameof(RemoteHasMixes), nameof(TriggerNote), nameof(HasTriggers),
                 })
            OnPropertyChanged(p);
    }

    private void BuildRemoteMixes(RemoteSettings r)
    {
        var mixes = _mix.BusesOf(StripKind.Mix).OrderBy(m => m.Number).ToList();
        if (RemoteMixRows.Select(x => x.Id + x.Name).SequenceEqual(mixes.Select(m => m.Id + NameOf(m))))
        {
            foreach (var row in RemoteMixRows) row.Set(r.Mixes.Contains(row.Id));
            return;
        }
        RemoteMixRows.Clear();
        foreach (var m in mixes) RemoteMixRows.Add(new RemoteMixRow(m.Id, $"Mix {m.Number}", NameOf(m), r.Mixes.Contains(m.Id), this));
    }

    private bool _editingRemote;

    internal void SaveSurfaces()
    {
        if (_applyingSurfaces || _applyingDesk || !CanMix) return;
        var r = DeskStatus.Settings.Remote with
        {
            On = RemoteOn,
            Port = int.TryParse(RemotePort, out var port) ? port : RemoteSettings.DefaultPort,
            Pin = RemotePin ?? "",
            Mixes = RemoteMixRows.Where(x => x.IsOn).Select(x => x.Id).ToList(),
        };
        var s = DeskStatus.Settings with
        {
            X32Surface = X32SurfaceOn,
            MidiIn = MidiIn is { Value.Length: > 0 } i ? i.Value : null,
            MidiOut = MidiOut is { Value.Length: > 0 } o ? o.Value : null,
            Remote = r,
        };
        _ = Command("surface.set", null, s);
    }

    partial void OnX32SurfaceOnChanged(bool value) => SaveSurfaces();
    partial void OnMidiInChanged(Choice? value) => SaveSurfaces();
    partial void OnMidiOutChanged(Choice? value) => SaveSurfaces();
    partial void OnRemoteOnChanged(bool value) => SaveSurfaces();
    partial void OnRemotePinChanged(string value) => _editingRemote = true;
    partial void OnRemotePortChanged(string value) => _editingRemote = true;

    [RelayCommand]
    private void SaveRemote()
    {
        var pin = new string((RemotePin ?? "").Where(char.IsDigit).ToArray());
        if (pin.Length is > 0 and < 4)
        {
            Notice = "The remote's PIN needs 4 to 8 digits (or leave it empty: then only musicians' mixes are on the remote).";
            return;
        }
        RemotePin = pin;
        _editingRemote = false;
        SaveSurfaces();
    }

    [RelayCommand]
    private void RefreshMidi() => _ = Command("midi.refresh");

    [RelayCommand]
    private async Task AllowRemoteFirewall()
    {
        var port = DeskStatus.Settings.Remote.Port;
        FirewallText = "Asking Windows…";
        var (_, msg) = await Task.Run(() => Firewall.AllowRemote(port));
        FirewallText = msg;
    }

    /// <summary>The screen selected a strip: the X32's SELECT light and the fader box follow.</summary>
    private void TellSurfaces(string id) => _ = Command("surface.select", new() { ["strip"] = id });

    // ---- Triggers from NEXORA ---------------------------------------------------------------------

    public ObservableCollection<TriggerRow> TriggerRows { get; } = [];
    public TriggerForm TriggerEdit => _triggerEdit ??= new TriggerForm(this);
    private TriggerForm? _triggerEdit;
    private string _triggerKey = "";
    [ObservableProperty] private bool _followNexora;
    private bool _applyingTriggers;

    public bool HasTriggers => TriggerRows.Count > 0;
    public string TriggerNote => DeskStatus.TriggerNote
        ?? (Live.IsConnected ? "Linked to the Live Output PC. Nothing has fired yet." : "Not linked to the Live Output PC just now, so nothing can fire.");

    private void ApplyTriggers(DeskStatus d)
    {
        _applyingTriggers = true;
        FollowNexora = d.Show.FollowNexora;
        _applyingTriggers = false;
        var key = System.Text.Json.JsonSerializer.Serialize(d.Show.Triggers, ShowState.Json) + string.Join(",", d.Snapshots.Select(x => x.Id + x.Name))
                  + string.Join(",", d.Show.Cues.Select(c => c.Id + c.Name)) + string.Join(",", d.Show.Macros.Select(c => c.Id + c.Name));
        if (key == _triggerKey) return;
        _triggerKey = key;
        TriggerRows.Clear();
        foreach (var t in d.Show.Triggers) TriggerRows.Add(new TriggerRow(t, this));
        TriggerEdit.Refresh();
    }

    partial void OnFollowNexoraChanged(bool value)
    {
        if (_applyingTriggers || !CanMix) return;
        _ = Command("trigger.follow", new() { ["on"] = value ? "true" : "false" });
    }

    internal string TriggerText(ShowTrigger t)
    {
        var when = t.When switch
        {
            TriggerWhen.SongStarts => t.Match.Length > 0 ? $"When a song with \"{t.Match}\" in its title goes up" : "When any song goes up",
            TriggerWhen.SectionLive => $"When {SectionName(t.Match)} goes up",
            _ => "When the screen is cleared (black, logo or nothing up)",
        };
        var what = t.Action switch
        {
            TriggerAction.Snapshot => $"recall the snapshot {SnapshotName(t.Target)}",
            TriggerAction.Cue => $"go to the cue {Show.Cues.FirstOrDefault(c => c.Id == t.Target)?.Name ?? "(deleted)"}",
            TriggerAction.NextCue => "go to the next cue",
            _ => $"run the macro {MacroName(t.Target)}",
        };
        return $"{when}, {what}.";
    }

    internal static string SectionName(string? s) => s switch
    {
        "Scripture" => "Scripture",
        "Announcements" => "an announcement",
        "LowerThirds" => "a lower third",
        "Countdown" => "the countdown",
        "Media" => "a video or picture",
        "MinisterTimer" => "the minister timer",
        _ => "anything",
    };

    internal void SaveTrigger(ShowTrigger t) => _ = Command("trigger.save", null, t);
    internal void DeleteTrigger(string id) => _ = Command("trigger.delete", new() { ["id"] = id });

    // ---- Watching the screens -----------------------------------------------------------------------

    private NexoraClient? _watched;
    private readonly LiveWatch _liveWatch = new();

    /// <summary>Once a second: makes sure NEX-Audio is listening to the Live Output PC's screens.</summary>
    private void WatchNexora()
    {
        var c = Live.Client;
        if (ReferenceEquals(c, _watched)) return;
        if (_watched is not null) _watched.SnapshotChanged -= OnNexoraSnapshot;
        _watched = c;
        _liveWatch.Reset();
        if (c is not null) c.SnapshotChanged += OnNexoraSnapshot;
    }

    private void OnNexoraSnapshot()
    {
        if (_watched?.Snapshot is not { } snap) return;
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var e in _liveWatch.Next(snap))
                _ = Command("live", new() { ["when"] = e.When.ToString(), ["name"] = e.Name });
        });
    }
}

/// <summary>One mix that musicians may open on their phones.</summary>
public sealed partial class RemoteMixRow(string id, string number, string name, bool on, NexAudioViewModel owner) : ObservableObject
{
    private bool _setting;
    public string Id { get; } = id;
    public string Number { get; } = number;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isOn = on;

    internal void Set(bool on)
    {
        _setting = true;
        IsOn = on;
        _setting = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_setting) owner.SaveSurfaces();
    }
}

public sealed class TriggerRow(ShowTrigger t, NexAudioViewModel owner)
{
    public ShowTrigger Trigger { get; } = t;
    public string Text { get; } = owner.TriggerText(t);
    public bool On
    {
        get => Trigger.On;
        set
        {
            Trigger.On = value;
            owner.SaveTrigger(Trigger);
        }
    }
    public IRelayCommand DeleteCommand { get; } = new RelayCommand(() => owner.DeleteTrigger(t.Id));
}

/// <summary>The form for a new trigger.</summary>
public sealed partial class TriggerForm : ObservableObject
{
    private readonly NexAudioViewModel _owner;

    public TriggerForm(NexAudioViewModel owner)
    {
        _owner = owner;
        _when = WhenChoices[0];
        _action = ActionChoices[0];
        _section = SectionChoices[0];
    }

    public IReadOnlyList<Choice> WhenChoices { get; } =
    [
        new(nameof(TriggerWhen.SongStarts), "A song goes up"),
        new(nameof(TriggerWhen.SectionLive), "Another section goes up"),
        new(nameof(TriggerWhen.ScreenClear), "The screen is cleared"),
    ];
    public IReadOnlyList<Choice> SectionChoices { get; } =
    [
        new("Scripture", "Scripture"), new("Announcements", "Announcements"), new("LowerThirds", "Lower thirds"),
        new("Countdown", "Countdown"), new("Media", "Video or picture"), new("MinisterTimer", "Minister timer"),
    ];
    public IReadOnlyList<Choice> ActionChoices { get; } =
    [
        new(nameof(TriggerAction.Snapshot), "Recall a snapshot"),
        new(nameof(TriggerAction.Cue), "Go to a cue"),
        new(nameof(TriggerAction.NextCue), "Go to the next cue"),
        new(nameof(TriggerAction.Macro), "Run a macro"),
    ];
    public ObservableCollection<Choice> Targets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSong), nameof(IsSection))]
    private Choice _when;
    [ObservableProperty] private Choice _section;
    [ObservableProperty] private string _song = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    private Choice _action;
    [ObservableProperty] private Choice? _target;

    public bool IsSong => When.Value == nameof(TriggerWhen.SongStarts);
    public bool IsSection => When.Value == nameof(TriggerWhen.SectionLive);
    public bool HasTarget => Action.Value != nameof(TriggerAction.NextCue);

    partial void OnActionChanged(Choice value) => Refresh();

    public void Refresh()
    {
        var d = _owner.DeskStatus;
        var keep = Target?.Value;
        Targets.Clear();
        IEnumerable<Choice> list = Action.Value switch
        {
            nameof(TriggerAction.Snapshot) => d.Snapshots.Select(s => new Choice(s.Id, s.Name)),
            nameof(TriggerAction.Cue) => d.Show.Cues.Select((c, i) => new Choice(c.Id, $"{i + 1}. {c.Name}")),
            nameof(TriggerAction.Macro) => d.Show.Macros.Select(m => new Choice(m.Id, m.Name)),
            _ => [],
        };
        foreach (var c in list) Targets.Add(c);
        Target = Targets.FirstOrDefault(t => t.Value == keep) ?? Targets.FirstOrDefault();
    }

    [RelayCommand]
    private void Add()
    {
        if (HasTarget && Target is null)
        {
            _owner.Notice = "Pick what it should do first (save a snapshot, cue or macro if there are none yet).";
            return;
        }
        var t = new ShowTrigger
        {
            When = Enum.Parse<TriggerWhen>(When.Value),
            Match = IsSong ? Song.Trim() : IsSection ? Section.Value : "",
            Action = Enum.Parse<TriggerAction>(Action.Value),
            Target = HasTarget ? Target?.Value : null,
        };
        _owner.SaveTrigger(t);
        Song = "";
    }
}
