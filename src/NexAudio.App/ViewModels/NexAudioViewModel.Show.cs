using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Audio.Dsp;
using Nexora.Audio.Engine;
using Nexora.Audio.Ipc;
using Nexora.Audio.Mix;
using Nexora.Audio.Show;

namespace NexAudio.App.ViewModels;

/// <summary>The Show page: the cue list with its big Go button, macros, show files and backups, and the recall preview.</summary>
public sealed partial class NexAudioViewModel
{
    public ObservableCollection<CueRow> CueRows { get; } = [];
    public ObservableCollection<MacroButton> MacroButtons { get; } = [];
    public ObservableCollection<BackupRow> BackupRows { get; } = [];
    private string _showKey = "", _backupKey = "";

    public PreviewForm RecallPreview { get; } = new();
    public CueForm CueEdit { get; private set; } = null!;
    public MacroForm MacroEdit { get; private set; } = null!;
    public ShowOpenForm ShowOpen { get; } = new();

    private void HookShow()
    {
        CueEdit = new CueForm(this);
        MacroEdit = new MacroForm(this);
        _engine.Preview += p => Dispatcher.UIThread.Post(() => OnPreview(p));
        _engine.ShowInfo += i => Dispatcher.UIThread.Post(() => OnShowInfo(i));
    }

    private ShowState Show => DeskStatus.Show;

    private void ApplyShow(DeskStatus d)
    {
        var s = d.Show;
        var key = System.Text.Json.JsonSerializer.Serialize(s, ShowState.Json) + "|" + string.Join(",", d.Snapshots.Select(x => x.Id + x.Name)) + string.Join(",", d.Scenes.Select(x => x.Id + x.Name));
        if (key != _showKey)
        {
            _showKey = key;
            CueRows.Clear();
            var current = s.CurrentIndex;
            for (var i = 0; i < s.Cues.Count; i++) CueRows.Add(new CueRow(s.Cues[i], i + 1, i == current, i == current + 1, this));
            MacroButtons.Clear();
            foreach (var m in s.Macros) MacroButtons.Add(new MacroButton(m, this));
            CueEdit.Refresh();
            MacroEdit.Refresh();
        }
        var bk = string.Join("|", d.Backups.Select(b => b.File));
        if (bk != _backupKey)
        {
            _backupKey = bk;
            BackupRows.Clear();
            foreach (var b in d.Backups.Take(20)) BackupRows.Add(new BackupRow(b, this));
        }
        foreach (var p in new[]
                 {
                     nameof(CurrentCueText), nameof(NextCueText), nameof(NextCueNotes), nameof(HasNextCue), nameof(NoCues), nameof(ShowNameText),
                     nameof(ShowFileText), nameof(BackupText), nameof(MacroRunningText), nameof(HasMacros), nameof(CueCountText), nameof(HasHeaderCue),
                 })
            OnPropertyChanged(p);
    }

    // ---- Names for the screen ----------------------------------------------------------------------

    /// <summary>"CH15 · Lead vox" for a preview line, using the X32's names.</summary>
    internal string StripLabel(string id, string fallback) =>
        _mix.Find(id) is { } s ? (s.Kind == StripKind.Channel ? $"{s.Id.ToUpperInvariant()} · {NameOf(s)}" : NameOf(s)) : fallback;

    internal string SnapshotName(string? id) => DeskStatus.Snapshots.FirstOrDefault(s => s.Id == id)?.Name ?? "(deleted)";
    internal string SceneName(string? id) => DeskStatus.Scenes.FirstOrDefault(s => s.Id == id)?.Name ?? "(deleted)";
    internal string MacroName(string? id) => Show.Macros.FirstOrDefault(m => m.Id == id)?.Name ?? "(deleted)";

