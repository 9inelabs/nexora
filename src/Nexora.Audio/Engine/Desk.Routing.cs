using Nexora.Audio.Mix;

namespace Nexora.Audio.Engine;

/// <summary>One strip's place in the delay line-up, in milliseconds, for the patch page.</summary>
public sealed record StripLatency(string Id, double OwnMs, double ArrivesMs, double LeavesMs);

public sealed partial class Desk
{
    private RoutingStore? _routing;
    private RoutingStore Routings => _routing ??= new RoutingStore(_dir);

    /// <summary>Saves the live routing as a layout (replacing the one with this id, if given).</summary>
    public RoutingLayout SaveRouting(string? id, string name, string notes, string by)
    {
        var p = Routings.Save(id, name, notes, by, Mix);
        Changed?.Invoke();
        return p;
    }

    /// <summary>What recalling a routing layout would change now, without changing anything.</summary>
    public RecallPreview? PreviewRouting(string id)
    {
        var layout = Routings.Load(id);
        if (layout is null) return null;
        var now = Mix;
        var after = RoutingRecall.Apply(now, layout.Mix);
        var (changes, safe, strips) = RecallDiff.Compare(now, after, layout.Mix);
        return new RecallPreview("routing:" + layout.Id, layout.Name, false, RecallScope.Routing, 0, changes, safe, strips);
    }

    /// <summary>Puts a routing layout on the desk (it can be undone).</summary>
    public bool RecallRouting(string id)
    {
        var layout = Routings.Load(id);
        if (layout is null) return false;
        return Change(s =>
        {
            var after = RoutingRecall.Apply(s, layout.Mix);
            s.Channels = after.Channels; s.Buses = after.Buses; s.Main = after.Main; s.Mono = after.Mono;
            s.StreamSettings = after.StreamSettings; s.Outputs = after.Outputs; s.Tone = after.Tone;
            return true;
        }, $"recall the routing \"{layout.Name}\"");
    }

    public bool DeleteRouting(string id)
    {
        var ok = Routings.Delete(id);
        if (ok) Changed?.Invoke();
        return ok;
    }

    /// <summary>Every channel that has an alternate input switches to it (true) or back to its main input (false).</summary>
    public bool UseAlternateInputs(bool alt) =>
        Change(s =>
        {
            var any = false;
            foreach (var c in s.Channels.Where(c => c.AltInput > 0 && !c.RecallSafe))
            {
                any |= c.UseAlt != alt;
                c.UseAlt = alt;
            }
            return any;
        }, alt ? "switch to the alternate inputs" : "switch back to the main inputs");

    private IReadOnlyList<StripLatency> LatencyList()
    {
        if (_engine is not { } e) return [];
        var ms = 1000.0 / e.SampleRate;
        return e.Latencies.Values
            .Where(l => l.Own > 0 || l.Arrives > 0)
            .Select(l => new StripLatency(l.Id, l.Own * ms, l.Arrives * ms, l.Leaves * ms))
            .ToList();
    }
}
