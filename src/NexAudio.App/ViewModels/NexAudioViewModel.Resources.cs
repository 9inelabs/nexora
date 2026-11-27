using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexora.Audio.Mix;

namespace NexAudio.App.ViewModels;

/// <summary>One shared pool on the resource page (colour strips, rack slots, nodes).</summary>
public sealed record PoolRow(string Name, string Detail, int Used, int Limit, string Colour)
{
    public string UsedText => Limit > 0 ? $"{Used} of {Limit}" : $"{Used}";
    public double Fraction => Limit > 0 ? Math.Clamp((double)Used / Limit, 0, 1) : 0;
    public bool HasLimit => Limit > 0;
    public bool Full => Limit > 0 && Used >= Limit;
}

/// <summary>One strip on the resource page: how much of the processor it uses.</summary>
public sealed record CostRow(string Name, string Kind, string Effects, double Share, double Fraction, string Colour)
{
    public string ShareText => Share >= 0.001 ? $"{Share * 100:0.0}%" : "<0.1%";
}

public partial class NexAudioViewModel
{
    public ObservableCollection<PoolRow> Pools { get; } = [];
    public ObservableCollection<CostRow> Costs { get; } = [];

    [ObservableProperty] private string _resourceVerdict = "";
    [ObservableProperty] private string _resourceAdvice = "";
    [ObservableProperty] private bool _resourceWarn;
    [ObservableProperty] private bool _resourceDanger;
    [ObservableProperty] private double _loadFraction;
    [ObservableProperty] private double _loadPeakFraction;
    [ObservableProperty] private string _loadSummary = "";
    [ObservableProperty] private string _mixCostText = "";

    private float[]? _lastCosts;

    /// <summary>Recounts the pools from the mix (after any change to the shape of the mix, or when the page opens).</summary>
    private void RefreshPools()
    {
        var used = Effects.Usage(_mix);
        var strips = _mix.AllStrips().ToList();
        int Count(Func<RackItem, bool> f) => strips.Sum(s => s.Rack.Count(f));
        Pools.Clear();
        Pools.Add(new PoolRow("Colour strips", "A whole extra strip (tube, tape or iron, EQ, compressor, gate) as an insert.",
            used[EffectPool.ColourStrip], Effects.MaxColourStrips, "#F59E5B"));
        Pools.Add(new PoolRow("Effects rack", "6-band dynamic EQ and 6-band multiband. A mono strip takes one slot, a stereo one two.",
            used[EffectPool.Rack], Effects.MaxRackSlots, "#A78BFA"));
        Pools.Add(new PoolRow("Processing nodes", "EQ, compressor, multiband, de-esser, limiter, gate or ducker dropped into any path.",
            used[EffectPool.Node], Effects.MaxNodes, "#6CB4F5"));
        static string N(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
        Pools.Add(new PoolRow("Effects", $"{N(Count(r => r.Type == "reverb"), "reverb", "reverbs")}, {N(Count(r => r.Type == "delay"), "delay", "delays")} and {N(Count(r => r.Type is "chorus" or "pitch" or "pitchshift" or "enhancer" or "eq31"), "other", "others")}. No fixed limit: only the PC's headroom.",
            used[EffectPool.Effect], 0, "#9BD66B"));
        Pools.Add(new PoolRow("Plugins", "VST3 plugins, each in its own safe space.", used[EffectPool.Plugin], 0, "#F2D06B"));
        RefreshHeadroom();
    }

    private void ApplyCosts(float[] cost)
    {
        _lastCosts = cost;
        if (!OnResources) return;
        BuildCosts();
    }

    private void BuildCosts()
    {
        if (_lastCosts is not { } cost) return;
        var strips = _mix.AllStrips().ToList();
        var n = Math.Min(strips.Count, cost.Length);
        var total = 0.0;
        for (var i = 0; i < n; i++) total += cost[i];
        var top = Enumerable.Range(0, n).OrderByDescending(i => cost[i]).Take(12).ToList();
        var most = top.Count > 0 ? Math.Max(cost[top[0]], 1e-6) : 1;
        Costs.Clear();
        foreach (var i in top)
        {
            var s = strips[i];
            var fx = s.Rack.Count == 0 ? "no effects" : string.Join(", ", s.Rack.Select(Effects.NameOf));
            Costs.Add(new CostRow(NameOf(s), MixState.KindLabel(s.Kind), fx, cost[i], cost[i] / most, HexOf(s)));
        }
        MixCostText = $"The whole mix uses about {total * 100:0.0}% of one processor core.";
    }

    private string HexOf(Strip s) => StripColour(s).ToString();

    /// <summary>The verdict at the top of the page, from the engine's load.</summary>
    private void RefreshHeadroom()
    {
        var avg = Status.LoadAverage;
        var peak = Status.LoadPeak;
        LoadFraction = Math.Clamp(avg, 0, 1);
        LoadPeakFraction = Math.Clamp(peak, 0, 1);
        LoadSummary = IsRunning ? $"{avg * 100:0}% on average, {peak * 100:0}% at the busiest moment of each buffer" : "The sound is stopped.";
        ResourceDanger = IsRunning && peak >= 0.85;
        ResourceWarn = IsRunning && !ResourceDanger && peak >= 0.7;
        (ResourceVerdict, ResourceAdvice) = !IsRunning
            ? ("Start the sound to measure the load", "The pools below still count what the show uses.")
            : ResourceDanger
                ? ("Very close to the limit", "The PC could run out of time and click. Before the service, bypass or remove the heaviest effects below, or raise the buffer on the Driver page.")
                : ResourceWarn
                    ? ("Getting close to the limit", "There is still room, but be careful adding more reverbs, colour strips or plugins. The heaviest strips are listed below.")
                    : ("Plenty of headroom", "The PC has time to spare for every buffer.");
        if (IsRunning && Pools.Any(p => p.Full))
            ResourceAdvice += " A pool is full: remove an item from it before adding another.";
    }

    partial void OnTabChanged(NexAudioTab value)
    {
        if (value != NexAudioTab.Resources) return;
        RefreshPools();
        BuildCosts();
    }
}
