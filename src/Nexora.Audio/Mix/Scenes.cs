using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexora.Audio.Mix;

/// <summary>What a recall changes.</summary>
[Flags]
public enum RecallScope
{
    None = 0,
    /// <summary>Faders, mutes, pans, send levels and the stream balance.</summary>
    Levels = 1,
    /// <summary>Filters, EQ, gate, compressor and every effect's settings on the strips.</summary>
    Processing = 2,
    /// <summary>The shared effects (reverbs, delays) and their returns.</summary>
    Effects = 4,
    /// <summary>Which buses exist, groups, routing and the output patch.</summary>
    Routing = 8,
    All = Levels | Processing | Effects | Routing,
}

/// <summary>A saved mix: a whole service's desk (a scene) or a moment within it (a snapshot).</summary>
public sealed class SavedMix
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..10];
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    public string SavedBy { get; set; } = "";
    /// <summary>Snapshots only: what the recall changes.</summary>
    public RecallScope Scope { get; set; } = RecallScope.All;
    /// <summary>Snapshots only: faders glide to the new levels over this many seconds.</summary>
    public double FadeSeconds { get; set; }
    public MixState Mix { get; set; } = new();
}

/// <summary>A short list for the screen (without the mix itself).</summary>
public sealed record SavedMixInfo(string Id, string Name, string Notes, DateTime SavedUtc, string SavedBy, RecallScope Scope, double FadeSeconds);

/// <summary>
/// Scenes (one per kind of service, the whole desk) and snapshots (quick cues inside a service: worship,
/// sermon, altar call), kept as files on the NEX-Audio PC.
/// </summary>
public sealed class SceneStore
{
    private readonly string _scenes, _snapshots;
    private readonly object _gate = new();

    public SceneStore(string dataDir)
    {
        _scenes = Path.Combine(dataDir, "scenes");
        _snapshots = Path.Combine(dataDir, "snapshots");
        Directory.CreateDirectory(_scenes);
        Directory.CreateDirectory(_snapshots);
    }

    private static readonly JsonSerializerOptions Json = new(MixState.Json) { WriteIndented = true };

    public IReadOnlyList<SavedMixInfo> Scenes => List(_scenes).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Snapshots in their running order.</summary>
    public IReadOnlyList<SavedMixInfo> Snapshots
    {
        get
        {
            var all = List(_snapshots).ToDictionary(s => s.Id);
            var order = LoadOrder();
            var result = order.Where(all.ContainsKey).Select(id => all[id]).ToList();
            result.AddRange(all.Values.Where(s => !order.Contains(s.Id)).OrderBy(s => s.SavedUtc));
            return result;
        }
    }

