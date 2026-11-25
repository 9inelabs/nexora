using Nexora.Core.Bible;
using Nexora.Core.Config;
using Nexora.Core.Net;

namespace Nexora.App.Services;

/// <summary>
/// Keeps this PC's copy of the shared library in step with the Live Output PC: imported Bible
/// translations and uploaded backgrounds are copied here as soon as they appear, and removed
/// when the admin removes them.
/// </summary>
public sealed class LibrarySync : IDisposable
{
    private readonly NexoraClient _client;
    private readonly BibleLibrary _bibles;
    private readonly Func<Task> _biblesReady;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private bool _pending;

    public LibrarySync(NexoraClient client, BibleLibrary bibles, Func<Task> biblesReady)
    {
        _client = client;
        _bibles = bibles;
        _biblesReady = biblesReady;
        _client.LibraryChanged += OnChanged;
    }

    /// <summary>Raised after Bibles were added or removed on this PC.</summary>
    public static event Action? BiblesUpdated;

    private void OnChanged()
    {
        _pending = true;
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        if (!await _gate.WaitAsync(0)) return; // a pass is running; it will loop
        try
        {
            while (_pending && !_cts.IsCancellationRequested)
            {
                _pending = false;
                try { await SyncOnceAsync(_cts.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Write("Library copy failed: " + ex.Message); }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        if (_client.Library is not { } lib) return;
        await _biblesReady();

        var changed = false;
        var local = _bibles.ImportedFiles().ToDictionary(x => Path.GetFileName(x.Path), x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var f in lib.Bibles)
        {
            if (local.TryGetValue(f.Name, out var have) && BibleImport.Hash(await File.ReadAllBytesAsync(have.Path, ct)) == f.Hash) continue;
            var tmp = Path.Combine(AppPaths.BiblesDir, "." + f.Name + ".download");
            if (!await _client.DownloadAsync("bibles", f.Name, tmp, ct)) continue;
            try
            {
                if (BibleImport.Hash(await File.ReadAllBytesAsync(tmp, ct)) != f.Hash) continue;
                _bibles.AddFile(tmp);
                changed = true;
                AppLog.Write($"Copied the {f.Title.Split('|')[0]} Bible from the Live Output PC");
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
        foreach (var name in local.Keys.Where(n => lib.Bibles.All(b => !string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase))))
        {
            if (_bibles.RemoveImported(Path.GetFileNameWithoutExtension(name))) changed = true;
        }

        foreach (var m in lib.Media)
        {
            var target = Path.Combine(AppPaths.MediaDir, m.Name);
            if (File.Exists(target) || File.Exists(Path.Combine(AppPaths.LibraryMediaDir, m.Name))) continue;
            await _client.DownloadAsync("media", m.Name, target, ct);
        }

        if (changed) BiblesUpdated?.Invoke();
    }

    public void Dispose()
    {
        _client.LibraryChanged -= OnChanged;
        _cts.Cancel();
    }
}
