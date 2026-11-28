using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Audio.Dsp;
using Nexora.Audio.Drums;
using Nexora.Audio.Engine;
using Nexora.Audio.Ipc;
using Nexora.Audio.Mix;

namespace NexAudio.App.ViewModels;

/// <summary>One sample on a trigger layer, shown as a chip with a remove button.</summary>
public sealed partial class TriggerTake(string path, bool missing, TriggerLayerRow row) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = path.Contains('/') ? path[(path.IndexOf('/') + 1)..].Replace(".wav", "", StringComparison.OrdinalIgnoreCase) : path;
    public string Kit { get; } = path.Contains('/') ? path[..path.IndexOf('/')] : "";
    /// <summary>The file is not in the library on this PC (the trigger plays the natural mic until it is back).</summary>
    public bool Missing { get; } = missing;

    [RelayCommand] private void Remove() => row.Remove(Path);
}

/// <summary>One hit-strength layer of the open channel's trigger: its samples, and a list to add another.</summary>
public sealed partial class TriggerLayerRow : ObservableObject
{
    private readonly NexAudioViewModel _owner;

    public TriggerLayerRow(NexAudioViewModel owner, int index, string title, string hint)
    {
        _owner = owner;
        Index = index;
        Title = title;
        Hint = hint;
    }

    public int Index { get; }
    public string Title { get; }
    public string Hint { get; }
    public ObservableCollection<TriggerTake> Takes { get; } = [];
    public bool CanAdd => Takes.Count < TriggerSettings.MaxTakes;
    public bool IsEmpty => Takes.Count == 0;
    public IReadOnlyList<Choice> Choices => _owner.DrumChoices;

    /// <summary>Picking a sample in the list adds it to this layer (then the list clears for the next one).</summary>
    [ObservableProperty] private Choice? _add;

    partial void OnAddChanged(Choice? value)
    {
        if (value is null) return;
        _owner.AddTriggerSample(Index, value.Value);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Add = null);
    }

    internal void Remove(string path) => _owner.RemoveTriggerSample(Index, path);

    internal void Set(IEnumerable<TriggerTake> takes)
    {
        Takes.Clear();
        foreach (var t in takes) Takes.Add(t);
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(IsEmpty));
    }

    internal void ChoicesChanged() => OnPropertyChanged(nameof(Choices));
}

/// <summary>
/// The Trigger page: the samples on each hit-strength layer, the starter kit, importing WAV files, Learn,
/// the hit light and the hit history.
/// </summary>
public sealed partial class NexAudioViewModel
{
    public ObservableCollection<TriggerLayerRow> TriggerLayers { get; } = [];
    public IReadOnlyList<Choice> DrumChoices { get; private set; } = [];
    public ObservableCollection<DrumSampleInfo> DrumLibraryRows { get; } = [];
    private string _drumKey = "";

    public static IReadOnlyList<Choice> TriggerLayerCounts { get; } =
        [new("1", "1 layer"), new("2", "2 layers"), new("3", "3 layers"), new("4", "4 layers")];

    public static IReadOnlyList<string> StarterDrums { get; } = ["Kick", "Snare", "Rack tom", "Floor tom"];

    [ObservableProperty] private Choice? _triggerLayerCount;
    private bool _buildingTrigger;

    /// <summary>The kit new samples are imported into.</summary>
    [ObservableProperty] private string _importKit = "My samples";

    // Live, from the meters.
    [ObservableProperty] private double _triggerFlash;
    [ObservableProperty] private TriggerHit[] _triggerHits = [];
    [ObservableProperty] private string _triggerLearnText = "";
    [ObservableProperty] private bool _triggerLearning;
    [ObservableProperty] private string _triggerStats = "No hits yet";
    [ObservableProperty] private string _triggerProblem = "";
    [ObservableProperty] private double _triggerRate = 48000;
    private uint _triggerSeq;
    private bool _triggerPrimed;
    private int _triggerFired, _triggerSoft;

    private static readonly string[][] LayerTitles =
    [
        ["Every hit"],
        ["Soft hits", "Hard hits"],
        ["Soft hits", "Medium hits", "Hard hits"],
        ["Soft hits", "Medium hits", "Hard hits", "Hardest hits"],
    ];

