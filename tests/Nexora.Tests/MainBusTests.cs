using Nexora.Audio.Dsp;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;

namespace Nexora.Tests;

/// <summary>Phase 3 of the complete desk: the left-centre-right main and fuller bus processing.</summary>
public class MainBusTests
{
    private const float Half = 0.70710678f;

    /// <summary>Channel 1 alone on the main, with main left, centre and right on outputs 1, 2 and 3.</summary>
    private static MixState Lcr()
    {
        var s = MixRig.Plain();
        foreach (var c in s.Channels) c.ToMain = false;
        s.Channels[0].ToMain = true;
        Assert.True(MixParams.Apply(s, "mainset.layout", MixRig.J("Lcr")));
        s.Outputs[0].Source = "main:l";
        s.Outputs[1].Source = "centre";
        s.Outputs[2].Source = "main:r";
        return s;
    }

    private static void Near(float want, float got, float tol = 0.01f) => Assert.InRange(got, want - tol, want + tol);

    [Fact]
    public void A_centre_panned_channel_is_only_in_the_centre()
    {
        var s = Lcr();
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Near(0f, o[0][^1]);
        Near(0.5f, o[1][^1]);
        Near(0f, o[2][^1]);
    }

    [Theory]
    [InlineData(-1.0, 1f, 0f, 0f)]
    [InlineData(-0.5, Half, Half, 0f)]
    [InlineData(0.5, 0f, Half, Half)]
    [InlineData(1.0, 0f, 0f, 1f)]
    public void The_pan_moves_across_left_centre_and_right_keeping_the_power(double pan, float l, float c, float r)
    {
        var s = Lcr();
        s.Channels[0].Pan = pan;
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Near(0.5f * l, o[0][^1]);
        Near(0.5f * c, o[1][^1]);
        Near(0.5f * r, o[2][^1]);
    }

    [Fact]
    public void A_channel_set_to_left_and_right_only_and_stereo_strips_never_reach_the_centre()
    {
        var s = Lcr();
        Assert.True(MixParams.Apply(s, "ch1.toCentre", MixRig.J(false)));
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Near(0.5f * Half, o[0][^1]);
        Near(0f, o[1][^1]);
        Near(0.5f * Half, o[2][^1]);
        Assert.DoesNotContain(Routing.Edges(s), x => x.From == "ch1" && x.To == "centre");
        // A stereo group: left and right only.
        var grp = s.Find("grp1")!;
        Assert.True(grp.Stereo);
        Assert.False(Routing.FeedsCentre(s, grp));
    }