    internal string RecallText(Cue c)
    {
        var parts = new List<string>();
        if (c.SnapshotId is { Length: > 0 }) parts.Add("Snapshot: " + SnapshotName(c.SnapshotId));
        else if (c.SceneId is { Length: > 0 }) parts.Add("Scene: " + SceneName(c.SceneId));
        if (c.Scope is { } sc) parts.Add(ScopeText(sc));
        if (c.Fade is { } f) parts.Add(f > 0 ? $"fade {f:0.#} s" : "no fade");
        if (c.MacroId is { Length: > 0 }) parts.Add("Macro: " + MacroName(c.MacroId));
        return parts.Count == 0 ? "A note only: nothing changes" : string.Join(" · ", parts);
    }

    public string CurrentCueText => Show.CurrentIndex >= 0 ? $"{Show.CurrentIndex + 1}. {Show.Cues[Show.CurrentIndex].Name}" : "Not started";
    public string NextCueText => Show.Next is { } n ? $"{Show.CurrentIndex + 2}. {n.Name}" : Show.Cues.Count == 0 ? "No cues yet" : "End of the list";
    public string NextCueNotes => Show.Next is { } n ? (n.Notes.Length > 0 ? n.Notes + "\n" : "") + RecallText(n) : "";
    public bool HasNextCue => Show.Next is not null;
    public bool NoCues => Show.Cues.Count == 0;
    public bool HasHeaderCue => Show.Cues.Count > 0;
    public string CueCountText => Show.Cues.Count == 1 ? "1 cue" : $"{Show.Cues.Count} cues";
    public bool HasMacros => Show.Macros.Count > 0;
    public string MacroRunningText => DeskStatus.RunningMacro is { } m ? $"Running {m}…" : "";

    public string ShowNameText => Show.Name.Length > 0 ? Show.Name : "Untitled show";

    public string ShowFileText => DeskStatus.Settings.ShowFile is { } f
        ? $"Last saved or opened: {f}"
        : "Not saved as a show file yet. NEX-Audio still keeps everything on this PC and backs it up.";

    public string BackupText => DeskStatus.LastBackupUtc == DateTime.MinValue
        ? "No backup yet."
        : $"Last backup {Ago(DeskStatus.LastBackupUtc)}. NEX-Audio backs up the whole desk every 10 minutes while it changes, and before opening a show.";

    internal static string Ago(DateTime utc)
    {
        var t = DateTime.UtcNow - utc;
        if (t.TotalMinutes < 1) return "just now";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min ago";
        var local = utc.ToLocalTime();
        return t.TotalDays < 1 ? $"at {local:HH:mm}" : $"on {local:ddd d MMM, HH:mm}";
    }

    // ---- Cue list ---------------------------------------------------------------------------------

    [RelayCommand] private Task GoCue() => Command("cue.go");
    [RelayCommand] private Task BackCue() => Command("cue.back");
    [RelayCommand] private Task ResetCues() => Command("cue.reset");
    [RelayCommand] private Task PreviewNextCue() => Command("cue.preview");

    [RelayCommand]
    private void AddCue() => CueEdit.Open(null);

    internal void GoTo(Cue c) => _ = Command("cue.go", new() { ["id"] = c.Id });
    internal void PreviewCue(Cue c) => _ = Command("cue.preview", new() { ["id"] = c.Id });
    internal void MoveCue(Cue c, int dir) => _ = Command("cue.move", new() { ["id"] = c.Id, ["dir"] = dir.ToString(CultureInfo.InvariantCulture) });
    internal void DeleteCue(Cue c) => _ = Command("cue.delete", new() { ["id"] = c.Id });
    internal void EditCue(Cue c) => CueEdit.Open(c);
    internal void SaveCue(Cue c) => _ = Command("cue.save", null, c);

    // ---- Macros -----------------------------------------------------------------------------------

    [RelayCommand]
    private void AddMacro() => MacroEdit.Open(null);

