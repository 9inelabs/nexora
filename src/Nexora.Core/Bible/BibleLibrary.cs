namespace Nexora.Core.Bible;

/// <summary>
/// Every translation on this PC. The bundled ones ship inside the installer and are prepared
/// into fast .nxbible files on first start; translations the admin imports are kept beside them
/// and copied to every PC.
/// </summary>
public sealed class BibleLibrary : IDisposable
{
    private readonly string _bundledDir;
    private readonly string _dataDir;
    private readonly object _gate = new();
    private List<BibleTranslation> _all = [];

    public BibleLibrary(string bundledDir, string dataDir)
    {
        _bundledDir = bundledDir;
        _dataDir = dataDir;
    }

    /// <summary>The translations, in the order shown to operators.</summary>
    public IReadOnlyList<BibleTranslation> All
    {
        get { lock (_gate) return _all; }
    }

    public bool IsLoaded { get; private set; }
    public string? LoadError { get; private set; }

    public event Action? Changed;

    public string ImportedDir => Path.Combine(_dataDir, "imported");
    private string BundledCacheDir => Path.Combine(_dataDir, "bundled");

    public BibleTranslation? Get(string? abbrev)
    {
        if (abbrev is null) return null;
        lock (_gate) return _all.FirstOrDefault(t => string.Equals(t.Abbrev, abbrev, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsBundled(string abbrev) =>
        File.Exists(Path.Combine(BundledCacheDir, abbrev + BibleFile.Extension)) &&
        !File.Exists(Path.Combine(ImportedDir, abbrev + BibleFile.Extension));

    /// <summary>Prepares the bundled translations (first start only) and opens everything. Safe to call again.</summary>
    public void Load()
    {
        try
        {
            Directory.CreateDirectory(BundledCacheDir);
            Directory.CreateDirectory(ImportedDir);
            PrepareBundled();
            Reopen();
            LoadError = null;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
        IsLoaded = true;
        Changed?.Invoke();
    }

    private void PrepareBundled()
    {
        if (!Directory.Exists(_bundledDir)) return;
        foreach (var src in Directory.GetFiles(_bundledDir))
        {
            var name = Path.GetFileName(src);
            if (!(name.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                  name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xml.gz", StringComparison.OrdinalIgnoreCase) ||
                  name.EndsWith(".usfm.zip", StringComparison.OrdinalIgnoreCase)))
                continue;
            var bytes = File.ReadAllBytes(src);
            var hash = BibleImport.Hash(bytes);
            var abbrev = name.Split('.')[0];
            var target = Path.Combine(BundledCacheDir, BibleImport.CleanAbbrev(abbrev) + BibleFile.Extension);
            if (BibleFile.ReadInfo(target) is { } info && info.Hash == hash) continue;
            var result = BibleImport.Load(src);
            BibleFile.Write(result.Bible, target, "bundled", hash);
        }
    }

    private void Reopen()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(BundledCacheDir, "*" + BibleFile.Extension)) files[Path.GetFileNameWithoutExtension(f)] = f;
        // An imported file with the same short name replaces the bundled one.
        foreach (var f in Directory.GetFiles(ImportedDir, "*" + BibleFile.Extension)) files[Path.GetFileNameWithoutExtension(f)] = f;

        var opened = new List<BibleTranslation>();
        foreach (var f in files.Values)
        {
            try { opened.Add(BibleTranslation.Open(f)); }
            catch (Exception) { /* a damaged file is skipped; the importer can replace it */ }
        }
        opened = opened
            .OrderBy(t => t.Abbrev.Equals("KJV", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(t => t.Abbrev, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<BibleTranslation> old;
        lock (_gate)
        {
            old = _all;
            _all = opened;
        }
        foreach (var t in old) t.Dispose();
    }

    /// <summary>Saves an imported translation on this PC. Returns the path of the new .nxbible file.</summary>
    public string SaveImported(BibleText bible, string source, string hash)
    {
        Directory.CreateDirectory(ImportedDir);
        var target = Path.Combine(ImportedDir, BibleImport.CleanAbbrev(bible.Abbrev) + BibleFile.Extension);
        CloseFile(target);
        BibleFile.Write(bible, target, source, hash);
        Reopen();
        Changed?.Invoke();
        return target;
    }

    /// <summary>Adds a ready .nxbible file (for example copied from the Live Output PC).</summary>
    public void AddFile(string nxbiblePath)
    {
        var info = BibleFile.ReadInfo(nxbiblePath) ?? throw new InvalidDataException("That is not a NEXORA Bible file.");
        var target = Path.Combine(ImportedDir, BibleImport.CleanAbbrev(info.Abbrev) + BibleFile.Extension);
        if (!string.Equals(Path.GetFullPath(nxbiblePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            CloseFile(target);
            File.Copy(nxbiblePath, target, overwrite: true);
        }
        Reopen();
        Changed?.Invoke();
    }

    public bool RemoveImported(string abbrev)
    {
        var target = Path.Combine(ImportedDir, BibleImport.CleanAbbrev(abbrev) + BibleFile.Extension);
        if (!File.Exists(target)) return false;
        CloseFile(target);
        File.Delete(target);
        Reopen();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Imported translations on this PC, with their content hash (used to copy them to terminals).</summary>
    public IReadOnlyList<(BibleInfo Info, string Path)> ImportedFiles()
    {
        if (!Directory.Exists(ImportedDir)) return [];
        return Directory.GetFiles(ImportedDir, "*" + BibleFile.Extension)
            .Select(f => (Info: BibleFile.ReadInfo(f), Path: f))
            .Where(x => x.Info is not null)
            .Select(x => (x.Info!, x.Path))
            .ToList();
    }

    private void CloseFile(string path)
    {
        lock (_gate)
        {
            var open = _all.FirstOrDefault(t => string.Equals(Path.GetFullPath(t.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (open is null) return;
            _all = _all.Where(t => t != open).ToList();
            open.Dispose();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var t in _all) t.Dispose();
            _all = [];
        }
    }
}
