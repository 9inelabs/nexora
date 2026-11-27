using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Services;
using Nexora.Core.Config;
using Nexora.Core.Model;
using Nexora.Core.Protocol;
using Velopack;
using Velopack.Sources;

namespace Nexora.App.ViewModels;

/// <summary>Settings pages for lyrics and CCLI, recording and streaming, backups and standby, and updates.</summary>
public sealed partial class SettingsViewModel
{
    private void AddMorePages()
    {
        NavItems.Add(new NavItem("lyrics", "Lyrics & CCLI", "lyrics"));
        if (S.IsLiveOutputPc) NavItems.Add(new NavItem("broadcast", "Recording & streaming", "broadcast"));
        NavItems.Add(new NavItem("backups", "Backups & standby", "shield"));
        NavItems.Add(new NavItem("updates", "Updates", "download"));
    }

    public bool IsLyrics => Page == "lyrics";
    public bool IsBroadcast => Page == "broadcast";
    public bool IsBackups => Page == "backups";
    public bool IsUpdates => Page == "updates";

    private void OnMorePage(string page)
    {
        OnPropertyChanged(nameof(IsLyrics));
        OnPropertyChanged(nameof(IsBroadcast));
        OnPropertyChanged(nameof(IsBackups));
        OnPropertyChanged(nameof(IsUpdates));
        switch (page)
        {
            case "lyrics": LoadLyrics(); _ = LoadUsageAsync(); break;
            case "broadcast": LoadBroadcast(); break;
            case "backups": LoadBackups(); break;
            case "updates": LoadUpdates(); break;
        }
    }

    /// <summary>Set by the view: asks where to save a file. Returns the chosen path.</summary>
    public Func<string, string, Task<string?>>? SaveFileAs { get; set; }
    /// <summary>Set by the view: asks for a file to open, with a filter name and patterns.</summary>
    public Func<string, string[], Task<string?>>? OpenFileOf { get; set; }
    /// <summary>Set by the view: asks for a folder.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    // ================= Lyrics & CCLI =================

    public static IReadOnlyList<LyricAnimation> Animations { get; } = Enum.GetValues<LyricAnimation>();
    public static IReadOnlyList<int> LinesChoices { get; } = [0, 1, 2, 3, 4, 5, 6];
    public static IReadOnlyList<string> Periods { get; } = ["Last 3 months", "Last 6 months", "Last 12 months", "All time"];

    public ObservableCollection<ScriptureTheme> LyricThemeList { get; } = [];
    public ObservableCollection<UsageRow> Usage { get; } = [];
    [ObservableProperty] private string _ccliLicence = "";
    [ObservableProperty] private ScriptureTheme? _lyricTheme;
    [ObservableProperty] private LyricAnimation _lyricAnimation;
    [ObservableProperty] private int _lyricAnimationMs = 450;
    [ObservableProperty] private int _linesPerSlide;
    [ObservableProperty] private bool _copyrightOnFirst = true;
    [ObservableProperty] private string _usagePeriod = "Last 6 months";
    [ObservableProperty] private string? _lyricsMessage;
    [ObservableProperty] private string _usageSummary = "";
    private List<SongUse> _uses = [];

    private void LoadLyrics()
    {
        var s = Live.LyricsSettings;
        LyricThemeList.Clear();
        foreach (var t in Live.LyricThemes) LyricThemeList.Add(t);
        CcliLicence = s.CcliLicence;
        LyricTheme = LyricThemeList.FirstOrDefault(t => t.Id == s.DefaultThemeId) ?? LyricThemeList.FirstOrDefault();
        LyricAnimation = s.Animation;
        LyricAnimationMs = s.AnimationMs;
        LinesPerSlide = s.LinesPerSlide;
        CopyrightOnFirst = s.CopyrightOnFirstSlide;
        LyricsMessage = null;
    }

    [RelayCommand]
    private async Task SaveLyrics()
    {
        if (Live.Client is not { } c) return;
        var s = new LyricsSettings(LyricTheme?.Id ?? "lyrics-classic", LyricAnimation, Math.Clamp(LyricAnimationMs, 0, 3000),
            LinesPerSlide, CopyrightOnFirst, CcliLicence.Trim());
        LyricsMessage = await c.SendAsync(new SaveLyricsSettings(s)) ? "Saved. Every Lyrics operator gets these at once." : "Not connected to the Live Output PC.";
    }

