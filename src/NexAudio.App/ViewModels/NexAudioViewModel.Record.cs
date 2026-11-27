using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Audio.Record;

namespace NexAudio.App.ViewModels;

/// <summary>The Soundcheck page: record every channel, then play the recording into the desk instead of the microphones.</summary>
public sealed partial class NexAudioViewModel
{
    public ObservableCollection<TakeRow> TakeRows { get; } = [];
    public ObservableCollection<TrackChip> TrackChips { get; } = [];
    private string _takesKey = "", _chipsKey = "";
    private bool _applyingTape;

    private TapeStatus Tape => DeskStatus.Tape;

    private void ApplyTape(TapeStatus t)
    {
        _applyingTape = true;
        var key = string.Join("|", t.Takes.Select(x => x.Id + x.Name + x.Seconds.ToString("0", CultureInfo.InvariantCulture))) + t.TakeId;
        if (key != _takesKey)
        {
            _takesKey = key;
            TakeRows.Clear();
            foreach (var x in t.Takes) TakeRows.Add(new TakeRow(x, x.Id == t.TakeId, this));
        }
        var armed = t.Settings.Armed?.ToHashSet();
        var count = Math.Max(_mix.Channels.Count, 0);
        var ck = string.Join(",", _mix.Channels.Select(c => NameOf(c) + ColourOf(c))) + "|" + string.Join(",", t.Settings.Armed ?? []) + "|" +
                 string.Join(",", t.LiveInputs) + "|" + string.Join(",", t.PlaybackInputs) + t.Playback + t.Ready + t.Inputs;
        if (ck != _chipsKey)
        {
            _chipsKey = ck;
            TrackChips.Clear();
            for (var n = 1; n <= count; n++)
            {
                var ch = _mix.Channels[n - 1];
                TrackChips.Add(new TrackChip(n, NameOf(ch), new SolidColorBrush(ColourOf(ch)), armed is null || armed.Contains(n), t.LiveInputs.Contains(n),
                    t.Playback && t.PlaybackInputs.Contains(n), this));
            }
        }
        if (!_editingTapeFolder) TapeFolder = t.Settings.Folder ?? "";
        TapeFormat = FormatChoices.FirstOrDefault(c => c.Value == t.Settings.Format.ToString()) ?? FormatChoices[0];
        TapeTap = TapChoices2.FirstOrDefault(c => c.Value == t.Settings.Tap.ToString()) ?? TapChoices2[0];
        RecordMain = t.Settings.RecordMain;
        TapeLoop = t.Settings.Loop;
        if (!_seeking) TapePosition = t.Position;
        _applyingTape = false;
        foreach (var p in new[]
                 {
                     nameof(IsRecording), nameof(IsPlayingTape), nameof(IsTapePaused), nameof(PlaybackMode), nameof(LiveMode), nameof(TapeClock), nameof(TapeLength),
                     nameof(TapeTitle), nameof(TapeStateText), nameof(DiskText), nameof(TapeNote), nameof(HasTapeNote), nameof(TapeReady), nameof(CanRecord),
                     nameof(ArmedText), nameof(HasTake), nameof(PlaybackBanner), nameof(DropoutText), nameof(TapeFolderShown), nameof(NoTakes), nameof(LowDisk), nameof(TapeLengthValue),
                 })
            OnPropertyChanged(p);
    }

    public bool IsRecording => Tape.State == TapeState.Recording;
    public bool IsPlayingTape => Tape.State == TapeState.Playing;
    public bool IsTapePaused => Tape.State is TapeState.Paused or TapeState.Stopped;
    public bool PlaybackMode => Tape.Playback;
    public bool LiveMode => !Tape.Playback;
    public bool TapeReady => Tape.Ready;
    public bool CanRecord => Tape.Ready && !Tape.Playback && !IsRecording;
    public bool HasTake => Tape.TakeId is not null && !IsRecording;
    public bool NoTakes => Tape.Takes.Count == 0;
    public string TapeClock => Clock(Tape.Position);
    public string TapeLength => IsRecording ? "recording" : Tape.Length > 0 ? "of " + Clock(Tape.Length) : "";
    public double TapeLengthValue => Math.Max(0.1, Tape.Length);
    public string TapeTitle => Tape.TakeName ?? (IsRecording ? "Recording" : "Nothing loaded");

