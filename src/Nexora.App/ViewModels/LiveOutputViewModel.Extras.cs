using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Controls;
using Nexora.App.Services;
using Nexora.Core.Config;
using Nexora.Core.Live;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.App.ViewModels;

/// <summary>
/// The lower half of the Live Output screen: videos and pictures, direct camera pick,
/// recording and streaming with health lights, and Service / Rehearsal mode.
/// </summary>
public sealed partial class LiveOutputViewModel
{
    private DispatcherTimer? _fast;
    private bool _extrasReady;

    public static IReadOnlyList<int> FadeChoices { get; } = [0, 250, 500, 1000, 1500, 2000, 3000];
    public ObservableCollection<MediaItem> Media { get; } = [];
    public ObservableCollection<string> AudioOutputs { get; } = ["Default sound output"];
    public ObservableCollection<CameraPick> CameraPicks { get; } = [];

    [ObservableProperty] private int _extrasTab;
    [ObservableProperty] private MediaItem? _selectedMedia;
    [ObservableProperty] private bool _mediaLoop;
    [ObservableProperty] private int _fadeInMs = 500;
    [ObservableProperty] private int _fadeOutMs = 500;
    [ObservableProperty] private int _volume = 100;
    [ObservableProperty] private string _audioOutput = "Default sound output";
    [ObservableProperty] private bool _showTextOverVideo;
    [ObservableProperty] private bool _adding;
    [ObservableProperty] private string _addStatus = "";
    [ObservableProperty] private string _boothText = "";
    [ObservableProperty] private string _serviceName = "Sunday Service";

    public bool TabMedia => ExtrasTab == 0;
    public bool TabCameras => ExtrasTab == 1;
    public bool TabBroadcast => ExtrasTab == 2;
    public bool TabService => ExtrasTab == 3;

    partial void OnExtrasTabChanged(int value)
    {
        OnPropertyChanged(nameof(TabMedia));
        OnPropertyChanged(nameof(TabCameras));
        OnPropertyChanged(nameof(TabBroadcast));
        OnPropertyChanged(nameof(TabService));
    }

    [RelayCommand] private void ShowTab(string i) => ExtrasTab = int.Parse(i);

    // ---------- Media ----------

    private MediaPlay? NowPlaying =>
        Live.Snapshot.Sources.FirstOrDefault(s => s.Key == Sections.SourceKey(LiveState.LiveOutputPc, SectionKind.Media))?.Current?.Media;

    public bool HasMedia => Media.Count > 0;
    public bool NoMedia => Media.Count == 0;
    public bool MediaOn => NowPlaying is not null;
    public bool MediaPlaying => NowPlaying?.Playing == true;
    public string MediaTitle => NowPlaying is { } p ? p.Media.Title : "Nothing playing";
    public string MediaState => NowPlaying switch
    {
        null => "Pick a video or picture, then Cue or Play.",
        { Media.Kind: MediaKind.Image } => "Picture on screen",
        { Playing: true, Loop: true } p => $"Playing (loop) · {Time(p.Position(DateTime.UtcNow))}",
        { Playing: true } p => $"Playing · {Time(p.Position(DateTime.UtcNow))} of {Time(p.Media.DurationSeconds)}",
        { } p => $"Cued · paused at {Time(p.PausedAt)}",
    };
    public string TimeLeft => NowPlaying is { Media.Kind: MediaKind.Video, Loop: false } p && p.Media.DurationSeconds > 0
        ? "-" + Time(p.Left(DateTime.UtcNow)) : "";
    public bool TimeLow => NowPlaying is { Playing: true, Loop: false, Media.Kind: MediaKind.Video } p && p.Media.DurationSeconds > 0 && p.Left(DateTime.UtcNow) <= 10;
    public double MediaProgress => NowPlaying is { Media.DurationSeconds: > 0 } p ? Math.Clamp(p.Position(DateTime.UtcNow) / p.Media.DurationSeconds, 0, 1) * 100 : 0;

