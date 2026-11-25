using System.Text.Json;
using Nexora.Core.Model;
using Nexora.Core.Net;
using Nexora.Core.Protocol;

namespace Nexora.Core.Songs;

/// <summary>
/// This PC's copy of the song library. It loads the whole library from the Live Output PC once, then
/// follows each change. A copy is kept on disk, so the songs are there at once after a restart.
/// </summary>
public sealed class SongCache : IDisposable
{
    private readonly string _file;
    private readonly object _gate = new();
    private Dictionary<string, Song> _songs = new();
    private long _version = -1;
    private NexoraClient? _client;
    private int _loading;

    public SongCache(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), Wire.Options) is { } saved)
            {
                _songs = saved.Songs.ToDictionary(s => s.Id);
                _version = saved.Version;
            }
        }
        catch { }
    }

    private sealed record Saved(long Version, List<Song> Songs);

    /// <summary>Raised (on a background thread) when songs change.</summary>
    public event Action? Changed;

    public IReadOnlyList<Song> All
    {
        get { lock (_gate) return _songs.Values.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public int Count { get { lock (_gate) return _songs.Count; } }

    public Song? Get(string id)
    {
        lock (_gate) return _songs.GetValueOrDefault(id);
    }

    public void Attach(NexoraClient client)
    {
        _client = client;
        client.MessageReceived += OnMessage;
        if (client.Library is { } li) Check(li.SongsVersion);
    }

    private void OnMessage(Message m)
    {
        switch (m)
        {
            case LibraryInfo li:
                Check(li.SongsVersion);
                break;
            case SongsChanged sc:
                bool reload;
                lock (_gate)
                {
                    reload = _version >= 0 && sc.Version != _version + 1 && sc.Version > _version + 1;
                    foreach (var s in sc.Saved) _songs[s.Id] = s;
                    foreach (var id in sc.Deleted) _songs.Remove(id);
                    _version = sc.Version;
                }
                Save();
                Changed?.Invoke();
                if (reload) _ = LoadAllAsync();
                break;
        }
    }

    private void Check(long serverVersion)
    {
        long mine;
        lock (_gate) mine = _version;
        if (serverVersion != mine) _ = LoadAllAsync();
    }

    /// <summary>Reloads the whole library from the Live Output PC.</summary>
    public async Task LoadAllAsync()
    {
        if (_client is not { } c || Interlocked.Exchange(ref _loading, 1) == 1) return;
        try
        {
            var version = c.Library?.SongsVersion ?? 0;
            var list = await c.GetJsonAsync<List<Song>>("library/songs");
            if (list is null) return;
            lock (_gate)
            {
                _songs = list.ToDictionary(s => s.Id);
                _version = version;
            }
            Save();
            Changed?.Invoke();
        }
        catch { }
        finally { Interlocked.Exchange(ref _loading, 0); }
    }

    private void Save()
    {
        try
        {
            Saved saved;
            lock (_gate) saved = new Saved(_version, _songs.Values.ToList());
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(saved, Wire.Options));
            File.Move(tmp, _file, overwrite: true);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_client is not null) _client.MessageReceived -= OnMessage;
        _client = null;
    }
}