    private void BuildTrigger(Strip s)
    {
        _buildingTrigger = true;
        var n = Math.Clamp(s.Trigger.Layers.Count, 1, TriggerSettings.MaxLayers);
        TriggerLayerCount = TriggerLayerCounts[n - 1];
        _buildingTrigger = false;
        TriggerLayers.Clear();
        var titles = LayerTitles[n - 1];
        for (var i = 0; i < n; i++)
            TriggerLayers.Add(new TriggerLayerRow(this, i, titles[i], LayerHint(i, n)));
        TriggerHits = [];
        _triggerSeq = 0;
        _triggerPrimed = false;
        _triggerFired = _triggerSoft = 0;
        TriggerStats = "No hits yet";
        UpdateTrigger(s);
    }

    private static string LayerHint(int i, int n)
    {
        if (n == 1) return "Every hit plays from here, louder or softer with the hit";
        var lo = 100 * i / n;
        var hi = 100 * (i + 1) / n;
        return $"Hits from {lo}% to {hi}% strength";
    }

    private void UpdateTrigger(Strip s)
    {
        if (TriggerLayers.Count != Math.Clamp(s.Trigger.Layers.Count, 1, TriggerSettings.MaxLayers))
        {
            BuildTrigger(s);
            return;
        }
        var have = DeskStatus.DrumSamples.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = 0;
        for (var i = 0; i < TriggerLayers.Count; i++)
        {
            var row = TriggerLayers[i];
            var samples = s.Trigger.Layers[i].Samples;
            if (row.Takes.Select(t => t.Path).SequenceEqual(samples) && row.Takes.All(t => t.Missing == !have.Contains(t.Path))) continue;
            row.Set(samples.Select(p => new TriggerTake(p, have.Count > 0 && !have.Contains(p), row)));
        }
        missing = s.Trigger.AllSamples().Count(p => have.Count > 0 && !have.Contains(p));
        TriggerProblem = !s.Trigger.On ? ""
            : missing > 0 ? $"{missing} sample{(missing == 1 ? " is" : "s are")} missing on this PC, so the natural mic is playing. Add {(missing == 1 ? "it" : "them")} again, or pick others."
            : !s.Trigger.AllSamples().Any() ? "Pick a sample below (or load a starter drum) so the trigger has something to play."
            : "";
    }

    private (string, bool) TriggerSummary(Strip s)
    {
        var t = s.Trigger;
        if (!t.On) return ("Off", false);
        var count = t.AllSamples().Count();
        if (count == 0) return ("On, no sample yet", false);
        var first = t.AllSamples().First();
        var name = first.Contains('/') ? first[(first.IndexOf('/') + 1)..].Replace(".wav", "", StringComparison.OrdinalIgnoreCase) : first;
        return (t.Compare ? "Comparing: natural mic" : $"{name} · {t.Blend:0}% sample", true);
    }