    internal void RunMacro(Macro m) => _ = Command("macro.run", new() { ["id"] = m.Id, ["by"] = Live.User?.Name ?? "" });
    internal void EditMacro(Macro m) => MacroEdit.Open(m);
    internal void SaveMacro(Macro m) => _ = Command("macro.save", null, m);
    internal void DeleteMacro(string id) => _ = Command("macro.delete", new() { ["id"] = id });

    /// <summary>F1 to F12 run the macro given that key.</summary>
    public bool RunMacroKey(int key)
    {
        if (Show.Macros.FirstOrDefault(m => m.Key == key) is not { } m) return false;
        RunMacro(m);
        return true;
    }

    // ---- Recall preview -----------------------------------------------------------------------------

    private Action? _afterPreview;

    internal void PreviewScene(string id, bool snapshot)
    {
        _afterPreview = () => RecallScene(id, snapshot);
        _ = Command("scene.preview", new() { ["id"] = id, ["snapshot"] = snapshot ? "true" : "false" });
    }

    private void OnPreview(PreviewMsg m)
    {
        if (m.Preview is not { } p)
        {
            Notice = m.Error ?? "Nothing to preview.";
            return;
        }
        if (p.CueId is { } cue) _afterPreview = () => _ = Command("cue.go", new() { ["id"] = cue });
        RecallPreview.Show(p, this, _afterPreview);
        _afterPreview = null;
    }

    // ---- Show files and backups -----------------------------------------------------------------------

    /// <summary>The show page's Save button picked a file.</summary>
    public void SaveShowTo(string file) => _ = Command("show.save", new() { ["file"] = file, ["name"] = Path.GetFileNameWithoutExtension(file), ["by"] = Live.User?.Name ?? "" });

    /// <summary>The show page's Open button picked a file: look inside it first.</summary>
    public void InspectShow(string file) => _ = Command("show.inspect", new() { ["file"] = file });

    private void OnShowInfo(ShowInspectMsg m)
    {
        if (m.Contents is not { } c)
        {
            Notice = m.Error ?? "That show file could not be opened.";
            return;
        }
        ShowOpen.Show(m.File, c, null, this);
    }

    internal void OpenShowFile(string file, bool takeMix) =>
        _ = Command("show.open", new() { ["file"] = file, ["mix"] = takeMix ? "true" : "false", ["by"] = Live.User?.Name ?? "" });

    internal void RestoreBackup(BackupInfo b, bool takeMix) =>
        _ = Command("backup.restore", new() { ["name"] = b.File, ["mix"] = takeMix ? "true" : "false", ["by"] = Live.User?.Name ?? "" });

    internal void AskRestore(BackupInfo b) => ShowOpen.Show(b.File, null, b, this);

    [RelayCommand] private Task BackupNow() => Command("backup.now", new() { ["by"] = Live.User?.Name ?? "" });

    [ObservableProperty] private string _newShowName = "";

    [RelayCommand]
    private Task NewShow()
    {
        var name = NewShowName.Trim();
        NewShowName = "";
        return Command("show.new", new() { ["name"] = name.Length > 0 ? name : $"Service {DateTime.Now:d MMM}" });
    }

    /// <summary>Strips for macro steps: every channel and bus by name.</summary>
    internal IReadOnlyList<Choice> StripChoices() =>
        _mix.AllStrips().Where(s => s.Kind != StripKind.Stream).Select(s => new Choice(s.Id, $"{s.Id.ToUpperInvariant()} · {NameOf(s)}")).ToList();

    internal IReadOnlyList<Choice> BusChoices() =>
        _mix.Buses.Where(b => b.Kind is StripKind.Mix or StripKind.Fx or StripKind.Matrix).Select(b => new Choice(b.Id, NameOf(b))).ToList();
}

// ---- Rows and forms ----------------------------------------------------------------------------------