    partial void OnUsagePeriodChanged(string value) => FillUsage();

    private async Task LoadUsageAsync()
    {
        if (Live.Client is not { } c) return;
        try
        {
            _uses = await c.GetJsonAsync<List<SongUse>>("library/usage") ?? [];
        }
        catch (Exception ex)
        {
            AppLog.Write("Usage report: " + ex.Message);
            _uses = [];
        }
        FillUsage();
    }

    private IEnumerable<SongUse> UsesInPeriod()
    {
        var months = UsagePeriod switch { "Last 3 months" => 3, "Last 6 months" => 6, "Last 12 months" => 12, _ => 0 };
        var from = months == 0 ? "" : DateTime.Today.AddMonths(-months).ToString("yyyy-MM-dd");
        return _uses.Where(u => string.CompareOrdinal(u.Date, from) >= 0);
    }

    private void FillUsage()
    {
        Usage.Clear();
        var rows = UsesInPeriod()
            .GroupBy(u => u.SongId)
            .Select(g => new UsageRow(g.Last().Title, g.Last().Author, g.Last().Ccli, g.Select(x => x.Date).Distinct().Count(), g.Max(x => x.Date)))
            .OrderByDescending(r => r.Times).ThenBy(r => r.Title)
            .ToList();
        foreach (var r in rows) Usage.Add(r);
        UsageSummary = rows.Count == 0
            ? "No songs shown in this period yet. Songs are counted once per day when they go live."
            : $"{rows.Count} songs, {rows.Sum(r => r.Times)} uses. Songs without a CCLI number: {rows.Count(r => string.IsNullOrWhiteSpace(r.Ccli))}.";
    }

    [RelayCommand]
    private async Task ExportUsage()
    {
        if (SaveFileAs is null) return;
        var path = await SaveFileAs($"CCLI song usage {DateTime.Today:yyyy-MM-dd}.csv", "csv");
        if (path is null) return;
        var sb = new StringBuilder("Title,Author,CCLI Song Number,Times used,Last used\r\n");
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        foreach (var r in Usage) sb.Append($"{Q(r.Title)},{Q(r.Author)},{Q(r.Ccli)},{r.Times},{r.LastUsed}\r\n");
        try
        {
            await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
            LyricsMessage = "Saved " + Path.GetFileName(path) + ". Enter these numbers in your CCLI reporting.";
        }
        catch (Exception ex) { LyricsMessage = "Could not save: " + ex.Message; }
    }

    // ================= Recording & streaming (Live Output PC only) =================

    public static IReadOnlyList<int> HeightChoices { get; } = [720, 1080];
    public static IReadOnlyList<int> FpsChoices { get; } = [25, 30, 50, 60];
    public ObservableCollection<string> SoundInputs { get; } = [];
    [ObservableProperty] private string _recordFolder = "";
    [ObservableProperty] private string _soundInput = NoSound;
    [ObservableProperty] private int _videoHeight = 1080;
    [ObservableProperty] private int _videoFps = 30;
    [ObservableProperty] private int _videoKbps = 6000;
    [ObservableProperty] private string _streamUrl = "";
    [ObservableProperty] private string _streamKey = "";
    [ObservableProperty] private bool _hasStreamKey;
    [ObservableProperty] private int _soundDelayMs;
    [ObservableProperty] private bool _cleanRecording;
    [ObservableProperty] private string _defaultServiceName = "Sunday Service";
    [ObservableProperty] private string? _broadcastMessage;
    private const string NoSound = "No sound (silent)";

    /// <summary>Record and stream NEX-Audio's stream mix (sent over the network), with the sound device as the backup.</summary>
    [ObservableProperty] private bool _nexAudioSound;

    public string EncoderText => Live.Engine.FfmpegAvailable
        ? "The recorder is ready. It uses the graphics card's encoder when it has one."
        : "The recorder (ffmpeg) was not found next to NEXORA. Reinstall NEXORA to record or stream.";