    partial void OnTriggerLayerCountChanged(Choice? value)
    {
        if (_buildingTrigger || value is null || SelectedStrip is not { } s) return;
        var n = int.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture);
        var layers = s.Trigger.Layers.Select(l => l.Clone()).ToList();
        while (layers.Count < n) layers.Add(new TriggerLayer());
        if (layers.Count > n)
        {
            // Samples on the layers taken away move to the hardest one left, so nothing is lost by accident.
            foreach (var extra in layers.Skip(n)) layers[n - 1].Samples.AddRange(extra.Samples.Where(x => !layers[n - 1].Samples.Contains(x)));
            layers.RemoveRange(n, layers.Count - n);
            layers[n - 1].Samples = layers[n - 1].Samples.Take(TriggerSettings.MaxTakes).ToList();
        }
        SetLayers(s.Id, layers);
    }

    private void SetLayers(string id, List<TriggerLayer> layers)
    {
        Queue($"{id}.trigger.layers", layers);
        if (_mix.Find(id) is { } st) BuildTrigger(st);
        UpdateWorkspace();
    }

    internal void AddTriggerSample(int layer, string path)
    {
        if (SelectedStrip is not { } s || layer >= s.Trigger.Layers.Count) return;
        var layers = s.Trigger.Layers.Select(l => l.Clone()).ToList();
        if (layers[layer].Samples.Count >= TriggerSettings.MaxTakes || layers[layer].Samples.Contains(path)) return;
        layers[layer].Samples.Add(path);
        SetLayers(s.Id, layers);
        if (!s.Trigger.On) Queue($"{s.Id}.trigger.on", true);
    }

    internal void RemoveTriggerSample(int layer, string path)
    {
        if (SelectedStrip is not { } s || layer >= s.Trigger.Layers.Count) return;
        var layers = s.Trigger.Layers.Select(l => l.Clone()).ToList();
        layers[layer].Samples.Remove(path);
        SetLayers(s.Id, layers);
    }

    /// <summary>Fills every layer from one drum of the starter kit (four strengths, two takes each) and turns the trigger on.</summary>
    [RelayCommand]
    private void LoadStarter(string drum)
    {
        if (SelectedStrip is not { } s || s.Kind != StripKind.Channel) return;
        var layers = StarterKit.Layers(drum).Select(l => new TriggerLayer { Samples = l }).ToList();
        SetLayers(s.Id, layers);
        Queue($"{s.Id}.trigger.on", true);
        Notice = $"{NameOf(s)} now triggers the starter {drum.ToLowerInvariant()}. Set the threshold so every hit lights the hit light.";
    }

    [RelayCommand]
    private Task LearnTrigger()
    {
        if (SelectedStrip is not { } s) return Task.CompletedTask;
        TriggerLearning = true;
        TriggerLearnText = "Listening… ask the drummer for 8 hits, the softest they will play included.";
        return Command("drums.learn", new() { ["strip"] = s.Id });
    }

    /// <summary>Copies WAV files into the drum library (the screen picks them). The first goes on the first empty layer.</summary>
    public Task ImportDrums(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return Task.CompletedTask;
        Notice = $"Adding {files.Count} sample{(files.Count == 1 ? "" : "s")} to \"{DrumLibrary.Clean(ImportKit)}\"…";
        return Command("drums.import", new() { ["files"] = string.Join('|', files), ["kit"] = ImportKit });
    }

    [RelayCommand]
    private Task DeleteDrum(DrumSampleInfo info) => Command("drums.delete", new() { ["path"] = info.Path });

    /// <summary>The drum library changed (on the desk status).</summary>
    private void ApplyDrums(DeskStatus d)
    {
        var key = string.Join("|", d.DrumSamples.Select(x => x.Path));
        if (key == _drumKey) return;
        _drumKey = key;
        DrumChoices = d.DrumSamples.Select(x => new Choice(x.Path, $"{x.Kit} · {x.Name}")).ToList();
        OnPropertyChanged(nameof(DrumChoices));
        DrumLibraryRows.Clear();
        foreach (var x in d.DrumSamples.Where(x => x.Kit != DrumLibrary.StarterKit)) DrumLibraryRows.Add(x);
        OnPropertyChanged(nameof(HasOwnSamples));
        foreach (var r in TriggerLayers) r.ChoicesChanged();
        if (SelectedStrip is { } s) UpdateTrigger(s);
    }

    public bool HasOwnSamples => DrumLibraryRows.Count > 0;

    /// <summary>The open channel's trigger, 30 times a second: the hit light, the history and Learn.</summary>
    private void ApplyTriggerLive(MixMetersMsg m, int index)
    {
        var flash = m.Trigger is { } tr && index >= 0 && index < tr.Length ? tr[index] : 0;
        TriggerFlash = Math.Max(flash, TriggerFlash * 0.7);
        if (TriggerFlash < 0.02) TriggerFlash = 0;
        if (m.TriggerLive is not { } live || live.StripId != SelectedId) return;
        TriggerRate = live.Rate;
        if (live.Learn >= 0)
        {
            TriggerLearning = true;
            TriggerLearnText = $"Listening… {live.Learn} of {DrumTrigger.LearnHits} hits. Ask for the softest hit they will play too.";
        }
        else if (TriggerLearning)
        {
            TriggerLearning = false;
            TriggerLearnText = "Learned. The threshold and hold are set; check the hit light on a few soft hits.";
        }
        if (_triggerPrimed && live.Seq == _triggerSeq) return;
        // Count the hits shown when the page opens, then each new one.
        var fresh = !_triggerPrimed ? live.Hits.Length : live.Seq < _triggerSeq ? 0 : (int)Math.Min(live.Seq - _triggerSeq, (uint)live.Hits.Length);
        _triggerPrimed = true;
        foreach (var h in live.Hits.Skip(live.Hits.Length - fresh))
        {
            if (h.Kind == HitKind.Fired) _triggerFired++;
            else _triggerSoft++;
        }
        _triggerSeq = live.Seq;
        TriggerHits = live.Hits;
        TriggerStats = _triggerFired + _triggerSoft == 0 ? "No hits yet"
            : $"{_triggerFired} fired · {_triggerSoft} too soft" + (_triggerSoft > 0 && _triggerFired > 0 ? " (lower the threshold if those were real hits)" : "");
    }

    [RelayCommand]
    private void ClearTriggerStats()
    {
        _triggerFired = _triggerSoft = 0;
        TriggerHits = [];
        TriggerStats = "No hits yet";
    }
}