public sealed class CueRow(Cue cue, int number, bool current, bool next, NexAudioViewModel owner)
{
    public Cue Cue => cue;
    public string Number => number.ToString(CultureInfo.InvariantCulture);
    public string Name => cue.Name;
    public string Notes => cue.Notes;
    public bool HasNotes => cue.Notes.Length > 0;
    public string Recall => owner.RecallText(cue);
    public bool IsCurrent => current;
    public bool IsNext => next;
    public IRelayCommand GoCommand { get; } = new RelayCommand(() => owner.GoTo(cue));
    public IRelayCommand PreviewCommand { get; } = new RelayCommand(() => owner.PreviewCue(cue));
    public IRelayCommand EditCommand { get; } = new RelayCommand(() => owner.EditCue(cue));
    public IRelayCommand UpCommand { get; } = new RelayCommand(() => owner.MoveCue(cue, -1));
    public IRelayCommand DownCommand { get; } = new RelayCommand(() => owner.MoveCue(cue, 1));
    public IRelayCommand DeleteCommand { get; } = new RelayCommand(() => owner.DeleteCue(cue));
}

public sealed class MacroButton(Macro m, NexAudioViewModel owner)
{
    public string Name => m.Name;
    public string KeyText => m.Key > 0 ? $"F{m.Key}" : "";
    public bool HasKey => m.Key > 0;
    public string StepsText => m.Steps.Count == 1 ? "1 step" : $"{m.Steps.Count} steps";
    public IBrush Tone => new SolidColorBrush(Color.TryParse(m.Colour, out var c) ? c : Color.Parse("#A78BFA"));
    public IBrush Tint => new SolidColorBrush(Color.TryParse(m.Colour, out var c) ? Color.FromArgb(0x2A, c.R, c.G, c.B) : Color.Parse("#2AA78BFA"));
    public IRelayCommand RunCommand { get; } = new RelayCommand(() => owner.RunMacro(m));
    public IRelayCommand EditCommand { get; } = new RelayCommand(() => owner.EditMacro(m));
}

public sealed class BackupRow(BackupInfo b, NexAudioViewModel owner)
{
    public string When => b.SavedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture);
    public string Reason => b.Reason switch
    {
        "auto" => "Every 10 minutes",
        "start" => "When NEX-Audio started",
        "before opening" => "Before a show was opened",
        "before new show" => "Before a new show",
        "by hand" => "Saved by hand",
        var r => r,
    };
    public string Size => b.Bytes < 1024 * 1024 ? $"{b.Bytes / 1024.0:0} KB" : $"{b.Bytes / 1048576.0:0.0} MB";
    public IRelayCommand RestoreCommand { get; } = new RelayCommand(() => owner.AskRestore(b));
}

/// <summary>"Recall Sermon?": the list of what would change, then Recall or Cancel.</summary>
public sealed partial class PreviewForm : ObservableObject
{
    private Action? _go;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _safeText = "";
    [ObservableProperty] private string _goText = "Recall now";
    public ObservableCollection<RecallChange> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    public bool NoRows => Rows.Count == 0;
    public bool HasSafe => SafeText.Length > 0;
    public bool CanGo => _go is not null;

    public void Show(RecallPreview p, NexAudioViewModel owner, Action? go)
    {
        _go = go;
        Rows.Clear();
        foreach (var r in p.Changes) Rows.Add(r with { Strip = owner.StripLabel(r.StripId, r.Strip) });
        var kind = p.CueId is not null ? "cue" : p.Snapshot ? "snapshot" : "scene";
        Title = p.CueId is not null ? $"Cue: {p.Name}" : $"Recall the {kind} \"{p.Name}\"?";
        Subtitle = p.Scope == RecallScope.None
            ? "This cue recalls nothing; it only runs its macro or shows its note."
            : $"Changes {NexAudioViewModel.ScopeText(p.Scope)}" + (p.Fade > 0.05 ? $" · levels glide over {p.Fade:0.#} s" : "") +
              $" · {(p.Strips == 1 ? "1 strip" : $"{p.Strips} strips")} change" + (p.Changes.Count >= 400 ? " (first 400 shown)" : "");
        SafeText = p.KeptSafe.Count == 0 ? "" : $"Kept as they are (recall safe): {string.Join(", ", p.KeptSafe)}.";
        GoText = p.CueId is not null ? "Go to this cue" : "Recall now";
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(NoRows));
        OnPropertyChanged(nameof(HasSafe));
        OnPropertyChanged(nameof(CanGo));
        IsOpen = true;
    }

    [RelayCommand]
    private void Go()
    {
        IsOpen = false;
        _go?.Invoke();
        _go = null;
    }

    [RelayCommand]
    private void Cancel()
    {
        IsOpen = false;
        _go = null;
    }
}