    private void LoadBroadcast()
    {
        var b = S.Store?.Broadcast ?? new BroadcastSettings();
        RecordFolder = b.RecordFolder ?? AppPaths.DefaultRecordingsDir;
        SoundInputs.Clear();
        SoundInputs.Add(NoSound);
        foreach (var d in Live.Engine.AudioDevices ?? []) SoundInputs.Add(d);
        if (b.AudioDevice is { } a && !SoundInputs.Contains(a)) SoundInputs.Add(a);
        SoundInput = "";
        SoundInput = b.AudioDevice ?? NoSound;
        VideoHeight = b.Height;
        VideoFps = b.Fps;
        VideoKbps = b.VideoKbps;
        StreamUrl = b.StreamUrl ?? "";
        StreamKey = "";
        HasStreamKey = !string.IsNullOrEmpty(b.StreamKey);
        SoundDelayMs = b.SoundDelayMs;
        CleanRecording = b.CleanRecording;
        NexAudioSound = b.NexAudioSound;
        DefaultServiceName = b.ServiceName;
        BroadcastMessage = null;
        OnPropertyChanged(nameof(EncoderText));
    }

    [RelayCommand]
    private async Task ChooseRecordFolder()
    {
        if (PickFolder is null) return;
        if (await PickFolder("Where should recordings go?") is { } f) RecordFolder = f;
    }