    [Fact]
    public void The_stream_mono_and_headphones_get_the_centre_folded_in()
    {
        var s = Lcr();
        s.Outputs[3].Source = "stream:l";
        s.Outputs[4].Source = "mono";
        s.Outputs[5].Source = "mon:l";
        s.Mono.Fader = 0;
        s.Stream.Comp.On = false;
        s.Stream.Rack.Clear();
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f }, 200);
        // The same level a centre-panned voice has in a left-right main.
        Near(0.5f * Half, o[3][^1], 0.02f);
        Near(0.5f * Half, o[4][^1], 0.02f);
        Near(0.5f * Half, o[5][^1], 0.02f);
    }

    [Fact]
    public void The_centre_has_its_own_processing_and_fader()
    {
        var s = Lcr();
        Assert.True(MixParams.Apply(s, "centre.fader", MixRig.J(-6.0)));
        Assert.True(MixParams.Apply(s, "centre.eq.0.gain", MixRig.J(3.0)));
        Assert.False(s.Centre.Stereo);
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f }, 200);
        Assert.InRange(o[1][^1], 0.5f * 0.45f, 0.5f * 0.75f); // -6 dB fader, a little low shelf
    }

    [Fact]
    public void Left_right_and_centre_leave_together_when_one_has_a_limiter()
    {
        var s = Lcr();
        s.Main.Rack.Add(Effects.Default("limiter"));
        var e = new MixEngine(32, 32, MixRig.Frames, MixRig.Rate);
        e.Load(s);
        var lat = e.Latencies;
        Assert.True(lat["main"].Leaves > 0);
        Assert.Equal(lat["main"].Leaves, lat["centre"].Leaves);
    }

    [Fact]
    public void Switching_back_to_left_and_right_hides_the_centre_and_frees_its_output()
    {
        var s = Lcr();
        s.Centre.Eq.Bands[1].Gain = 4;
        Assert.Contains(s.AllStrips(), x => x.Id == "centre");
        Assert.True(MixParams.Apply(s, "mainset.layout", MixRig.J("Lr")));
        Assert.DoesNotContain(s.AllStrips(), x => x.Id == "centre");
        Assert.Equal("off", s.Outputs[1].Source);
        // The centre keeps its settings for next time, also through a save.
        var back = MixState.FromJson(s.ToJson())!;
        Assert.Equal(4, back.Centre.Eq.Bands[1].Gain);
        Assert.False(MixParams.Apply(s, "mainset.layout", MixRig.J("Lcrs")));
    }

    [Fact]
    public void A_routing_layout_and_a_scene_bring_back_the_main_layout()
    {
        var s = Lcr();
        s.Channels[0].ToCentre = false;
        var saved = s.Clone();
        MixParams.Apply(s, "mainset.layout", MixRig.J("Lr"));
        s.Channels[0].ToCentre = true;
        var r = RoutingRecall.Apply(s, saved);
        Assert.True(r.IsLcr);
        Assert.False(r.Channels[0].ToCentre);
        var sc = SceneRecall.Apply(s, saved, RecallScope.Routing);
        Assert.True(sc.IsLcr);
    }

    [Fact]
    public void An_old_file_opens_with_a_left_and_right_main()
    {
        var json = MixState.CreateDefault().ToJson().Replace("\"centre\":", "\"oldCentre\":").Replace("\"mainSettings\":", "\"oldMain\":");
        var s = MixState.FromJson(json)!;
        Assert.False(s.IsLcr);
        Assert.Equal("centre", s.Centre.Id);
        Assert.Equal(StripKind.Centre, s.Centre.Kind);
    }

    // ---- Buses -----------------------------------------------------------------------------------------

    [Fact]
    public void A_bus_ducks_under_a_channel_key()
    {
        // The band group dips while the pastor talks.
        var s = MixRig.Plain();
        foreach (var c in s.Channels) c.ToMain = false;
        var band = s.Find("grp2")!;
        s.Channels[13].Groups.Add("grp2");
        s.Outputs[0].Source = "grp2:l";
        band.Gate.On = true;
        band.Gate.Mode = GateMode.Ducker;
        band.Gate.Key = "ch1";
        band.Gate.Threshold = -30;
        band.Gate.Range = 12;
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [13] = 0.3f }, 400);
        var open = o[0][^1];
        MixRig.Settle(e, i, o, new() { [13] = 0.3f, [0] = 0.5f }, 600);
        var ducked = o[0][^1];
        Assert.True(ducked < open * 0.5f, $"open {open}, ducked {ducked}");
    }

    [Fact]
    public void A_bus_trim_goes_to_plus_60_and_a_channel_trim_stays_at_plus_24()
    {
        var s = MixState.CreateDefault();
        MixParams.Apply(s, "mix1.trim", MixRig.J(80.0));
        MixParams.Apply(s, "ch1.trim", MixRig.J(80.0));
        Assert.Equal(60, s.Find("mix1")!.Trim);
        Assert.Equal(24, s.Channels[0].Trim);
    }

    [Fact]
    public void Pasting_dynamics_onto_a_bus_brings_the_gate_too()
    {
        var s = MixState.CreateDefault();
        var from = s.Channels[0].Clone();
        from.Gate.On = true;
        from.Gate.Mode = GateMode.Ducker;
        from.Gate.Key = "ch2";
        Assert.True(MixParams.Paste(s, "grp2", from, "dyn"));
        Assert.True(s.Find("grp2")!.Gate.On);
        Assert.Equal("ch2", s.Find("grp2")!.Gate.Key);
    }
}
