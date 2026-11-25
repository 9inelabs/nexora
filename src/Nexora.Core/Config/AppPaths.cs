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
}