/// <summary>Before a show file or backup is opened: what is in it, and whether to take its mix too.</summary>
public sealed partial class ShowOpenForm : ObservableObject
{
    private NexAudioViewModel? _owner;
    private string _file = "";
    private BackupInfo? _backup;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";

    public void Show(string file, ShowContents? c, BackupInfo? backup, NexAudioViewModel owner)
    {
        _owner = owner;
        _file = file;
        _backup = backup;
        if (c is not null)
        {
            Title = $"Open \"{c.Manifest.Name}\"?";
            var by = c.Manifest.SavedBy.Length > 0 ? $" by {c.Manifest.SavedBy}" : "";
            Detail = $"Saved {c.Manifest.SavedUtc.ToLocalTime():ddd d MMM yyyy, HH:mm}{by}. " +
                     $"{(c.HasMix ? $"A mix with {N(c.Channels, "channel")} and {N(c.Buses, "bus", "buses")}, " : "No mix, ")}" +
                     $"{N(c.Scenes, "scene")}, {N(c.Snapshots, "snapshot")}, {N(c.Cues, "cue")}, {N(c.Macros, "macro")} and {N(c.Presets, "preset")}. " +
                     "This desk's scenes, snapshots, cues, macros and presets are replaced. A backup of the desk as it is now is saved first.";
        }
        else if (backup is not null)
        {
            Title = $"Go back to the backup from {backup.SavedUtc.ToLocalTime():ddd d MMM, HH:mm}?";
            Detail = "This desk's scenes, snapshots, cues, macros and presets go back to how they were then. A backup of the desk as it is now is saved first.";
        }
        IsOpen = true;
    }

    private static string N(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";

    [RelayCommand] private void TakeAll() => Do(true);
    [RelayCommand] private void KeepMix() => Do(false);

    private void Do(bool takeMix)
    {
        IsOpen = false;
        if (_owner is null) return;
        if (_backup is not null) _owner.RestoreBackup(_backup, takeMix);
        else _owner.OpenShowFile(_file, takeMix);
    }

    [RelayCommand] private void Cancel() => IsOpen = false;
}

/// <summary>Adding or changing a cue.</summary>
public sealed partial class CueForm(NexAudioViewModel owner) : ObservableObject
{
    private Cue? _editing;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private Choice? _recall;
    [ObservableProperty] private Choice? _macro;
    [ObservableProperty] private Choice? _scope;
    [ObservableProperty] private bool _ownFade;
    [ObservableProperty] private double _fade;
    public ObservableCollection<Choice> Recalls { get; } = [];
    public ObservableCollection<Choice> Macros { get; } = [];
    public string Heading => _editing is null ? "New cue" : "Change cue";

    public static IReadOnlyList<Choice> Scopes { get; } =
    [
        new("", "As the snapshot was saved"), new("All", "Everything"), new("Levels", "Levels only"), new("Levels, Processing", "Levels and processing"),
        new("Levels, Effects", "Levels and effects"),
    ];

