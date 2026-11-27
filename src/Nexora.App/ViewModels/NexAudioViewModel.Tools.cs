using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;

namespace Nexora.App.ViewModels;

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
        new("dyn", "Gate and compressor"), new("fx", "Effects"),
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

    // ---- Presets -----------------------------------------------------------------------------------

    public ObservableCollection<Choice> PresetChoices { get; } = [];
    [ObservableProperty] private Choice? _presetChoice;
    [ObservableProperty] private string _newPresetName = "";
    private string _presetsKey = "";

    private void ApplyTools(DeskStatus d)
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
        var key = string.Join("|", d.Presets.Select(p => p.Id + p.Name));
        if (key == _presetsKey) return;
        _presetsKey = key;
        var keep = PresetChoice?.Value;
        PresetChoices.Clear();
        foreach (var p in d.Presets) PresetChoices.Add(new Choice(p.Id, p.BuiltIn ? p.Name : "★ " + p.Name));
        PresetChoice = PresetChoices.FirstOrDefault(c => c.Value == keep) ?? PresetChoices.FirstOrDefault();
    }

    [RelayCommand]
    private Task LoadPreset()
    {
        if (SelectedId is not { } id || PresetChoice is not { } p) return Task.CompletedTask;
        return Command("preset.load", new() { ["strip"] = id, ["id"] = p.Value, ["parts"] = PastePart.Value });
    }

    [RelayCommand]
    private Task SavePreset()
    {
        if (SelectedStrip is not { } s) return Task.CompletedTask;
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? NameOf(s) : NewPresetName.Trim();
        NewPresetName = "";
        Notice = $"Saved the preset \"{name}\".";
        return Command("preset.save", new() { ["strip"] = s.Id, ["name"] = name });
    }

    [RelayCommand]
    private Task DeletePreset()
    {
        if (PresetChoice is not { } p || p.Value.StartsWith("builtin-", StringComparison.Ordinal)) return Task.CompletedTask;
        return Command("preset.delete", new() { ["id"] = p.Value });
    }

    // ---- Dynamic EQ activity -----------------------------------------------------------------------

    /// <summary>How far the dynamic EQ is moving on the open strip right now (dB).</summary>
    [ObservableProperty] private double _selectedDynEq;
}
