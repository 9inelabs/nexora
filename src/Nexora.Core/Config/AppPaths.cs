namespace Nexora.Core.Config;

/// <summary>
/// Where NEXORA keeps its files. Data lives outside the install folder so that
/// updates and reinstalls never touch the church's users, settings or library.
/// </summary>
public static class AppPaths
{
    private static string? _root;

    /// <summary>Overrides the data folder (tests, or NEXORA_DATA for running two copies on one PC).</summary>
    public static void UseRoot(string path) => _root = path;

    public static string Root
    {
        get
        {
            if (_root is not null) return _root;
            var env = Environment.GetEnvironmentVariable("NEXORA_DATA");
            _root = !string.IsNullOrWhiteSpace(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexoraData");
            return _root;
        }
    }

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string ConfigFile => Path.Combine(Ensure(Root), "this-pc.json");
    public static string LibraryFile => Path.Combine(Ensure(Path.Combine(Root, "library")), "nexora-library.db");
    public static string LogsDir => Ensure(Path.Combine(Root, "logs"));
    /// <summary>Bible translations: bundled ones are prepared here on first start, imported ones are kept here.</summary>
    public static string BiblesDir => Ensure(Path.Combine(Root, "bibles"));
    /// <summary>Uploaded backgrounds and other media, copied to every PC.</summary>
    public static string MediaDir => Ensure(Path.Combine(Root, "media"));
    /// <summary>The Live Output PC's master copies of imported Bibles and uploaded media, served to every PC.</summary>
    public static string LibraryBiblesDir => Ensure(Path.Combine(Root, "library", "bibles"));
    public static string LibraryMediaDir => Ensure(Path.Combine(Root, "library", "media"));
    /// <summary>Things only this PC needs, such as today's Scripture list.</summary>
    public static string LocalDir => Ensure(Path.Combine(Root, "local"));
    /// <summary>Videos and pictures in the Live Output PC's media list, copied here so a network drop cannot stop them.</summary>
    public static string VideosDir => Ensure(Path.Combine(Root, "videos"));
    /// <summary>The pictures and video loops that come with NEXORA (in the install folder, the same on every PC).</summary>
    public static string BuiltInBackgroundsDir => Path.Combine(AppContext.BaseDirectory, "Backgrounds");
    /// <summary>Automatic and manual backups.</summary>
    public static string BackupsDir => Ensure(Path.Combine(Root, "backups"));
    /// <summary>Update packages the Live Output PC offers to the other PCs.</summary>
    public static string UpdatesDir => Ensure(Path.Combine(Root, "updates"));
    /// <summary>A recent copy of the Live Output PC's library, so this PC can take over if that PC fails.</summary>
    public static string StandbyDir => Ensure(Path.Combine(Root, "standby"));
    /// <summary>Where recordings go when no folder is chosen.</summary>
    public static string DefaultRecordingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) is { Length: > 0 } v ? v : Root, "NEXORA Recordings");
}