    public void Refresh()
    {
        var r = Recall?.Value;
        var m = Macro?.Value;
        Recalls.Clear();
        Recalls.Add(new Choice("", "Nothing (a note, or a macro only)"));
        foreach (var s in owner.DeskStatus.Snapshots) Recalls.Add(new Choice("snap:" + s.Id, "Snapshot: " + s.Name));
        foreach (var s in owner.DeskStatus.Scenes) Recalls.Add(new Choice("scene:" + s.Id, "Scene: " + s.Name));
        Macros.Clear();
        Macros.Add(new Choice("", "No macro"));
        foreach (var x in owner.DeskStatus.Show.Macros) Macros.Add(new Choice(x.Id, x.Name));
        Recall = Recalls.FirstOrDefault(c => c.Value == r) ?? Recalls[0];
        Macro = Macros.FirstOrDefault(c => c.Value == m) ?? Macros[0];
    }

    public void Open(Cue? c)
    {
        _editing = c;
        Refresh();
        Name = c?.Name ?? "";
        Notes = c?.Notes ?? "";
        var want = c is null ? "" : c.SnapshotId is { Length: > 0 } s ? "snap:" + s : c.SceneId is { Length: > 0 } sc ? "scene:" + sc : "";
        if (c is null && owner.DeskStatus.Snapshots.Count > 0)
        {
            // A new cue suggests the next snapshot not yet in the list.
            var used = owner.DeskStatus.Show.Cues.Select(x => x.SnapshotId).ToHashSet();
            want = owner.DeskStatus.Snapshots.FirstOrDefault(x => !used.Contains(x.Id)) is { } next ? "snap:" + next.Id : "";
            if (want.Length > 0) Name = owner.DeskStatus.Snapshots.First(x => "snap:" + x.Id == want).Name;
        }
        Recall = Recalls.FirstOrDefault(x => x.Value == want) ?? Recalls[0];
        Macro = Macros.FirstOrDefault(x => x.Value == (c?.MacroId ?? "")) ?? Macros[0];
        Scope = Scopes.FirstOrDefault(x => x.Value == (c?.Scope?.ToString() ?? "")) ?? Scopes[0];
        OwnFade = c?.Fade is not null;
        Fade = c?.Fade ?? 0;
        OnPropertyChanged(nameof(Heading));
        IsOpen = true;
    }

    [RelayCommand]
    private void Save()
    {
        var c = _editing is null ? new Cue() : new Cue { Id = _editing.Id };
        var r = Recall?.Value ?? "";
        c.SnapshotId = r.StartsWith("snap:", StringComparison.Ordinal) ? r[5..] : null;
        c.SceneId = r.StartsWith("scene:", StringComparison.Ordinal) ? r[6..] : null;
        c.MacroId = Macro?.Value is { Length: > 0 } m ? m : null;
        c.Scope = Enum.TryParse<RecallScope>(Scope?.Value, out var sc) && Scope?.Value.Length > 0 ? sc : null;
        c.Fade = OwnFade ? Math.Round(Fade, 1) : null;
        c.Notes = Notes.Trim();
        c.Name = Name.Trim().Length > 0 ? Name.Trim() : Recall is { Value.Length: > 0 } x ? x.Label[(x.Label.IndexOf(':') + 2)..] : "Cue";
        owner.SaveCue(c);
        IsOpen = false;
    }

    [RelayCommand] private void Cancel() => IsOpen = false;
}

/// <summary>Making a macro: a name, a colour, an F key and a list of steps.</summary>
public sealed partial class MacroForm(NexAudioViewModel owner) : ObservableObject
{
    private string? _id;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private Choice? _colour;
    [ObservableProperty] private Choice? _key;
    public ObservableCollection<MacroStepRow> Steps { get; } = [];
    public bool IsExisting => _id is not null;
    public string Heading => _id is null ? "New macro" : "Change macro";

    public static IReadOnlyList<Choice> Colours { get; } =
    [
        new("#A78BFA", "Violet"), new("#6CB4F5", "Blue"), new("#4FD1C5", "Teal"), new("#9BD66B", "Green"), new("#F2D06B", "Yellow"),
        new("#F59E5B", "Orange"), new("#FF6B6B", "Red"), new("#F472B6", "Pink"),
    ];

