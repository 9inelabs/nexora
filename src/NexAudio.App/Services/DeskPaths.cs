using Nexora.Core.Config;

namespace NexAudio.App.Services;

/// <summary>
/// Where NEX-Audio keeps its files: its own data folder (NexAudioData), apart from NEXORA's, so the two
/// apps update and uninstall separately. The first time NEX-Audio starts on a PC where NEXORA already ran
/// the desk, it brings that desk across (mix, scenes, presets, patch, plugins).
/// </summary>
public static class DeskPaths
{
    /// <summary>NEX-Audio's own data folder (NEXAUDIO_DATA overrides it, for tests or two copies on one PC).</summary>
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("NEXAUDIO_DATA");
            return !string.IsNullOrWhiteSpace(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexAudioData");
        }
    }

    /// <summary>NEXORA's data folder on this PC, if NEXORA is here too.</summary>
    public static string NexoraRoot
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("NEXORA_DATA");
            return !string.IsNullOrWhiteSpace(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexoraData");
        }
    }

    /// <summary>Points the shared code (logs, settings, the engine) at NEX-Audio's folder. Call first thing.</summary>
    public static void Use()
    {
        Directory.CreateDirectory(Root);
        AppPaths.UseRoot(Root);
    }

    /// <summary>NEXORA's own settings on this PC (to find the Live Output PC without asking again).</summary>
    public static ThisPcConfig? NexoraConfig()
    {
        try
        {
            var file = Path.Combine(NexoraRoot, "this-pc.json");
            if (!File.Exists(file)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<ThisPcConfig>(File.ReadAllText(file), Nexora.Core.Protocol.Wire.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Brings across the desk NEXORA ran before NEX-Audio became its own app, once: only when NEX-Audio has
    /// no desk of its own yet. If the sound was on, it comes back on under NEX-Audio.
    /// </summary>
    public static void BringAcrossFromNexora(Action<string> log)
    {
        try
        {
            var from = Path.Combine(NexoraRoot, "audio");
            var to = Path.Combine(Root, "audio");
            if (!Directory.Exists(from) || Directory.Exists(to)) return;
            if (Path.GetFullPath(from) == Path.GetFullPath(to)) return;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(from, file);
                var name = Path.GetFileName(rel);
                if (name.Equals("engine.stop", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: false);
            }
            var local = Path.Combine(NexoraRoot, "local", "nex-audio.json");
            if (File.Exists(local))
            {
                Directory.CreateDirectory(Path.Combine(Root, "local"));
                File.Copy(local, Path.Combine(Root, "local", "nex-audio.json"), overwrite: false);
            }
            log("Brought the NEX-Audio desk across from NEXORA's folder.");
        }
        catch (Exception ex)
        {
            log("Could not bring the desk across from NEXORA: " + ex.Message);
        }
    }
}
