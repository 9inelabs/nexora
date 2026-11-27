using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Nexora.Core.Config;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Nexora.App.Services;

/// <summary>
/// Updating NEXORA. The Live Output PC gets a new version from the internet (or from update files),
/// offers it to every PC over the church network and installs it on itself. Terminals then update
/// from the Live Output PC by themselves when nobody is signed in on them, or with one press.
/// Users, songs, settings and media live outside the install folder, so nothing is lost.
/// </summary>
public static class Updater
{
    public const string RepoUrl = "https://github.com/9inelabs/nexora";

    private static int _busy;
    public static bool Busy => _busy == 1;

    private static AppServices S => AppServices.Instance;

    /// <summary>The version this PC is offered by the Live Output PC, if it is newer than this PC's.</summary>
    public static Version? NewerOffered()
    {
        var offered = S.Live.Library?.OfferedVersion;
        return Version.TryParse(offered, out var o) && Version.TryParse(AppServices.Version, out var me) && o > me ? o : null;
    }

    /// <summary>
    /// Live Output PC: looks for a newer NEXORA online. When there is one, downloads it, offers it to every PC
    /// and installs it here (NEXORA restarts). Returns a message when it does not restart.
    /// </summary>
    public static async Task<string> UpdateFromInternetAsync(Action<string> say)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return "An update is already running.";
        try
        {
            if (S.Live.Modes.ServiceMode) return "Service mode is on. Update after the service.";
            var mgr = new UpdateManager(new GithubSource(RepoUrl, null, true), new UpdateOptions());
            if (!mgr.IsInstalled) return "This copy of NEXORA was not installed with the installer, so it cannot update itself.";
            say("Looking for a new version…");
            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) return $"NEXORA is up to date ({AppServices.Version}).";
            var target = info.TargetFullRelease;
            say($"Downloading NEXORA {target.Version}…");
            await mgr.DownloadUpdatesAsync(info, p => say($"Downloading NEXORA {target.Version}… {p}%"));

            // Hand the same version to every PC on the church network.
            var package = Path.Combine(VelopackLocator.Current.PackagesDir ?? "", target.FileName);
            if (File.Exists(package))
            {
                say("Offering it to every PC…");
                await Task.Run(() => Offer(target.Version.ToString(), package, FeedFor(target)));
                S.Server?.RefreshLibrary();
                // NEX-Audio is its own app with its own installer: bring its matching version down too,
                // so the sound PC can update from here without the internet.
                say("Fetching NEX-Audio for the sound PC…");
                await FetchNexAudioAsync(target.Version.ToString());
            }
            else AppLog.Write("Update: downloaded package not found at " + package);