    public string TapeStateText => Tape.State switch
    {
        TapeState.Recording => "RECORDING",
        TapeState.Playing => "PLAYING INTO THE DESK",
        TapeState.Paused => "PAUSED",
        _ => Tape.Ready ? "READY" : "START THE SOUND FIRST",
    };

    public string PlaybackBanner => $"Virtual soundcheck: the channels are playing \"{Tape.TakeName ?? "the recording"}\", not the microphones.";

    public string DiskText => Tape.FreeGb <= 0 ? "" :
        $"{Tape.FreeGb:0} GB free · room for about {Hours(Tape.MinutesLeft)} with {ArmedCount} channels{(Tape.Settings.RecordMain ? " and the main mix" : "")}";

    public bool LowDisk => Tape.FreeGb > 0 && Tape.MinutesLeft < 120;
    public string TapeNote => Tape.Note ?? "";
    public bool HasTapeNote => Tape.Note is { Length: > 0 };
    public string DropoutText => Tape.Dropouts == 0 ? "" : IsRecording ? $"The disk fell behind {Tape.Dropouts}×" : $"{Tape.Dropouts} gaps in playback";
    public string TapeFolderShown => Tape.Folder;

    private int ArmedCount => Tape.Settings.Armed?.Count ?? _mix.Channels.Count;
    public string ArmedText => $"{ArmedCount} of {_mix.Channels.Count} channels armed";

    private static string Clock(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string Hours(double minutes) =>
        minutes >= 120 ? $"{minutes / 60:0} hours" : minutes >= 60 ? "1 hour" : $"{minutes:0} minutes";

    // ---- Transport -------------------------------------------------------------------------------

    [ObservableProperty] private string _newTakeName = "";

    [RelayCommand]
    private Task RecordTape()
    {
        var name = NewTakeName.Trim();
        NewTakeName = "";
        // The X32's scribble names, so each file is named after its channel.
        var names = _mix.Channels.Select(NameOf).ToList();
        return Command("tape.record", new() { ["name"] = name, ["by"] = Live.User?.Name ?? "" }, names);
    }

    [RelayCommand] private Task StopTape() => Command("tape.stop");
    [RelayCommand] private Task PlayTape() => Command("tape.play");
    [RelayCommand] private Task PauseTape() => Command("tape.pause");
    [RelayCommand] private Task TapeToStart() => Seek(0);
    [RelayCommand] private Task TapeBack() => Seek(Tape.Position - 10);
    [RelayCommand] private Task TapeForward() => Seek(Tape.Position + 10);
    [RelayCommand] private Task PlaybackOn() => Command("tape.playback", new() { ["on"] = "true" });
    [RelayCommand] private Task LiveInputs() => Command("tape.playback", new() { ["on"] = "false" });
    [RelayCommand] private Task ArmAll() => Command("tape.arm", new() { ["ch"] = "0", ["on"] = "true" });
    [RelayCommand] private Task ArmNone() => Command("tape.arm", new() { ["ch"] = "0", ["on"] = "false" });

    private Task Seek(double seconds) => Command("tape.seek", new() { ["seconds"] = Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture) });

    // The position slider: a drag seeks once, when it is let go (see SoundcheckPage).
    [ObservableProperty] private double _tapePosition;
    private bool _seeking;

    public void BeginSeek() => _seeking = true;

    public void EndSeek()
    {
        _seeking = false;
        _ = Seek(TapePosition);
    }

    internal void LoadTake(TakeInfo t, bool play) => _ = Command(play ? "tape.play" : "tape.load", new() { ["id"] = t.Id });
    internal void DeleteTake(TakeInfo t) => _ = Command("tape.delete", new() { ["id"] = t.Id });
    internal void Arm(int ch, bool on) => _ = Command("tape.arm", new() { ["ch"] = ch.ToString(CultureInfo.InvariantCulture), ["on"] = on ? "true" : "false" });
    internal void KeepLive(int ch, bool on) => _ = Command("tape.live", new() { ["ch"] = ch.ToString(CultureInfo.InvariantCulture), ["on"] = on ? "true" : "false" });

    /// <summary>The Soundcheck page's Bring in button picked a folder of WAV files.</summary>
    public void ImportTake(string folder) => _ = Command("tape.import", new() { ["folder"] = folder });

    // ---- Recorder settings -------------------------------------------------------------------------

    public static IReadOnlyList<Choice> FormatChoices { get; } = [new("Pcm24", "24-bit (most programs)"), new("Float32", "32-bit float (cannot clip)")];

