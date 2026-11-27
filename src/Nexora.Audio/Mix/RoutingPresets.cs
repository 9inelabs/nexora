using System.Text.Json;

namespace Nexora.Audio.Mix;

/// <summary>
/// A saved routing layout: which buses exist, which input feeds each channel, every route to the main mix, group and send,
/// the direct outs and the output patch. Recalling one leaves the sound alone (EQ, dynamics, effects, faders and speaker processing).
/// </summary>
public sealed class RoutingLayout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..10];
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    public string SavedBy { get; set; } = "";
    /// <summary>Comes with NEX-Audio (cannot be deleted).</summary>
    public bool BuiltIn { get; set; }
    public MixState Mix { get; set; } = new();
}

public sealed record RoutingInfo(string Id, string Name, string Notes, DateTime SavedUtc, string SavedBy, bool BuiltIn);

public static class RoutingRecall
{
    /// <summary>The built-in "start again" layout: every channel on its own input to the main mix, the usual buses, outputs passing inputs straight through.</summary>
    public const string FactoryId = "factory";

    public static RoutingLayout Factory() => new()
    {
        Id = FactoryId, Name = "Start again (factory routing)", BuiltIn = true, SavedUtc = DateTime.UnixEpoch,
        Notes = "Channel N hears input N and goes to the main mix, the usual buses, outputs pass their input straight through.",
        Mix = MixState.CreateDefault(),
    };

    /// <summary>Only the routing of a mix, for saving as a layout.</summary>
    public static MixState Capture(MixState s)
    {
        var c = s.Clone();
        c.Tone = new ToneSettings();
        return c;
    }

    /// <summary>
    /// The live mix with this layout's routing. Buses the layout has keep their own sound if they already exist; buses it does
    /// not have are removed. Recall-safe strips keep their routing.
    /// </summary>
    public static MixState Apply(MixState current, MixState layout)
    {
        var result = current.Clone();
        var from = layout.Clone();
        from.Repair();

        // Which buses exist.
        var buses = new List<Strip>();
        foreach (var b in from.Buses)
        {
            var live = result.Buses.FirstOrDefault(x => x.Id == b.Id && x.Kind == b.Kind);
            if (live is null)
            {
                buses.Add(b.Clone());
                continue;
            }
            if (!live.RecallSafe) TakeRouting(live, b);
            buses.Add(live);
        }
        // A recall-safe bus stays even when the layout does not have it.
        foreach (var b in result.Buses.Where(x => x.RecallSafe && buses.All(y => y.Id != x.Id))) buses.Add(b);
        result.Buses = buses;

        foreach (var ch in result.Channels)
            if (!ch.RecallSafe && from.Channels.FirstOrDefault(c => c.Id == ch.Id) is { } src)
                TakeRouting(ch, src);
        if (!result.Main.RecallSafe) result.Main.Sends = from.Main.Sends.Select(x => x.Clone()).ToList();
        if (!result.Mono.RecallSafe) result.Mono.Sends = from.Mono.Sends.Select(x => x.Clone()).ToList();
        result.StreamSettings.Source = from.StreamSettings.Source;

        // The output patch: what each output plays and its name. The speaker processing stays.
        foreach (var o in result.Outputs)
            if (from.Outputs.FirstOrDefault(x => x.Number == o.Number) is { } fo)
            {
                o.Source = fo.Source;
                o.Name = fo.Name;
            }
        if (result.Tone.Bus.Length > 0 && result.Find(result.Tone.Bus) is null)
        {
            result.Tone.Bus = "";
            result.Tone.On = false;
        }
        result.Repair();
        return result;
    }

    private static void TakeRouting(Strip dst, Strip src)
    {
        dst.Name = src.Name;
        dst.Colour = src.Colour;
        if (dst.Kind != StripKind.Channel) dst.Stereo = src.Stereo;
        dst.Input = src.Input;
        dst.AltInput = src.AltInput;
        dst.UseAlt = src.UseAlt;
        dst.DirectTap = src.DirectTap;
        dst.ToMain = src.ToMain;
        dst.SendsFrom = src.SendsFrom;
        dst.Groups = [.. src.Groups];
        dst.Sends = src.Sends.Select(x => x.Clone()).ToList();
    }
}

/// <summary>Routing layouts, kept as files on the NEX-Audio PC (audio data folder, "routing").</summary>
public sealed class RoutingStore
{
    private readonly string _dir;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(MixState.Json) { WriteIndented = true };

    public RoutingStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "routing");
        Directory.CreateDirectory(_dir);
    }

    public IReadOnlyList<RoutingInfo> List()
    {
        var f0 = RoutingRecall.Factory();
        var list = new List<RoutingInfo>();
        lock (_gate)
            foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<RoutingLayout>(File.ReadAllText(f), Json);
                    if (p is not null) list.Add(new RoutingInfo(p.Id, p.Name, p.Notes, p.SavedUtc, p.SavedBy, false));
                }
                catch (Exception) { }
            }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Append(new RoutingInfo(f0.Id, f0.Name, f0.Notes, f0.SavedUtc, "", true)).ToList();
    }

    public RoutingLayout? Load(string id)
    {
        if (id == RoutingRecall.FactoryId) return RoutingRecall.Factory();
        if (!SafeId(id)) return null;
        lock (_gate)
        {
            try
            {
                var f = Path.Combine(_dir, id + ".json");
                if (!File.Exists(f)) return null;
                var p = JsonSerializer.Deserialize<RoutingLayout>(File.ReadAllText(f), Json);
                p?.Mix.Repair();
                return p;
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Saves the routing of this mix. With the id of an existing layout, it is replaced (keeping its name when none is given).</summary>
    public RoutingLayout Save(string? id, string name, string notes, string by, MixState mix)
    {
        var old = id is { Length: > 0 } && id != RoutingRecall.FactoryId ? Load(id) : null;
        var p = new RoutingLayout
        {
            Id = old?.Id ?? Guid.NewGuid().ToString("n")[..10],
            Name = string.IsNullOrWhiteSpace(name) ? old?.Name ?? "Routing" : name.Trim(),
            Notes = (notes ?? "").Trim(),
            SavedBy = by ?? "",
            Mix = RoutingRecall.Capture(mix),
        };
        if (p.Name.Length > 40) p.Name = p.Name[..40];
        if (p.Notes.Length > 200) p.Notes = p.Notes[..200];
        lock (_gate)
        {
            var f = Path.Combine(_dir, p.Id + ".json");
            File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(p, Json));
            File.Move(f + ".tmp", f, true);
        }
        return p;
    }

    public bool Delete(string id)
    {
        if (!SafeId(id) || id == RoutingRecall.FactoryId) return false;
        lock (_gate)
        {
            var f = Path.Combine(_dir, id + ".json");
            if (!File.Exists(f)) return false;
            File.Delete(f);
            return true;
        }
    }

    private static bool SafeId(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}
