using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;

namespace NexAudio.App.ViewModels;

/// <summary>One preset in the library: load it, move it to a folder, or delete it (your own only).</summary>
public sealed partial class PresetRow(NexAudioViewModel owner, PresetInfo p) : ObservableObject
{
    public string Id { get; } = p.Id;
    public string Name { get; } = p.Name;
    public string Folder { get; } = p.Folder.Length > 0 ? p.Folder : "Not in a folder";
    public bool Mine { get; } = !p.BuiltIn;
    public string Who => p.BuiltIn ? "NEX-Audio starting point" : p.SavedUtc > DateTime.MinValue ? $"Saved {p.SavedUtc.ToLocalTime():d MMM yyyy}" : "Saved here";
    [ObservableProperty] private string _moveTo = p.Folder;
    [RelayCommand] private Task Load() => owner.LoadPreset(Id);
    [RelayCommand] private Task Delete() => owner.DeletePreset(Id);
    [RelayCommand] private Task Move() => owner.MovePreset(Id, MoveTo);
}

/// <summary>Undo and redo, copy and paste between strips, and channel presets.</summary>
public sealed partial class NexAudioViewModel
{
    public bool CanUndo => DeskStatus.UndoText is not null;
    public bool CanRedo => DeskStatus.RedoText is not null;
    public string UndoTip => DeskStatus.UndoText is { } u ? $"Undo {Friendly(u)}" : "Nothing to undo";
    public string RedoTip => DeskStatus.RedoText is { } r ? $"Redo {Friendly(r)}" : "Nothing to redo";

    /// <summary>"EQ band 3 gain on ch15" with the strip's own name.</summary>
    private string Friendly(string label)
    {
        var i = label.LastIndexOf(" on ", StringComparison.Ordinal);
        if (i < 0) return label;
        var id = label[(i + 4)..].Trim();
        return _mix.Find(id) is { } s ? $"{label[..i]} on {NameOf(s)}" : label;
    }

    [RelayCommand] private Task Undo() => Command("undo");
    [RelayCommand] private Task Redo() => Command("redo");

    // ---- Copy and paste ------------------------------------------------------------------------

    private Strip? _clipboard;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasClipboard))] private string _clipboardName = "";
    public bool HasClipboard => _clipboard is not null;

    public static IReadOnlyList<Choice> PasteParts { get; } =
    [
        new("all", "Everything"), new("input", "Cuts and drive"), new("eq", "EQ and dynamic EQ"),
        new("dyn", "Gate and compressor"), new("trigger", "Drum trigger"), new("fx", "Effects"),
    ];

    [ObservableProperty] private Choice _pastePart = PasteParts[0];

    [RelayCommand]
    private void CopyStrip()
    {
        if (SelectedStrip is not { } s) return;
        _clipboard = s.Clone();
        ClipboardName = NameOf(s);
        Notice = $"Copied {NameOf(s)}. Open another strip and press Paste.";
    }

    [RelayCommand]
    private Task PasteStrip()
    {
        if (_clipboard is not { } c || SelectedId is not { } id) return Task.CompletedTask;
        return Command("paste", new() { ["to"] = id, ["parts"] = PastePart.Value }, c);
    }

    // ---- The preset library -------------------------------------------------------------------------

    public ObservableCollection<PresetRow> PresetRows { get; } = [];
    public ObservableCollection<Choice> PresetFolders { get; } = [new("*", "All folders")];
    [ObservableProperty] private string _presetSearch = "";
    [ObservableProperty] private Choice? _presetFolder;
    [ObservableProperty] private string _newPresetName = "";
    [ObservableProperty] private string _newPresetFolder = "";
    public ObservableCollection<string> FolderNames { get; } = [];
    private IReadOnlyList<PresetInfo> _presets = [];
    private string _presetsKey = "";
    public string PresetCountText => PresetRows.Count == _presets.Count
        ? $"{_presets.Count} presets"
        : $"{PresetRows.Count} of {_presets.Count} presets";

    private void ApplyTools(DeskStatus d)
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
        var key = string.Join("|", d.Presets.Select(p => p.Id + p.Name + "/" + p.Folder));
        if (key == _presetsKey) return;
        _presetsKey = key;
        _presets = d.Presets;
        var keep = PresetFolder?.Value ?? "*";
        var folders = d.Presets.Where(p => p.Folder.Length > 0).GroupBy(p => p.Folder, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => !g.Any(p => p.BuiltIn)).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First().Folder).ToList();
        PresetFolders.Clear();
        PresetFolders.Add(new Choice("*", "All folders"));
        PresetFolders.Add(new Choice("mine", "Only mine"));
        foreach (var f in folders) PresetFolders.Add(new Choice(f, f));
        if (d.Presets.Any(p => p.Folder.Length == 0)) PresetFolders.Add(new Choice("", "Not in a folder"));
        FolderNames.Clear();
        foreach (var f in folders) FolderNames.Add(f);
        PresetFolder = PresetFolders.FirstOrDefault(c => c.Value == keep) ?? PresetFolders[0];
        FilterPresets();
    }

    partial void OnPresetSearchChanged(string value) => FilterPresets();
    partial void OnPresetFolderChanged(Choice? value) => FilterPresets();

    private void FilterPresets()
    {
        var folder = PresetFolder?.Value ?? "*";
        PresetRows.Clear();
        foreach (var p in _presets.Where(p => p.Matches(PresetSearch)
                     && (folder == "*" || folder == "mine" && !p.BuiltIn || folder != "mine" && string.Equals(p.Folder, folder, StringComparison.CurrentCultureIgnoreCase))))
            PresetRows.Add(new PresetRow(this, p));
        OnPropertyChanged(nameof(PresetCountText));
    }

    internal Task LoadPreset(string id)
    {
        if (SelectedId is not { } strip) return Task.CompletedTask;
        var name = _presets.FirstOrDefault(p => p.Id == id)?.Name ?? "the preset";
        Notice = $"Loaded {name} ({PastePart.Label.ToLowerInvariant()}).";
        return Command("preset.load", new() { ["strip"] = strip, ["id"] = id, ["parts"] = PastePart.Value });
    }

    [RelayCommand]
    private Task SavePreset()
    {
        if (SelectedStrip is not { } s) return Task.CompletedTask;
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? NameOf(s) : NewPresetName.Trim();
        var folder = PresetStore.CleanFolder(NewPresetFolder);
        NewPresetName = "";
        Notice = folder.Length > 0 ? $"Saved the preset \"{name}\" in {folder}." : $"Saved the preset \"{name}\".";
        return Command("preset.save", new() { ["strip"] = s.Id, ["name"] = name, ["folder"] = folder });
    }

    internal Task DeletePreset(string id) =>
        id.StartsWith("builtin-", StringComparison.Ordinal) ? Task.CompletedTask : Command("preset.delete", new() { ["id"] = id });

    internal Task MovePreset(string id, string folder) => Command("preset.move", new() { ["id"] = id, ["folder"] = PresetStore.CleanFolder(folder) });

    // ---- Dynamic EQ activity -----------------------------------------------------------------------

    /// <summary>How far the dynamic EQ is moving on the open strip right now (dB).</summary>
    [ObservableProperty] private double _selectedDynEq;
}