    private IEnumerable<SavedMixInfo> List(string dir)
    {
        lock (_gate)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                if (Path.GetFileName(f) == "order.json") continue;
                SavedMix? m = null;
                try { m = JsonSerializer.Deserialize<SavedMix>(File.ReadAllText(f), Json); }
                catch (Exception) { }
                if (m is not null) yield return new SavedMixInfo(m.Id, m.Name, m.Notes, m.SavedUtc, m.SavedBy, m.Scope, m.FadeSeconds);
            }
        }
    }

    public SavedMix? Load(string id, bool snapshot)
    {
        if (!SafeId(id)) return null;
        var f = Path.Combine(snapshot ? _snapshots : _scenes, id + ".json");
        lock (_gate)
        {
            try
            {
                if (!File.Exists(f)) return null;
                var m = JsonSerializer.Deserialize<SavedMix>(File.ReadAllText(f), Json);
                m?.Mix.Repair();
                return m;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public SavedMix Save(SavedMix m, bool snapshot)
    {
        if (!SafeId(m.Id)) m.Id = Guid.NewGuid().ToString("n")[..10];
        m.Name = string.IsNullOrWhiteSpace(m.Name) ? (snapshot ? "Snapshot" : "Scene") : m.Name.Trim();
        if (m.Name.Length > 40) m.Name = m.Name[..40];
        var dir = snapshot ? _snapshots : _scenes;
        lock (_gate)
        {
            var tmp = Path.Combine(dir, m.Id + ".tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(m, Json));
            File.Move(tmp, Path.Combine(dir, m.Id + ".json"), true);
            if (snapshot)
            {
                var order = LoadOrder();
                if (!order.Contains(m.Id)) order.Add(m.Id);
                SaveOrder(order);
            }
        }
        return m;
    }

    public bool Delete(string id, bool snapshot)
    {
        if (!SafeId(id)) return false;
        lock (_gate)
        {
            var f = Path.Combine(snapshot ? _snapshots : _scenes, id + ".json");
            if (!File.Exists(f)) return false;
            File.Delete(f);
            if (snapshot)
            {
                var order = LoadOrder();
                order.Remove(id);
                SaveOrder(order);
            }
            return true;
        }
    }

    /// <summary>Puts the snapshots in a new running order.</summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        lock (_gate) SaveOrder(ids.Where(SafeId).Distinct().ToList());
    }

    private List<string> LoadOrder()
    {
        try
        {
            var f = Path.Combine(_snapshots, "order.json");
            if (File.Exists(f)) return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(f)) ?? [];
        }
        catch (Exception) { }
        return [];
    }

    private void SaveOrder(List<string> order)
    {
        try { File.WriteAllText(Path.Combine(_snapshots, "order.json"), JsonSerializer.Serialize(order)); }
        catch (Exception) { }
    }

    private static bool SafeId(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>Merges a saved mix into the live one, as far as the scope allows and leaving recall-safe strips alone.</summary>
public static class SceneRecall
{
    public static MixState Apply(MixState current, MixState saved, RecallScope scope)
    {
        var result = current.Clone();
        var from = saved.Clone();
        from.Repair();

        if (scope.HasFlag(RecallScope.Routing))
        {
            // The shape of the desk: which buses exist and where everything goes.
            var keepSafe = result.AllStrips().Where(s => s.RecallSafe).ToDictionary(s => s.Id, s => s.Clone());
            result.Buses = from.Buses.Select(b => b.Clone()).ToList();
            result.Outputs = from.Outputs.Select(o => o.Clone()).ToList();
            result.StreamSettings = from.StreamSettings;
            result.Solo = from.Solo;
            foreach (var ch in result.Channels)
            {
                var src = from.Channels.FirstOrDefault(c => c.Id == ch.Id);
                if (src is null || ch.RecallSafe) continue;
                ch.ToMain = src.ToMain;
                ch.Groups = [.. src.Groups];
                ch.Sends = src.Sends.Select(x => x.Clone()).ToList();
                ch.Name = src.Name;
                ch.Colour = src.Colour;
            }
            // A recall-safe bus that still exists in the new layout keeps its own settings.
            for (var i = 0; i < result.Buses.Count; i++)
                if (keepSafe.TryGetValue(result.Buses[i].Id, out var safe)) result.Buses[i] = safe;
        }

        foreach (var dst in result.AllStrips())
        {
            if (dst.RecallSafe) continue;
            var src = from.Find(dst.Id);
            if (src is null) continue;
            var isFx = dst.Kind == StripKind.Fx;
            if (scope.HasFlag(RecallScope.Levels))
            {
                dst.Fader = src.Fader;
                dst.Mute = src.Mute;
                dst.Pan = src.Pan;
                dst.StreamLevel = src.StreamLevel;
                dst.StreamOn = src.StreamOn;
                foreach (var s in src.Sends)
                {
                    var d = dst.SendTo(s.Bus);
                    if (d is null)
                    {
                        if (result.Find(s.Bus) is { } bus && Routing.CanSend(dst, bus)) dst.Sends.Add(s.Clone());
                        continue;
                    }
                    d.Level = s.Level;
                    d.On = s.On;
                    d.Tap = s.Tap;
                    d.Pan = s.Pan;
                }
                // Sends the saved mix did not have go quiet.
                foreach (var d in dst.Sends.Where(d => src.SendTo(d.Bus) is null)) d.On = false;
            }
            if (isFx ? scope.HasFlag(RecallScope.Effects) : scope.HasFlag(RecallScope.Processing))
            {
                dst.Trim = src.Trim;
                dst.Invert = src.Invert;
                dst.DelayMs = src.DelayMs;
                dst.Hpf = src.Hpf.Clone();
                dst.Lpf = src.Lpf.Clone();
                dst.Eq = src.Eq.Clone();
                dst.Gate = src.Gate.Clone();
                dst.Comp = src.Comp.Clone();
                dst.Rack = src.Rack.Select(r => r.Clone()).ToList();
                dst.Preamp = src.Preamp;
                dst.Phantom = src.Phantom;
            }
        }
        result.Repair();
        return result;
    }

    /// <summary>A mix part way between two (0 = from, 1 = to): faders, sends and stream levels glide, everything else is taken from "to".</summary>
    public static MixState Blend(MixState from, MixState to, double t)
    {
        if (t >= 1) return to.Clone();
        var r = to.Clone();
        foreach (var dst in r.AllStrips())
        {
            if (from.Find(dst.Id) is not { } a) continue;
            dst.Fader = Lerp(a.Fader, dst.Fader, t);
            dst.StreamLevel = Lerp(a.StreamLevel, dst.StreamLevel, t);
            dst.Pan = a.Pan + (dst.Pan - a.Pan) * t;
            foreach (var s in dst.Sends)
                if (a.SendTo(s.Bus) is { } sa)
                    s.Level = Lerp(sa.On ? sa.Level : Dsp.Db.Floor, s.On ? s.Level : Dsp.Db.Floor, t);
        }
        return r;
    }

    /// <summary>Glides in dB but eases out of silence, so a fade-in does not jump.</summary>
    private static double Lerp(double a, double b, double t)
    {
        a = Math.Max(a, -60);
        b = Math.Max(b, -60);
        var v = a + (b - a) * t;
        return v <= -59.9 ? Dsp.Db.Floor : v;
    }
}
