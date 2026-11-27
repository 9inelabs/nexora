using Velopack;
using Velopack.Sources;

namespace NexAudio.App.Services;

/// <summary>
/// Updating NEX-Audio. It takes the new version from the Live Output PC when it offers one (NEXORA's
/// Update brings NEX-Audio's files down with its own), otherwise from the internet. It never updates by
/// itself: the sound has to be stopped first, so an update can never cut a service.
/// </summary>
public static class DeskUpdater
{
    public const string RepoUrl = "https://github.com/9inelabs/nexora";
    public const string Channel = "nexaudio";

    private static int _busy;

    private static DeskServices S => DeskServices.Instance;

    private static IEnumerable<(string Name, IUpdateSource Source)> Sources()
    {
        if (S.Live.IsConnected && S.Live.Client?.ServerHttpRoot is { } root)
            yield return ("the Live Output PC", new SimpleWebSource(new Uri(root, "updates/").ToString()));
        yield return ("the internet", new GithubSource(RepoUrl, null, true));
    }

    /// <summary>Looks for a newer NEX-Audio. Returns the version and where it is, or null.</summary>
    public static async Task<(string Version, string From)?> FindAsync()
    {
        foreach (var (name, src) in Sources())
        {
            try
            {
                var mgr = new UpdateManager(src, new UpdateOptions { ExplicitChannel = Channel });
                if (!mgr.IsInstalled) return null;
                if (await mgr.CheckForUpdatesAsync() is { } info) return (info.TargetFullRelease.Version.ToString(), name);
            }
            catch (Exception ex) { AppLog.Write($"NEX-Audio update check ({name}): {ex.Message}"); }
        }
        return null;
    }

    /// <summary>Downloads and installs the newest NEX-Audio, then restarts. Returns a message when it does not restart.</summary>
    public static async Task<string> InstallAsync(bool soundOn, Action<string> say)
    {
        if (soundOn) return "Stop the sound first (top right), then update. Never update during a service.";
        if (Interlocked.Exchange(ref _busy, 1) == 1) return "An update is already running.";
        try
        {
            foreach (var (name, src) in Sources())
            {
                UpdateManager mgr;
                UpdateInfo? info;
                try
                {
                    mgr = new UpdateManager(src, new UpdateOptions { ExplicitChannel = Channel });
                    if (!mgr.IsInstalled) return "This copy of NEX-Audio was not installed with the installer, so it cannot update itself.";
                    say($"Looking on {name}…");
                    info = await mgr.CheckForUpdatesAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Write($"NEX-Audio update ({name}): {ex.Message}");
                    continue;
                }
                if (info is null) continue;
                var target = info.TargetFullRelease;
                say($"Downloading NEX-Audio {target.Version} from {name}…");
                await mgr.DownloadUpdatesAsync(info, p => say($"Downloading NEX-Audio {target.Version}… {p}%"));
                say("Installing. NEX-Audio restarts in a moment.");
                S.Audio.StopForUpdate();
                mgr.ApplyUpdatesAndRestart(target);
                return "Installing…";
            }
            return $"NEX-Audio is up to date ({DeskServices.Version}).";
        }
        catch (Exception ex)
        {
            AppLog.Write("NEX-Audio update: " + ex);
            return "Update failed: " + ex.Message;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