    [RelayCommand]
    private async Task SaveBroadcast()
    {
        if (Live.Client is not { } c) return;
        var url = StreamUrl.Trim();
        if (url.Length > 0 && !url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
        {
            BroadcastMessage = "The stream address starts with rtmp:// or rtmps:// (copy it from YouTube Studio or Facebook Live).";
            return;
        }
        var b = new BroadcastSettings(
            RecordFolder.Trim().Length == 0 ? null : RecordFolder.Trim(),
            SoundInput == NoSound ? null : SoundInput,
            VideoHeight, VideoFps, Math.Clamp(VideoKbps, 1000, 20000),
            url.Length == 0 ? null : url,
            StreamKey.Trim().Length == 0 ? null : StreamKey.Trim(),
            Math.Clamp(SoundDelayMs, 0, 2000), CleanRecording, true,
            DefaultServiceName.Trim().Length == 0 ? "Sunday Service" : DefaultServiceName.Trim(),
            NexAudioSound);
        if (!await c.SendAsync(new SaveBroadcastSettings(b))) { BroadcastMessage = "Not connected to the Live Output PC."; return; }
        if (StreamKey.Trim().Length > 0) HasStreamKey = true;
        StreamKey = "";
        BroadcastMessage = "Saved. The next recording or stream uses these settings.";
    }

    // ================= Backups & standby =================

    public ObservableCollection<BackupRow> NightlyBackups { get; } = [];
    [ObservableProperty] private string? _backupMessage;
    [ObservableProperty] private bool _backupBusy;
    [ObservableProperty] private bool _confirmPromote;
    public bool IsTerminal => !S.IsLiveOutputPc;
    public string StandbyText => Backups.StandbyTime is { } t
        ? $"This PC holds a copy of the Live Output PC's library from {t:dddd h:mm tt}. It refreshes every 30 minutes while connected."
        : "No copy yet. This PC fetches one within a few minutes of connecting to the Live Output PC.";
    public bool HasStandby => Backups.StandbyTime is not null;

    private void LoadBackups()
    {
        NightlyBackups.Clear();
        if (S.IsLiveOutputPc)
            foreach (var f in Directory.GetFiles(AppPaths.BackupsDir, "NEXORA backup *.zip").OrderByDescending(f => f))
                NightlyBackups.Add(new BackupRow(f));
        ConfirmPromote = false;
        BackupMessage = null;
        OnPropertyChanged(nameof(StandbyText));
        OnPropertyChanged(nameof(HasStandby));
    }

    [RelayCommand]
    private async Task DownloadBackup()
    {
        if (Live.Client is not { } c || SaveFileAs is null) return;
        var path = await SaveFileAs($"NEXORA backup {DateTime.Now:yyyy-MM-dd HHmm}.zip", "zip");
        if (path is null) return;
        BackupBusy = true;
        BackupMessage = "Making the backup…";
        try
        {
            BackupMessage = await c.DownloadPathAsync("backup", path)
                ? $"Saved {Path.GetFileName(path)}. Keep a copy off this PC (a USB drive or cloud folder)."
                : "The Live Output PC did not send the backup. Only admins can make one.";
        }
        catch (Exception ex) { BackupMessage = "Backup failed: " + ex.Message; }
        finally { BackupBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreFromFile()
    {
        if (OpenFileOf is null) return;
        var path = await OpenFileOf("NEXORA backups", ["*.zip"]);
        if (path is not null) await RestorePath(path);
    }

    [RelayCommand]
    private Task RestoreNightly(BackupRow row) => RestorePath(row.Path);

    private async Task RestorePath(string path)
    {
        if (Live.Client is not { } c) return;
        if (!Nexora.Server.LiveServer.IsBackup(path)) { BackupMessage = "That file is not a NEXORA backup."; return; }
        BackupBusy = true;
        BackupMessage = "Sending the backup to the Live Output PC…";
        try
        {
            await c.PostFileAsync("restore", path);
            BackupMessage = "Restoring. Every PC reconnects in a few seconds.";
        }
        catch (Exception ex) { BackupMessage = ex.Message; }
        finally { BackupBusy = false; }
    }

    [RelayCommand] private void AskPromote() => ConfirmPromote = true;
    [RelayCommand] private void CancelPromote() => ConfirmPromote = false;

    /// <summary>The Live Output PC has failed: this terminal takes over with its standby copy, then restarts.</summary>
    [RelayCommand]
    private async Task Promote()
    {
        if (!HasStandby) { BackupMessage = "There is no standby copy on this PC yet."; return; }
        BackupBusy = true;
        BackupMessage = "Taking over as the Live Output PC…";
        try
        {
            await S.StopAsync(stopOutput: true);
            var copy = Path.Combine(AppPaths.StandbyDir, "promote.zip");
            File.Copy(Backups.StandbyFile, copy, true);
            Backups.Restore(copy, becomeLiveOutput: true);
            AppLog.Write("This PC took over as Live Output");
            RestartApp();
        }
        catch (Exception ex)
        {
            BackupBusy = false;
            BackupMessage = "Could not take over: " + ex.Message;
            S.ReloadConfig();
            await S.StartAsync();
        }
    }

    public static void RestartApp()
    {
        try
        {
            if (Environment.ProcessPath is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch (Exception ex) { AppLog.Write("Restart: " + ex.Message); }
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
    }

    // ================= Updates =================

    [ObservableProperty] private string? _updateMessage;
    [ObservableProperty] private bool _updateBusy;
    public string ThisVersion => AppServices.Version;
    public string OfferedVersion => Live.Library?.OfferedVersion ?? "—";
    public bool UpdateOffered => Version.TryParse(Live.Library?.OfferedVersion, out var o) && Version.TryParse(AppServices.Version, out var me) && o != me;
    public string UpdateState => UpdateOffered
        ? $"The Live Output PC offers NEXORA {OfferedVersion}. This PC runs {ThisVersion}."
        : $"This PC runs NEXORA {ThisVersion}, the same as the Live Output PC.";
    public bool CanInstall => UpdateOffered && !UpdateBusy;
    partial void OnUpdateBusyChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
    public bool HasPrevious => File.Exists(Path.Combine(AppPaths.UpdatesDir, "previous", "offered.txt"));
    public string PreviousVersion => HasPrevious ? File.ReadAllText(Path.Combine(AppPaths.UpdatesDir, "previous", "offered.txt")).Trim() : "";

    private void LoadUpdates()
    {
        UpdateMessage = null;
        foreach (var n in new[] { nameof(ThisVersion), nameof(OfferedVersion), nameof(UpdateOffered), nameof(UpdateState), nameof(CanInstall), nameof(HasPrevious), nameof(PreviousVersion), nameof(OnlineState) })
            OnPropertyChanged(n);
    }

    /// <summary>Terminal (or the Live Output PC itself): install the version the Live Output PC offers, then restart.</summary>
    [RelayCommand]
    private async Task InstallUpdate()
    {
        UpdateBusy = true;
        try { UpdateMessage = await Updater.InstallOfferedAsync(m => Dispatcher.UIThread.Post(() => UpdateMessage = m)); }
        finally { UpdateBusy = false; }
    }

    public string OnlineState => Updater.AvailableOnline is { } v
        ? $"NEXORA {v} is available online. This PC runs {ThisVersion}."
        : $"This PC runs NEXORA {ThisVersion}. Press the button to look for a newer version online.";

    /// <summary>Live Output PC: get the newest version from the internet, give it to every PC, and install it here.</summary>
    [RelayCommand]
    private async Task UpdateOnline()
    {
        UpdateBusy = true;
        try { UpdateMessage = await Updater.UpdateFromInternetAsync(m => Dispatcher.UIThread.Post(() => UpdateMessage = m)); }
        finally { UpdateBusy = false; OnPropertyChanged(nameof(OnlineState)); }
    }

    /// <summary>Live Output PC: offer a new version to every PC from the update files (releases.win.json and the .nupkg).</summary>
    [RelayCommand]
    private async Task OfferUpdate()
    {
        if (OpenFileOf is null) return;
        var json = await OpenFileOf("NEXORA update list (releases.win.json)", ["releases.*.json", "*.json"]);
        if (json is null) return;
        try
        {
            var dir = Path.GetDirectoryName(json)!;
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(json));
            var full = doc.RootElement.GetProperty("Assets").EnumerateArray()
                .Where(a => a.GetProperty("Type").GetString() == "Full")
                .Select(a => (Version: a.GetProperty("Version").GetString()!, File: a.GetProperty("FileName").GetString()!))
                .OrderByDescending(a => Version.TryParse(a.Version.Split('-')[0], out var v) ? v : new Version())
                .FirstOrDefault();
            if (full.File is null || !File.Exists(Path.Combine(dir, full.File)))
            {
                UpdateMessage = "The package named in that list is not next to it. Keep the .nupkg file in the same folder.";
                return;
            }
            UpdateBusy = true;
            UpdateMessage = $"Offering NEXORA {full.Version}…";
            var text = await File.ReadAllTextAsync(json);
            await Task.Run(() => Updater.Offer(full.Version, Path.Combine(dir, full.File), text));
            UpdateMessage = $"Every PC is now offered NEXORA {full.Version}. Press Install on this PC. Terminals update by themselves when nobody is signed in on them.";
            S.Server?.RefreshLibrary();
        }
        catch (Exception ex) { UpdateMessage = "Could not offer the update: " + ex.Message; }
        finally { UpdateBusy = false; LoadUpdates(); }
    }

    /// <summary>Live Output PC: offer the previous version again. Each PC then goes back with Install.</summary>
    [RelayCommand]
    private async Task RollBack()
    {
        var updates = AppPaths.UpdatesDir;
        var prev = Path.Combine(updates, "previous");
        if (!Directory.Exists(prev)) return;
        try
        {
            await Task.Run(() =>
            {
                var swap = Path.Combine(updates, "swap");
                if (Directory.Exists(swap)) Directory.Delete(swap, true);
                Directory.CreateDirectory(swap);
                foreach (var f in Directory.GetFiles(updates)) File.Move(f, Path.Combine(swap, Path.GetFileName(f)));
                foreach (var f in Directory.GetFiles(prev)) File.Move(f, Path.Combine(updates, Path.GetFileName(f)));
                Directory.Delete(prev, true);
                Directory.Move(swap, prev);
            });
            UpdateMessage = $"Now offering NEXORA {File.ReadAllText(Path.Combine(updates, "offered.txt")).Trim()} again. Press Install on each PC to go back.";
            S.Server?.RefreshLibrary();
        }
        catch (Exception ex) { UpdateMessage = "Could not roll back: " + ex.Message; }
        LoadUpdates();
    }
}

public sealed record UsageRow(string Title, string Author, string Ccli, int Times, string LastUsed)
{
    public string CcliText => string.IsNullOrWhiteSpace(Ccli) ? "No CCLI number" : "CCLI " + Ccli;
}

public sealed class BackupRow(string path)
{
    public string Path => path;
    public string Name => System.IO.Path.GetFileNameWithoutExtension(path);
    public string Size => new FileInfo(path).Length is var n && n < 1024 * 1024 ? $"{n / 1024.0:0} KB" : $"{n / 1024.0 / 1024.0:0.0} MB";
}