            say("Installing. NEXORA restarts in a moment.");
            await S.StopAsync(stopOutput: true);
            mgr.ApplyUpdatesAndRestart(target);
            return "Installing…";
        }
        catch (Exception ex)
        {
            AppLog.Write("Online update: " + ex);
            return ex is HttpRequestException or TaskCanceledException
                ? "Could not reach the internet to look for updates. Check this PC's internet connection and try again."
                : "Update failed: " + ex.Message;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Installs the version the Live Output PC offers, then restarts. Returns a message when it does not restart.</summary>
    public static async Task<string> InstallOfferedAsync(Action<string> say)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return "An update is already running.";
        try
        {
            if (S.Live.Modes.ServiceMode) return "Service mode is on. Update after the service.";
            IUpdateSource src;
            if (S.IsLiveOutputPc) src = new SimpleFileSource(new DirectoryInfo(AppPaths.UpdatesDir));
            else if (S.Live.Client?.ServerHttpRoot is { } root) src = new SimpleWebSource(new Uri(root, "updates/").ToString());
            else return "Not connected to the Live Output PC.";
            var mgr = new UpdateManager(src, new UpdateOptions { AllowVersionDowngrade = true });
            if (!mgr.IsInstalled) return "This copy of NEXORA was not installed with the installer, so it cannot update itself.";
            say("Checking…");
            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) return "Already up to date.";
            var target = info.TargetFullRelease;
            say($"Downloading NEXORA {target.Version}…");
            await mgr.DownloadUpdatesAsync(info, p => say($"Downloading NEXORA {target.Version}… {p}%"));
            say("Installing. NEXORA restarts in a moment.");
            await S.StopAsync(stopOutput: true);
            mgr.ApplyUpdatesAndRestart(target);
            return "Installing…";
        }
        catch (Exception ex)
        {
            AppLog.Write("Update: " + ex);
            return "Update failed: " + ex.Message;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>
    /// Live Output PC: makes this version the one every PC is offered. What was offered before is kept,
    /// so it can be offered again (roll back).
    /// </summary>
    public static void Offer(string version, string packagePath, string feedJson)
    {
        var updates = AppPaths.UpdatesDir;
        var prev = Path.Combine(updates, "previous");
        // NEX-Audio's files sit beside NEXORA's and are managed by FetchNexAudioAsync.
        var mine = Directory.GetFiles(updates).Where(f => !IsNexAudioFile(Path.GetFileName(f))).ToList();
        if (File.Exists(Path.Combine(updates, "offered.txt")))
        {
            if (Directory.Exists(prev)) Directory.Delete(prev, true);
            Directory.CreateDirectory(prev);
            foreach (var f in mine) File.Move(f, Path.Combine(prev, Path.GetFileName(f)));
        }
        else
            foreach (var f in mine) File.Delete(f);
        File.WriteAllText(Path.Combine(updates, "releases.win.json"), feedJson);
        File.Copy(packagePath, Path.Combine(updates, Path.GetFileName(packagePath)), true);
        File.WriteAllText(Path.Combine(updates, "offered.txt"), version);
    }

    private static bool IsNexAudioFile(string name) =>
        name.StartsWith("NexAudio-", StringComparison.OrdinalIgnoreCase) || name.Equals("releases.nexaudio.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Live Output PC: downloads NEX-Audio's package from the same release as this NEXORA version into the
    /// updates folder, which the sound PC reads over the church network. Quiet when it cannot (logged only).
    /// </summary>
    public static async Task FetchNexAudioAsync(string version)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var baseUrl = $"{RepoUrl}/releases/download/v{version}/";
            var feed = await http.GetStringAsync(baseUrl + "releases.nexaudio.json");
            using var doc = JsonDocument.Parse(feed);
            var asset = doc.RootElement.GetProperty("Assets").EnumerateArray()
                .Where(a => string.Equals(a.GetProperty("Type").GetString(), "Full", StringComparison.OrdinalIgnoreCase))
                .LastOrDefault(a => a.GetProperty("FileName").GetString()?.Contains(version, StringComparison.Ordinal) == true);
            if (asset.ValueKind != JsonValueKind.Object) return;
            var full = asset.GetProperty("FileName").GetString()!;
            // Only the full package is kept here, so the list names only that (no small "delta" updates).
            var onlyFull = "{\"Assets\":[" + asset.GetRawText() + "]}";
            var updates = AppPaths.UpdatesDir;
            var tmp = Path.Combine(updates, full + ".part");
            await using (var src = await http.GetStreamAsync(baseUrl + full))
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst);
            foreach (var old in Directory.GetFiles(updates, "NexAudio-*.nupkg")) File.Delete(old);
            File.Move(tmp, Path.Combine(updates, full), true);
            await File.WriteAllTextAsync(Path.Combine(updates, "releases.nexaudio.json"), onlyFull);
            AppLog.Write($"NEX-Audio {version} is ready for the sound PC.");
        }
        catch (Exception ex) { AppLog.Write("Could not fetch NEX-Audio for the sound PC: " + ex.Message); }
    }

    /// <summary>
    /// Live Output PC, at start: if this PC runs a newer version than it offers (it was updated with the installer),
    /// offer its own version, so the terminals follow it.
    /// </summary>
    public static void OfferOwnVersionIfNewer()
    {
        try
        {
            if (!VelopackLocator.IsCurrentSet || VelopackLocator.Current.IsPortable) return;
            if (!Version.TryParse(AppServices.Version, out var me)) return;
            var offeredFile = Path.Combine(AppPaths.UpdatesDir, "offered.txt");
            if (File.Exists(offeredFile) && Version.TryParse(File.ReadAllText(offeredFile).Trim(), out var offered) && offered >= me) return;
            var own = VelopackLocator.Current.GetLatestLocalFullPackage();
            if (own is null || !Version.TryParse(own.Version.ToString(), out var ownV) || ownV != me) return;
            var package = Path.Combine(VelopackLocator.Current.PackagesDir ?? "", own.FileName);
            if (!File.Exists(package)) return;
            Offer(own.Version.ToString(), package, FeedFor(own));
            AppLog.Write($"Offering this PC's own version {me} to every PC.");
        }
        catch (Exception ex) { AppLog.Write("Offer own version: " + ex.Message); }
    }

    /// <summary>An update list (releases.win.json) naming just this one full package.</summary>
    private static string FeedFor(VelopackAsset a)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("Assets");
            w.WriteStartObject();
            w.WriteString("PackageId", a.PackageId);
            w.WriteString("Version", a.Version.ToString());
            w.WriteString("Type", "Full");
            w.WriteString("FileName", a.FileName);
            w.WriteString("SHA1", a.SHA1);
            w.WriteString("SHA256", a.SHA256);
            w.WriteNumber("Size", a.Size);
            w.WriteString("NotesMarkdown", a.NotesMarkdown ?? "");
            w.WriteString("NotesHTML", a.NotesHTML ?? "");
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Terminals: when the Live Output PC offers a newer version and nobody is signed in here,
    /// update by itself. Called whenever the offer or the signed-in user changes.
    /// </summary>
    public static void MaybeUpdateByItself()
    {
        if (S.IsLiveOutputPc || Busy || NewerOffered() is not { } v) return;
        if (S.Live.User is not null)
        {
            if (_told != v)
            {
                _told = v;
                S.Live.RaiseToast($"NEXORA {v} is ready. It installs by itself when this PC is signed out, or now from Settings › Updates.");
            }
            return;
        }
        // Give the connection a moment to settle, then check again that nobody signed in meanwhile.
        DispatcherTimer.RunOnce(async () =>
        {
            if (S.Live.User is not null || NewerOffered() is null) return;
            S.Live.RaiseToast($"Updating to NEXORA {v}…");
            var result = await InstallOfferedAsync(_ => { });
            if (result != "Installing…") AppLog.Write("Automatic update: " + result);
        }, TimeSpan.FromSeconds(20));
    }

    private static Version? _told;

    /// <summary>Live Output PC, once at start: if a newer NEXORA is online, says so. Quiet when offline.</summary>
    public static async Task TellIfNewerOnlineAsync()
    {
        try
        {
            if (!S.IsLiveOutputPc) return;
            var mgr = new UpdateManager(new GithubSource(RepoUrl, null, true), new UpdateOptions());
            if (!mgr.IsInstalled) return;
            if (await mgr.CheckForUpdatesAsync() is { } info)
            {
                var v = info.TargetFullRelease.Version.ToString();
                AppServices.OnUi(() =>
                {
                    AvailableOnline = v;
                    S.Live.RaiseToast($"NEXORA {v} is available. Settings › Updates › Update NEXORA.");
                });
            }
        }
        catch (Exception ex) { AppLog.Write("Update check: " + ex.Message); }
    }

    /// <summary>A newer version seen online at start, if any.</summary>
    public static string? AvailableOnline { get; private set; }
}