    private static string Time(double s)
    {
        if (s < 0 || double.IsNaN(s)) s = 0;
        var t = TimeSpan.FromSeconds(Math.Round(s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void InitExtras()
    {
        if (_extrasReady) return;
        _extrasReady = true;
        if (IsLocal)
        {
            foreach (var m in MediaLibrary.Load()) Media.Add(new MediaItem(m));
            _ = Task.Run(() =>
            {
                var outs = VlcFeed.AudioOutputs();
                Dispatcher.UIThread.Post(() => { foreach (var o in outs) if (!AudioOutputs.Contains(o)) AudioOutputs.Add(o); });
            });
            ServiceName = S.Store?.Broadcast.ServiceName ?? ServiceName;
        }
        SelectedMedia = Media.FirstOrDefault();
        BoothText = Live.Modes.Booth ?? "";
        _fast = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => TickExtras());
        _fast.Start();
        SyncExtras();
    }

    private void TickExtras()
    {
        if (NowPlaying is null) return;
        OnPropertyChanged(nameof(MediaState));
        OnPropertyChanged(nameof(TimeLeft));
        OnPropertyChanged(nameof(TimeLow));
        OnPropertyChanged(nameof(MediaProgress));
    }

    /// <summary>The view hands over the files the operator picked. Copying and checking runs off the UI thread.</summary>
    public async Task AddMediaAsync(IReadOnlyList<string> paths)
    {
        if (!IsLocal || paths.Count == 0) return;
        Adding = true;
        try
        {
            var n = 0;
            foreach (var p in paths)
            {
                n++;
                AddStatus = paths.Count == 1 ? $"Copying and checking {Path.GetFileName(p)}…" : $"Copying and checking {n} of {paths.Count}…";
                try
                {
                    var info = await Task.Run(() => MediaLibrary.Add(p));
                    var existing = Media.FirstOrDefault(m => m.Info.File == info.File);
                    if (existing is not null) Media.Remove(existing);
                    var item = new MediaItem(info);
                    Media.Add(item);
                    SelectedMedia = item;
                }
                catch (Exception ex)
                {
                    Live.RaiseToast($"Could not add {Path.GetFileName(p)}: {ex.Message}", true);
                }
            }
            MediaLibrary.Save(Media.Select(m => m.Info));
            var problems = Media.Count(m => m.HasProblem);
            AddStatus = problems > 0 ? $"{problems} file(s) need a look before the service." : "";
        }
        finally
        {
            Adding = false;
            OnPropertyChanged(nameof(HasMedia));
            OnPropertyChanged(nameof(NoMedia));
        }
    }

    [RelayCommand]
    private void RemoveMedia(MediaItem item)
    {
        if (NowPlaying?.Media.File == item.Info.File) { Live.RaiseToast("Stop this video before removing it.", true); return; }
        Media.Remove(item);
        MediaLibrary.Remove(item.Info);
        MediaLibrary.Save(Media.Select(m => m.Info));
        if (SelectedMedia == item) SelectedMedia = Media.FirstOrDefault();
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(NoMedia));
    }

    [RelayCommand]
    private void MoveMediaUp(MediaItem item)
    {
        var i = Media.IndexOf(item);
        if (i <= 0) return;
        Media.Move(i, i - 1);
        MediaLibrary.Save(Media.Select(m => m.Info));
    }

    [RelayCommand] private void PickMedia(MediaItem item) => SelectedMedia = item;

    private MediaPlay Build(MediaInfo m, bool playing, double at, long? id = null) => new(
        m, playing, DateTime.UtcNow, at, MediaLoop, FadeInMs, FadeOutMs, Volume,
        AudioOutput == AudioOutputs[0] ? null : AudioOutput, ShowTextOverVideo, id ?? DateTime.UtcNow.Ticks);

    private Task SendMedia(MediaPlay? p) => Live.Client is { } c ? c.SendAsync(new MediaCommand(p)) : Task.CompletedTask;

    [RelayCommand]
    private Task Cue()
    {
        if (SelectedMedia is not { } m) return Task.CompletedTask;
        return SendMedia(Build(m.Info, false, 0));
    }

    /// <summary>Plays the picked file. If the same file is cued or paused, it carries on from there.</summary>
    [RelayCommand]
    private Task Play()
    {
        var now = NowPlaying;
        if (SelectedMedia is not { } m)
            return now is { Playing: false } ? SendMedia(now with { Playing = true }) : Task.CompletedTask;
        if (m.HasProblem && m.Info.Width == 0 && m.Info.Kind == MediaKind.Video)
            Live.RaiseToast("This file failed its check. Playing anyway.", true);
        if (now is { Playing: false } && now.Media.File == m.Info.File)
            return SendMedia(Build(m.Info, true, now.PausedAt, now.Id));
        return SendMedia(Build(m.Info, true, 0));
    }

    [RelayCommand]
    private Task Pause()
    {
        if (NowPlaying is not { Playing: true } p) return Task.CompletedTask;
        return SendMedia(p with { Playing = false, PausedAt = p.Position(DateTime.UtcNow) });
    }

    [RelayCommand]
    private Task Restart()
    {
        if (NowPlaying is not { } p) return Task.CompletedTask;
        return SendMedia(p with { Playing = true, PausedAt = 0, Id = DateTime.UtcNow.Ticks });
    }

    [RelayCommand] private Task StopMedia() => SendMedia(null);

    /// <summary>Volume, loop and "text over video" changes reach a playing video straight away.</summary>
    private void Adjust()
    {
        if (NowPlaying is not { } p) return;
        var at = p.Position(DateTime.UtcNow);
        _ = SendMedia(p with { Volume = Volume, Loop = MediaLoop, ShowText = ShowTextOverVideo, PausedAt = at, FadeOutMs = FadeOutMs });
    }

    private bool _syncingMedia;
    partial void OnVolumeChanged(int value) { if (!_syncingMedia) Adjust(); }
    partial void OnMediaLoopChanged(bool value) { if (!_syncingMedia) Adjust(); }
    partial void OnShowTextOverVideoChanged(bool value) { if (!_syncingMedia) Adjust(); }

    // ---------- Cameras ----------

    public string CameraNow =>
        Live.Snapshot.Sources.FirstOrDefault(s => s.Key == Sections.SourceKey(LiveState.LiveOutputPc, SectionKind.Camera))?.Current?.Camera?.Label
        ?? "No direct camera";
    public bool CameraHint => CameraPicks.Count == 0;
    public string CameraHelp => Live.Engine switch
    {
        { CameraProblem: { } p } => p,
        { NdiAvailable: false } when CameraPicks.Count == 0 => "No capture cards found, and NDI is not installed on this PC. Install the NDI runtime to see network cameras.",
        _ when CameraPicks.Count == 0 => "No cameras found yet. Plug in a capture card or start an NDI camera; the list refreshes every few seconds.",
        _ => "Use these when the Camera Switcher terminal is not running. A camera from the switcher takes over when it sends.",
    };

    [RelayCommand]
    private Task TakeCamera(CameraPick pick) =>
        Live.Client is { } c ? c.SendAsync(new CameraCommand(new CameraFeed(pick.Label, pick.Kind, pick.Source, TransitionKind.Fade, 400))) : Task.CompletedTask;

    [RelayCommand]
    private Task ClearCamera() => Live.Client is { } c ? c.SendAsync(new CameraCommand(null)) : Task.CompletedTask;

    // ---------- Broadcast ----------

    public EngineStatus Engine => Live.Engine;
    public bool Recording => Engine.Recording;
    public bool Streaming => Engine.Streaming;
    public string RecordLabel => Recording ? "Stop recording" : "Start recording";
    public string StreamLabel => Streaming ? "Stop streaming" : "Start streaming";
    public string RecordText => Recording
        ? $"Recording · {Time(Engine.RecordingSeconds)} · {Path.GetFileName(Engine.RecordingFile ?? "")}" + (Engine.RecordProblem is { } warn ? $"\n{warn}" : "")
        : Engine.RecordProblem ?? (Engine.FfmpegAvailable ? "Not recording" : "The recorder (ffmpeg) is missing on this PC.");
    public string StreamText => Streaming
        ? $"{Engine.StreamState} · {Engine.StreamKbps:0} kbps · {Engine.DroppedFrames} dropped"
        : Engine.StreamProblem ?? (Live.Library?.StreamingConfigured == true ? "Not streaming" : "Add the stream address in Settings › Recording & streaming.");
    public string FpsText => $"{Engine.Fps:0} fps";
    public string CpuText => $"CPU {Engine.CpuPercent:0}%";
    public string DiskText => $"{Engine.DiskFreeGb:0} GB free";
    public bool FpsOk => Engine.Fps >= 24 || (!Recording && !Streaming);
    public bool CpuOk => Engine.CpuPercent < 85;
    public bool DiskOk => Engine.DiskFreeGb >= 20 || Engine.DiskFreeGb == 0;
    public bool StreamOk => Streaming ? Engine.StreamProblem is null && Engine.StreamState == "Live" : Engine.StreamProblem is null && Live.Library?.StreamingConfigured == true;
    public bool RecordOk => Engine.RecordProblem is null && Engine.FfmpegAvailable;

    [RelayCommand]
    private Task ToggleRecord() => Live.Client is { } c
        ? c.SendAsync(new EngineCommand(Recording ? EngineAction.StopRecording : EngineAction.StartRecording, ServiceName))
        : Task.CompletedTask;

    [RelayCommand]
    private Task ToggleStream() => Live.Client is { } c
        ? c.SendAsync(new EngineCommand(Streaming ? EngineAction.StopStream : EngineAction.StartStream, ServiceName))
        : Task.CompletedTask;

    // ---------- Service ----------

    public bool ServiceMode => Live.Modes.ServiceMode;
    public bool Rehearsal => Live.Modes.Rehearsal;
    public string ServiceModeText => ServiceMode
        ? "On. Settings, imports and updates are locked so nothing changes mid-service."
        : "Off. Turn it on when the service starts.";
    public string RehearsalText => Rehearsal
        ? "On. Operators can practise; the audience screens show only the logo, and nothing is recorded or streamed."
        : "Off. Everything sent live reaches the screens.";

    private Task SendModes(LiveModes m) => Live.Client is { } c ? c.SendAsync(new SetModes(m)) : Task.CompletedTask;

    [RelayCommand] private Task ToggleServiceMode() => SendModes(Live.Modes with { ServiceMode = !ServiceMode });
    [RelayCommand] private Task ToggleRehearsal() => SendModes(Live.Modes with { Rehearsal = !Rehearsal });
    [RelayCommand] private Task SendBooth() => SendModes(Live.Modes with { Booth = string.IsNullOrWhiteSpace(BoothText) ? null : BoothText.Trim() });
    [RelayCommand] private Task ClearBooth() { BoothText = ""; return SendModes(Live.Modes with { Booth = null }); }
    [RelayCommand] private void Booth(string text) { BoothText = text; _ = SendBooth(); }

    // ---------- Sync ----------

    private void SyncExtras(bool fromSnapshot = true)
    {
        if (!_extrasReady) return;
        var p = NowPlaying;
        if (p is not null && fromSnapshot)
        {
            _syncingMedia = true;
            Volume = p.Volume;
            MediaLoop = p.Loop;
            ShowTextOverVideo = p.ShowText;
            _syncingMedia = false;
        }
        foreach (var m in Media) m.IsOn = p?.Media.File == m.Info.File;

        var e = Live.Engine;
        var picks = new List<CameraPick>();
        foreach (var d in e.CameraDevices ?? []) picks.Add(new CameraPick(d, CameraSourceKind.Device, d, "Capture card"));
        foreach (var n in e.NdiSources ?? []) picks.Add(new CameraPick(n, CameraSourceKind.Ndi, n, "NDI"));
        if (!CameraPicks.Select(c => c.Key).SequenceEqual(picks.Select(c => c.Key)))
        {
            CameraPicks.Clear();
            foreach (var c in picks) CameraPicks.Add(c);
        }

        foreach (var n in new[]
        {
            nameof(MediaOn), nameof(MediaPlaying), nameof(MediaTitle), nameof(MediaState), nameof(TimeLeft), nameof(TimeLow), nameof(MediaProgress),
            nameof(CameraNow), nameof(CameraHint), nameof(CameraHelp),
            nameof(Engine), nameof(Recording), nameof(Streaming), nameof(RecordLabel), nameof(StreamLabel), nameof(RecordText), nameof(StreamText),
            nameof(FpsText), nameof(CpuText), nameof(DiskText), nameof(FpsOk), nameof(CpuOk), nameof(DiskOk), nameof(StreamOk), nameof(RecordOk),
            nameof(ServiceMode), nameof(Rehearsal), nameof(ServiceModeText), nameof(RehearsalText),
        }) OnPropertyChanged(n);
    }

    private void LeaveExtras() => _fast?.Stop();
}

public sealed partial class MediaItem : ObservableObject
{
    public MediaItem(MediaInfo info)
    {
        Info = info;
        if (info.Thumbnail is { } t)
        {
            try
            {
                var path = Path.Combine(AppPaths.VideosDir, t);
                if (File.Exists(path))
                {
                    using var fs = File.OpenRead(path);
                    Thumb = Bitmap.DecodeToWidth(fs, 240);
                }
            }
            catch { }
        }
    }

    public MediaInfo Info { get; }
    public Bitmap? Thumb { get; }
    public string Title => Info.Title;
    public bool IsVideo => Info.Kind == MediaKind.Video;
    public string Icon => IsVideo ? "film" : "image";
    public bool HasProblem => Info.Problem is not null;
    public string Problem => Info.Problem ?? "";
    public string Details => Info.Kind == MediaKind.Image
        ? $"Picture · {Info.Width}×{Info.Height}"
        : $"{(Info.DurationSeconds > 0 ? TimeSpan.FromSeconds(Math.Round(Info.DurationSeconds)).ToString(Info.DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "?:??")} · {(Info.Height > 0 ? Info.Height + "p" : "size unknown")}{(Info.Codec is { } c ? " · " + c.ToUpperInvariant() : "")}";
    [ObservableProperty] private bool _isOn;
}

public sealed record CameraPick(string Label, CameraSourceKind Kind, string Source, string KindText)
{
    public string Key => $"{Kind}:{Source}";
    public string Icon => Kind == CameraSourceKind.Ndi ? "network" : "camera";
}
