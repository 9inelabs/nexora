using Nexora.Audio.Dsp;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;

namespace Nexora.Tests;

/// <summary>Phase 1 of the complete desk: routing and patching.</summary>
public class RoutingTests
{
    private static float Rms(float[] x) => (float)Math.Sqrt(x.Sum(v => (double)v * v) / x.Length);

    // ---- Input patch and alternate input ----------------------------------------------------------

    [Fact]
    public void A_channel_can_hear_any_input_and_switch_to_its_alternate()
    {
        var s = MixRig.Plain();
        foreach (var c in s.Channels) c.ToMain = false;
        var ch = s.Channels[0];
        ch.Eq.Bands[0].Gain = 6; // processing that must stay when the source changes
        Assert.True(MixParams.Apply(s, "ch1.input", MixRig.J(5)));
        Assert.True(MixParams.Apply(s, "ch1.altInput", MixRig.J(9)));
        s.Outputs[0].Source = "ch:1";
        s.Channels[0].DirectTap = SendTap.Input;

        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [4] = 0.2f, [8] = 0.4f, [0] = 0.9f });
        Assert.InRange(o[0][^1], 0.19f, 0.21f); // input 5, not input 1

        Assert.True(MixParams.Apply(s, "ch1.useAlt", MixRig.J(true)));
        e.Load(s);
        MixRig.Settle(e, i, o, new() { [4] = 0.2f, [8] = 0.4f, [0] = 0.9f });
        Assert.InRange(o[0][^1], 0.39f, 0.41f); // the alternate, input 9
        Assert.Equal(6, s.Channels[0].Eq.Bands[0].Gain);
    }

    [Fact]
    public void An_old_mix_file_keeps_channel_N_on_input_N()
    {
        var s = MixState.CreateDefault();
        foreach (var c in s.Channels) c.Input = 0;
        s.Version = 2;
        var back = MixState.FromJson(s.ToJson())!;
        Assert.Equal(Enumerable.Range(1, 32), back.Channels.Select(c => c.Input));
        // A version 3 file keeps "no input" as it is.
        back.Channels[3].Input = 0;
        Assert.Equal(0, MixState.FromJson(back.ToJson())!.Channels[3].Input);
    }

    [Fact]
    public void Switching_every_channel_to_its_alternate_input_skips_the_ones_without_one()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexaudio-alt-" + Guid.NewGuid().ToString("n")[..6]);
        using var desk = new Desk(dir, network: false);
        desk.Change(m => { m.Channels[0].AltInput = 20; m.Channels[1].AltInput = 21; return true; });
        Assert.True(desk.UseAlternateInputs(true));
        var mix = desk.Mix;
        Assert.True(mix.Channels[0].UseAlt);
        Assert.True(mix.Channels[1].UseAlt);
        Assert.False(mix.Channels[2].UseAlt);
        Assert.True(desk.Undo());
        Assert.False(desk.Mix.Channels[0].UseAlt);
    }

    // ---- Direct outs -------------------------------------------------------------------------------

    [Theory]
    [InlineData(SendTap.Input, 0.25f)]   // before the EQ and the fader
    [InlineData(SendTap.Pre, 1.0f)]      // after the +12 dB shelf, before the fader
    [InlineData(SendTap.Post, 0.1f)]     // after the -20 dB fader
    public void A_direct_out_comes_from_its_own_tap_point(SendTap tap, float factor)
    {
        var s = MixRig.Plain();
        var ch = s.Channels[0];
        ch.Eq.Bands[0].Gain = 12;
        ch.Fader = -20;
        ch.DirectTap = tap;
        s.Outputs[3].Source = "ch:1";
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.25f }, 400);
        var shelf = 0.25f * (float)Math.Pow(10, 12 / 20.0);
        var want = tap switch { SendTap.Input => 0.25f, SendTap.Pre => shelf, _ => shelf * 0.1f };
        Assert.InRange(o[3][^1], want * 0.95f, want * 1.05f);
        Assert.True(factor > 0);
    }

    [Fact]
    public void A_muted_channel_sends_nothing_from_its_direct_out()
    {
        var s = MixRig.Plain();
        s.Channels[0].DirectTap = SendTap.Pre;
        s.Channels[0].Mute = true;
        s.Outputs[3].Source = "ch:1";
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.25f });
        Assert.Equal(0f, o[3][^1], 4);
    }

    // ---- Bus-to-bus routing and loops ----------------------------------------------------------------

    [Fact]
    public void A_group_can_feed_another_group_and_stops_going_to_main_by_itself()
    {
        var s = MixRig.Plain();
        foreach (var c in s.Channels) c.ToMain = false;
        var drums = s.BusesOf(StripKind.Group).First(); // grp1
        var band = s.BusesOf(StripKind.Group).Skip(1).First(); // grp2
        Assert.True(MixParams.Apply(s, $"ch1.group.{drums.Id}", MixRig.J(true)));
        Assert.True(MixParams.Apply(s, $"{drums.Id}.group.{band.Id}", MixRig.J(true)));
        Assert.False(s.Find(drums.Id)!.ToMain);
        // The way back would be a loop.
        Assert.False(MixParams.Apply(s, $"{band.Id}.group.{drums.Id}", MixRig.J(true)));
        Assert.NotNull(Routing.WhyNot(s, s.Find(band.Id)!, s.Find(drums.Id)!, group: true));

        s.Find(band.Id)!.Fader = -6;
        s.Outputs[0].Source = "main:l";
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        // Channel (centre, -3 dB into the stereo group), group 1 at 0 dB into group 2 at -6 dB, then the main at 0 dB.
        var want = 0.5f * 0.7071f * 0.5012f;
        Assert.InRange(o[0][^1], want * 0.97f, want * 1.03f);
    }

    [Fact]
    public void A_mix_can_feed_another_mix_but_never_back()
    {
        var s = MixRig.Plain();
        var m1 = s.BusesOf(StripKind.Mix).First();
        var m2 = s.BusesOf(StripKind.Mix).Skip(1).First();
        m1.Fader = 0;
        m2.Fader = 0;
        Assert.True(MixParams.Apply(s, $"ch1.send.{m1.Id}.level", MixRig.J(0.0)));
        Assert.True(MixParams.Apply(s, $"{m1.Id}.send.{m2.Id}.level", MixRig.J(-6.0)));
        Assert.False(MixParams.Apply(s, $"{m2.Id}.send.{m1.Id}.level", MixRig.J(0.0)));
        Assert.False(MixParams.Apply(s, $"{m1.Id}.send.{m1.Id}.level", MixRig.J(0.0)));
        s.Outputs[5].Source = m2.Id;
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.4f });
        Assert.InRange(o[5][^1], 0.4f * 0.5012f * 0.97f, 0.4f * 0.5012f * 1.03f);
    }

    [Fact]
    public void Effects_can_chain_and_a_group_can_feed_a_reverb()
    {
        var s = MixState.CreateDefault();
        var fx1 = s.BusesOf(StripKind.Fx).First();
        var fx2 = s.BusesOf(StripKind.Fx).Skip(1).First();
        var choir = s.BusesOf(StripKind.Group).Last();
        Assert.True(MixParams.Apply(s, $"{fx1.Id}.send.{fx2.Id}.level", MixRig.J(-10.0)));
        Assert.True(MixParams.Apply(s, $"{choir.Id}.send.{fx1.Id}.level", MixRig.J(-10.0)));
        Assert.False(MixParams.Apply(s, $"{fx2.Id}.send.{fx1.Id}.level", MixRig.J(-10.0)));
        var order = Routing.Order(s)!.Select(x => x.Id).ToList();
        Assert.True(order.IndexOf(choir.Id) < order.IndexOf(fx1.Id));
        Assert.True(order.IndexOf(fx1.Id) < order.IndexOf(fx2.Id));
        Assert.True(order.IndexOf("main") < order.IndexOf("mono"));
    }

    [Fact]
    public void A_file_with_a_loop_in_it_loses_only_the_route_that_closes_it()
    {
        var s = MixState.CreateDefault();
        var m1 = s.BusesOf(StripKind.Mix).First();
        var m2 = s.BusesOf(StripKind.Mix).Skip(1).First();
        m1.Sends.Add(new Send { Bus = m2.Id, Level = 0 });
        m2.Sends.Add(new Send { Bus = m1.Id, Level = 0 }); // written by hand: a loop
        var back = MixState.FromJson(s.ToJson())!;
        Assert.NotNull(back.Find(m1.Id)!.SendTo(m2.Id));
        Assert.Null(back.Find(m2.Id)!.SendTo(m1.Id));
        Assert.NotNull(Routing.Order(back));
    }

    [Fact]
    public void Signal_trace_finds_everything_upstream_and_downstream()
    {
        var s = MixState.CreateDefault();
        var drums = s.BusesOf(StripKind.Group).First();
        var lobby = s.BusesOf(StripKind.Matrix).First();
        MixParams.Apply(s, $"ch1.group.{drums.Id}", MixRig.J(true));
        MixParams.Apply(s, $"main.send.{lobby.Id}.level", MixRig.J(0.0));
        var down = Routing.Downstream(s, "ch1");
        Assert.Contains(drums.Id, down);
        Assert.Contains("main", down);
        Assert.Contains(lobby.Id, down);
        Assert.Contains("stream", down);
        var up = Routing.Upstream(s, lobby.Id);
        Assert.Contains("ch1", up);
        Assert.Contains(drums.Id, up);
        Assert.DoesNotContain("mix1", up);
    }

    // ---- Line-up (plugin and look-ahead delay) ---------------------------------------------------------

    [Fact]
    public void A_look_ahead_on_a_group_delays_the_other_paths_into_the_main_mix_to_match()
    {
        var s = MixRig.Plain();
        var drums = s.BusesOf(StripKind.Group).First();
        MixParams.AddRackItem(s, drums.Id, Effects.Default("limiter"));
        s.Channels[0].ToMain = false;
        s.Channels[0].Groups.Add(drums.Id);
        s.Outputs[0].Source = "main:l";
        var (e, _, _) = MixRig.Make(s);
        Assert.Equal(48, e.MixLatency);
        var lat = e.Latencies;
        Assert.Equal(48, lat[drums.Id].Own);
        Assert.Equal(48, lat["main"].Arrives);

        int ArrivalOf(int ch)
        {
            var (en, inp, outp) = MixRig.Make(s);
            for (var b = 0; b < 40; b++)
            {
                foreach (var x in inp) Array.Clear(x);
                if (b == 20) inp[ch][0] = 0.5f;
                en.Process(inp, outp, MixRig.Frames);
                for (var n = 0; n < MixRig.Frames; n++)
                    if (Math.Abs(outp[0][n]) > 0.02f) return (b - 20) * MixRig.Frames + n;
            }
            return -1;
        }
        Assert.Equal(48, ArrivalOf(0)); // through the group with its limiter
        Assert.Equal(48, ArrivalOf(1)); // straight to the main mix, delayed to match
    }

    // ---- Longer delays -----------------------------------------------------------------------------------

    [Fact]
    public void Delays_reach_one_point_three_seconds_on_channels_and_outputs()
    {
        var s = MixRig.Plain();
        Assert.True(MixParams.Apply(s, "ch1.delayMs", MixRig.J(5000.0)));
        Assert.Equal(1300, s.Channels[0].DelayMs);
        Assert.True(MixParams.Apply(s, "out.1.delayMs", MixRig.J(1200.0)));
        Assert.Equal(1200, s.Outputs[0].DelayMs);

        s.Channels[0].DelayMs = 1000;
        s.Outputs[0].Source = "ch:1";
        s.Outputs[0].DelayMs = 200;
        var (e, i, o) = MixRig.Make(s);
        var at = -1;
        // Let the gains settle first, then one click.
        for (var b = 0; b < 1100 && at < 0; b++)
        {
            foreach (var x in i) Array.Clear(x);
            if (b == 50) i[0][0] = 0.5f;
            e.Process(i, o, MixRig.Frames);
            for (var n = 0; n < MixRig.Frames; n++)
                if (Math.Abs(o[0][n]) > 0.1f) { at = (b - 50) * MixRig.Frames + n; break; }
        }
        Assert.Equal((int)(1.2 * MixRig.Rate), at);
    }

    // ---- Test signal ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ToneKind.Sine)]
    [InlineData(ToneKind.Pink)]
    [InlineData(ToneKind.White)]
    public void The_test_signal_plays_at_about_its_level_and_never_above_minus_6(ToneKind kind)
    {
        var s = MixRig.Plain();
        Assert.True(MixParams.Apply(s, "tone.kind", MixRig.J(kind.ToString())));
        Assert.True(MixParams.Apply(s, "tone.level", MixRig.J(0.0)));
        Assert.Equal(ToneSettings.MaxLevel, s.Tone.Level);
        Assert.True(MixParams.Apply(s, "tone.level", MixRig.J(-20.0)));
        s.Outputs[0].Source = "tone";
        var (e, i, o) = MixRig.Make(s);
        var all = new List<float>();
        for (var b = 0; b < 1500; b++)
        {
            e.Process(i, o, MixRig.Frames);
            if (b > 200) all.AddRange(o[0]);
        }
        var rms = Rms(all.ToArray());
        var want = 0.1f * 0.7071f; // a sine at -20 dBFS
        Assert.InRange(rms, want * 0.7f, want * 1.4f);
        Assert.True(all.Max(Math.Abs) <= 1f);
    }

    [Fact]
    public void The_test_signal_can_play_into_a_bus_and_through_its_routing()
    {
        var s = MixRig.Plain();
        var m1 = s.BusesOf(StripKind.Mix).First();
        m1.Fader = -6;
        Assert.True(MixParams.Apply(s, "tone.bus", MixRig.J(m1.Id)));
        Assert.True(MixParams.Apply(s, "tone.on", MixRig.J(true)));
        Assert.False(MixParams.Apply(s, "tone.bus", MixRig.J("ch1")));
        s.Outputs[2].Source = m1.Id;
        var (e, i, o) = MixRig.Make(s);
        var peak = 0f;
        for (var b = 0; b < 400; b++)
        {
            e.Process(i, o, MixRig.Frames);
            if (b > 100) peak = Math.Max(peak, o[2].Max(Math.Abs));
        }
        Assert.InRange(peak, 0.125893f * 0.5012f * 0.95f, 0.125893f * 0.5012f * 1.05f);

        // Turning it off (or the desk starting up) silences it.
        s.Tone.On = false;
        e.Load(s);
        MixRig.Settle(e, i, o, [], 200);
        Assert.True(o[2].Max(Math.Abs) < 1e-4f);
    }

    [Fact]
    public void The_desk_never_starts_with_the_test_signal_playing_into_a_bus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexaudio-tone-" + Guid.NewGuid().ToString("n")[..6]);
        Directory.CreateDirectory(dir);
        var s = MixState.CreateDefault();
        s.Tone.Bus = "mix1";
        s.Tone.On = true;
        File.WriteAllText(Path.Combine(dir, "mix.json"), s.ToJson());
        using var desk = new Desk(dir, network: false);
        Assert.False(desk.Mix.Tone.On);
        Assert.Equal("mix1", desk.Mix.Tone.Bus);
    }

    // ---- Routing layouts -----------------------------------------------------------------------------------

    [Fact]
    public void A_routing_layout_brings_back_the_routing_and_leaves_the_sound_alone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexaudio-routing-" + Guid.NewGuid().ToString("n")[..6]);
        using var desk = new Desk(dir, network: false);
        desk.Change(m =>
        {
            m.Channels[0].Input = 7;
            m.Channels[0].DirectTap = SendTap.Pre;
            m.Channels[0].Groups.Add("grp1");
            m.Outputs[4].Source = "ch:1";
            m.Outputs[4].DelayMs = 30;
            return true;
        });
        var saved = desk.SaveRouting(null, "Youth service", "", "Franklyn");

        // Change the routing and the sound.
        desk.Change(m =>
        {
            m.Channels[0].Input = 1;
            m.Channels[0].Groups.Clear();
            m.Channels[0].Eq.Bands[1].Gain = 5;
            m.Channels[0].Fader = -12;
            m.Outputs[4].Source = "off";
            m.Outputs[4].DelayMs = 45;
            m.Buses.RemoveAll(b => b.Id == "mtx2");
            return true;
        });

        var preview = desk.PreviewRouting(saved.Id)!;
        Assert.Contains(preview.Changes, c => c.StripId == "ch1" && c.What == "Input" && c.To == "In 7");
        Assert.Contains(preview.Changes, c => c.What == "Bus" && c.To == "added");

        Assert.True(desk.RecallRouting(saved.Id));
        var mix = desk.Mix;
        Assert.Equal(7, mix.Channels[0].Input);
        Assert.Equal(SendTap.Pre, mix.Channels[0].DirectTap);
        Assert.Contains("grp1", mix.Channels[0].Groups);
        Assert.Equal("ch:1", mix.Outputs[4].Source);
        Assert.NotNull(mix.Find("mtx2"));
        // The sound and the speaker processing stayed.
        Assert.Equal(5, mix.Channels[0].Eq.Bands[1].Gain);
        Assert.Equal(-12, mix.Channels[0].Fader);
        Assert.Equal(45, mix.Outputs[4].DelayMs);

        Assert.Contains(desk.Status.Routings, r => r.Id == saved.Id);
        Assert.Contains(desk.Status.Routings, r => r.BuiltIn);
        Assert.True(desk.Undo());
        Assert.Equal(1, desk.Mix.Channels[0].Input);
    }

    [Fact]
    public void The_factory_routing_starts_again_and_cannot_be_deleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexaudio-factory-" + Guid.NewGuid().ToString("n")[..6]);
        using var desk = new Desk(dir, network: false);
        desk.Change(m => { m.Channels[2].Input = 30; m.Channels[2].ToMain = false; m.Outputs[0].Source = "off"; return true; });
        Assert.True(desk.RecallRouting(RoutingRecall.FactoryId));
        Assert.Equal(3, desk.Mix.Channels[2].Input);
        Assert.True(desk.Mix.Channels[2].ToMain);
        Assert.Equal("in:1", desk.Mix.Outputs[0].Source);
        Assert.False(desk.DeleteRouting(RoutingRecall.FactoryId));
    }

    [Fact]
    public void A_recall_safe_channel_keeps_its_routing()
    {
        var now = MixState.CreateDefault();
        var layout = MixState.CreateDefault();
        layout.Channels[0].Input = 12;
        layout.Channels[1].Input = 13;
        now.Channels[0].RecallSafe = true;
        var after = RoutingRecall.Apply(now, layout);
        Assert.Equal(1, after.Channels[0].Input);
        Assert.Equal(13, after.Channels[1].Input);
    }
}