    public static IReadOnlyList<Choice> Keys { get; } = [new("0", "No key"), .. Enumerable.Range(1, 12).Select(i => new Choice(i.ToString(CultureInfo.InvariantCulture), $"F{i}"))];

    internal IReadOnlyList<Choice> StripChoices = [];
    internal IReadOnlyList<Choice> BusChoices = [];
    internal IReadOnlyList<Choice> TargetChoices = [];

    public void Refresh()
    {
        StripChoices = owner.StripChoices();
        BusChoices = owner.BusChoices();
        TargetChoices =
        [
            .. owner.DeskStatus.Snapshots.Select(s => new Choice("snap:" + s.Id, "Snapshot: " + s.Name)),
            .. owner.DeskStatus.Scenes.Select(s => new Choice("scene:" + s.Id, "Scene: " + s.Name)),
        ];
    }

    public void Open(Macro? m)
    {
        Refresh();
        _id = m?.Id;
        Name = m?.Name ?? "";
        Colour = Colours.FirstOrDefault(c => c.Value.Equals(m?.Colour, StringComparison.OrdinalIgnoreCase)) ?? Colours[owner.DeskStatus.Show.Macros.Count % Colours.Count];
        Key = Keys.FirstOrDefault(k => k.Value == (m?.Key ?? 0).ToString(CultureInfo.InvariantCulture)) ?? Keys[0];
        Steps.Clear();
        foreach (var s in m?.Steps ?? []) Steps.Add(new MacroStepRow(this, s));
        if (Steps.Count == 0) Steps.Add(new MacroStepRow(this, new MacroStep { Action = MacroAction.Mute }));
        OnPropertyChanged(nameof(IsExisting));
        OnPropertyChanged(nameof(Heading));
        IsOpen = true;
    }

    [RelayCommand]
    private void AddStep()
    {
        if (Steps.Count < 32) Steps.Add(new MacroStepRow(this, new MacroStep { Action = MacroAction.Mute }));
    }

    internal void Remove(MacroStepRow r) => Steps.Remove(r);

    internal void Move(MacroStepRow r, int dir)
    {
        var i = Steps.IndexOf(r);
        var j = i + dir;
        if (i < 0 || j < 0 || j >= Steps.Count) return;
        Steps.Move(i, j);
    }

    [RelayCommand]
    private void Save()
    {
        var m = new Macro
        {
            Id = _id ?? Guid.NewGuid().ToString("n")[..8],
            Name = Name.Trim().Length > 0 ? Name.Trim() : "Macro",
            Colour = Colour?.Value ?? "#A78BFA",
            Key = int.TryParse(Key?.Value, out var k) ? k : 0,
            Steps = Steps.Select(s => s.ToStep()).ToList(),
        };
        owner.SaveMacro(m);
        IsOpen = false;
    }

    [RelayCommand]
    private void Delete()
    {
        if (_id is not null) owner.DeleteMacro(_id);
        IsOpen = false;
    }

    [RelayCommand] private void Cancel() => IsOpen = false;
}

public sealed partial class MacroStepRow : ObservableObject
{
    private readonly MacroForm _form;

    public MacroStepRow(MacroForm form, MacroStep s)
    {
        _form = form;
        _action = Actions.FirstOrDefault(a => a.Value == s.Action.ToString()) ?? Actions[0];
        var first = s.Strip?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        _strip = Strips.FirstOrDefault(c => c.Value == first) ?? Strips.FirstOrDefault();
        _bus = Buses.FirstOrDefault(c => c.Value == s.Bus) ?? Buses.FirstOrDefault();
        var t = s.Action == MacroAction.Scene ? "scene:" + s.Target : "snap:" + s.Target;
        _target = Targets.FirstOrDefault(c => c.Value == t) ?? Targets.FirstOrDefault();
        _value = s.Action == MacroAction.Fader ? Math.Max(-60, s.Value) : s.Value;
        _extra = s.Strip?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList() ?? [];
    }