    public static IReadOnlyList<Choice> TapChoices2 { get; } =
    [
        new("Input", "Straight from the X32"), new("Processed", "After EQ and dynamics"),
    ];

    [ObservableProperty] private string _tapeFolder = "";
    [ObservableProperty] private Choice? _tapeFormat;
    [ObservableProperty] private Choice? _tapeTap;
    [ObservableProperty] private bool _recordMain = true;
    [ObservableProperty] private bool _tapeLoop;
    private bool _editingTapeFolder;

    partial void OnTapeFolderChanged(string value)
    {
        if (!_applyingTape) _editingTapeFolder = true;
    }

    partial void OnTapeFormatChanged(Choice? value) => SaveTape();
    partial void OnTapeTapChanged(Choice? value) => SaveTape();
    partial void OnRecordMainChanged(bool value) => SaveTape();
    partial void OnTapeLoopChanged(bool value) => SaveTape();

    [RelayCommand]
    private void SaveTapeFolder()
    {
        _editingTapeFolder = false;
        SaveTape(force: true);
    }

    /// <summary>The folder picker on the Soundcheck page chose a folder for recordings.</summary>
    public void UseTapeFolder(string folder)
    {
        TapeFolder = folder;
        SaveTapeFolder();
    }

    private void SaveTape(bool force = false)
    {
        if ((_applyingTape && !force) || !CanMix) return;
        var s = Tape.Settings with
        {
            Folder = string.IsNullOrWhiteSpace(TapeFolder) ? null : TapeFolder.Trim(),
            Format = Enum.TryParse<RecordFormat>(TapeFormat?.Value, out var f) ? f : RecordFormat.Pcm24,
            Tap = Enum.TryParse<RecordTap>(TapeTap?.Value, out var t) ? t : RecordTap.Input,
            RecordMain = RecordMain,
            Loop = TapeLoop,
        };
        _ = Command("tape.set", null, s);
    }
}

public sealed class TakeRow(TakeInfo t, bool loaded, NexAudioViewModel owner)
{
    public string Name => t.Name;
    public bool IsLoaded => loaded;
    public bool CanPlay => t.CanSoundcheck;

    public string Detail
    {
        get
        {
            var when = t.StartedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture);
            var len = TimeSpan.FromSeconds(t.Seconds);
            var dur = len.TotalHours >= 1 ? $"{(int)len.TotalHours} h {len.Minutes} min" : len.TotalMinutes >= 1 ? $"{len.Minutes} min {len.Seconds} s" : $"{len.Seconds} s";
            var size = t.Bytes >= 1e9 ? $"{t.Bytes / 1e9:0.0} GB" : $"{t.Bytes / 1e6:0} MB";
            var extra = t.Imported ? " · brought in" : t.Open ? " · cut short" : t.Tap == RecordTap.Processed ? " · after processing (not for soundcheck)" : "";
            return $"{when} · {t.Tracks} channels · {dur} · {size} · {t.SampleRate / 1000.0:0.#} kHz{extra}";
        }
    }

    public IRelayCommand PlayCommand { get; } = new RelayCommand(() => owner.LoadTake(t, true));
    public IRelayCommand LoadCommand { get; } = new RelayCommand(() => owner.LoadTake(t, false));
    public IRelayCommand DeleteCommand { get; } = new RelayCommand(() => owner.DeleteTake(t));
}

/// <summary>One channel on the Soundcheck page: armed to record, and live or playing back.</summary>
public sealed partial class TrackChip : ObservableObject
{
    private readonly NexAudioViewModel _owner;

    public TrackChip(int number, string name, IBrush tone, bool armed, bool live, bool playing, NexAudioViewModel owner)
    {
        Number = number;
        Name = name;
        Tone = tone;
        _armed = armed;
        _live = live;
        Playing = playing;
        _owner = owner;
    }

    public int Number { get; }
    public string NumberText => Number.ToString(CultureInfo.InvariantCulture);
    public string Name { get; }
    public IBrush Tone { get; }
    /// <summary>Playing the recording now, instead of its microphone.</summary>
    public bool Playing { get; }
    public string SourceText => Playing ? "PLAYBACK" : "LIVE";

    [ObservableProperty] private bool _armed;
    [ObservableProperty] private bool _live;

    partial void OnArmedChanged(bool value) => _owner.Arm(Number, value);
    partial void OnLiveChanged(bool value) => _owner.KeepLive(Number, value);
}