    private readonly List<string> _extra;

    public static IReadOnlyList<Choice> Actions { get; } =
    [
        new(nameof(MacroAction.Mute), "Mute"), new(nameof(MacroAction.Unmute), "Unmute"), new(nameof(MacroAction.Fader), "Set a fader"),
        new(nameof(MacroAction.SendOn), "Turn a send on"), new(nameof(MacroAction.SendOff), "Turn a send off"),
        new(nameof(MacroAction.Snapshot), "Recall a snapshot or scene"), new(nameof(MacroAction.NextCue), "Go to the next cue"),
        new(nameof(MacroAction.StartRecording), "Start recording"), new(nameof(MacroAction.StopRecording), "Stop recording"),
        new(nameof(MacroAction.PlaybackOn), "Play the recording into the channels"), new(nameof(MacroAction.LiveInputs), "Back to the live inputs"),
        new(nameof(MacroAction.ClearSolo), "Clear every solo"), new(nameof(MacroAction.Wait), "Wait"),
    ];

    public IReadOnlyList<Choice> Strips => _form.StripChoices;
    public IReadOnlyList<Choice> Buses => _form.BusChoices;
    public IReadOnlyList<Choice> Targets => _form.TargetChoices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsStrip), nameof(NeedsBus), nameof(NeedsTarget), nameof(NeedsDb), nameof(NeedsSeconds), nameof(ValueText))]
    private Choice _action;

    [ObservableProperty] private Choice? _strip;
    [ObservableProperty] private Choice? _bus;
    [ObservableProperty] private Choice? _target;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ValueText))] private double _value;

    private MacroAction Kind => Enum.TryParse<MacroAction>(Action.Value, out var a) ? a : MacroAction.Mute;
    public bool NeedsStrip => Kind is MacroAction.Mute or MacroAction.Unmute or MacroAction.Fader or MacroAction.SendOn or MacroAction.SendOff;
    public bool NeedsBus => Kind is MacroAction.SendOn or MacroAction.SendOff;
    public bool NeedsTarget => Kind is MacroAction.Snapshot or MacroAction.Scene;
    public bool NeedsDb => Kind == MacroAction.Fader;
    public bool NeedsSeconds => Kind == MacroAction.Wait;
    public string ValueText => NeedsSeconds ? $"{Value:0.#} s" : Value <= -59.5 ? "−∞" : $"{Value:+0.0;−0.0;0.0} dB";
    public string ExtraText => _extra.Count == 0 ? "" : $"+ {_extra.Count} more";

    partial void OnActionChanged(Choice value)
    {
        if (NeedsSeconds && Value < 0) Value = 1;
        if (NeedsDb && Value > 10) Value = 0;
    }

    public IRelayCommand RemoveCommand => new RelayCommand(() => _form.Remove(this));
    public IRelayCommand UpCommand => new RelayCommand(() => _form.Move(this, -1));
    public IRelayCommand DownCommand => new RelayCommand(() => _form.Move(this, 1));

    public MacroStep ToStep()
    {
        var kind = Kind;
        var t = Target?.Value ?? "";
        if (kind == MacroAction.Snapshot && t.StartsWith("scene:", StringComparison.Ordinal)) kind = MacroAction.Scene;
        var strips = Strip?.Value is { } s ? string.Join(",", new[] { s }.Concat(_extra.Where(x => x != s))) : null;
        return new MacroStep
        {
            Action = kind,
            Strip = NeedsStrip ? strips : null,
            Bus = NeedsBus ? Bus?.Value : null,
            Target = NeedsTarget ? t[(t.IndexOf(':') + 1)..] : null,
            Value = NeedsDb ? (Value <= -59.5 ? Db.Floor : Math.Round(Value, 1)) : NeedsSeconds ? Math.Round(Value, 1) : 0,
        };
    }
}
