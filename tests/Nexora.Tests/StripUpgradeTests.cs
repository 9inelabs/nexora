using Nexora.Audio.Dsp;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;

namespace Nexora.Tests;

/// <summary>The channel strip upgrades: drive, dynamic EQ, ducker and sidechain, inserts, order, undo, paste and presets.</summary>
public class StripUpgradeTests
{
    private static float[] Sine(double freq, double amp, int n, double rate = 48000) =>
        Enumerable.Range(0, n).Select(i => (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate))).ToArray();

    private static float Peak(float[] x, int from) => x.Skip(from).Max(Math.Abs);

    private static float RunSine(Processor p, double freq, double amp, int blocks = 200)
    {
        var all = Sine(freq, amp, blocks * 64);
        var peak = 0f;
        for (var b = 0; b < blocks; b++)
        {
            var buf = all.AsSpan(b * 64, 64).ToArray();
            p.Run(buf, null, 64);
            if (b > blocks / 2) peak = Math.Max(peak, buf.Max(Math.Abs));
        }
        return peak;
    }

    [Fact]
    public void Drive_keeps_quiet_sound_at_the_same_level_and_rounds_off_loud_peaks()
    {
        var d = new Saturator(48000, 64);
        d.Configure(new DriveSettings { On = true, Style = DriveStyle.Edge, Drive = 12, Mix = 100 });
        Assert.InRange(RunSine(d, 1000, 0.01), 0.0095f, 0.0105f);
        var loud = new Saturator(48000, 64);
        loud.Configure(new DriveSettings { On = true, Style = DriveStyle.Edge, Drive = 12, Mix = 100 });
        Assert.True(RunSine(loud, 1000, 0.8) < 0.5f);
    }

    [Fact]
    public void Warm_drive_leaves_no_offset()
    {
        var d = new Saturator(48000, 64);
        d.Configure(new DriveSettings { On = true, Style = DriveStyle.Warm, Drive = 18, Mix = 100 });
        var all = Sine(200, 0.7, 64 * 800);
        double sum = 0;
        for (var b = 0; b < 800; b++)
        {
            var buf = all.AsSpan(b * 64, 64).ToArray();
            d.Run(buf, null, 64);
            if (b >= 400) sum += buf.Sum();
        }
        Assert.InRange(sum / (400 * 64), -0.01, 0.01);
    }

    [Fact]
    public void Dynamic_eq_cuts_its_band_only_when_it_is_loud()
    {
        DynEqSettings S() => new() { On = true, Bands = [new() { On = true, Freq = 3000, Q = 2, Threshold = -30, Range = -9, Ratio = 10, Attack = 1, Release = 50 }, new()] };
        var quiet = new DynamicEq(48000, 64);
        quiet.Configure(S());
        Assert.InRange(RunSine(quiet, 3000, 0.005), 0.0048f, 0.0052f); // -46 dB: below the threshold, untouched
        var loud = new DynamicEq(48000, 64);
        loud.Configure(S());
        var p = RunSine(loud, 3000, 0.5);                                // -6 dB: well over, cut by the full 9 dB
        Assert.InRange(p, 0.5f * 0.32f, 0.5f * 0.40f);
        var other = new DynamicEq(48000, 64);
        other.Configure(S());
        Assert.InRange(RunSine(other, 200, 0.5), 0.47f, 0.52f);         // far from the band: untouched
    }

    [Fact]
    public void A_ducker_keyed_from_the_pastor_dips_the_music()
    {
        var s = MixRig.Plain();
        s.Outputs[0].Source = "ch:2";
        s.Channels[1].Gate = new GateSettings { On = true, Mode = GateMode.Ducker, Key = "ch1", Threshold = -30, Range = 12, Attack = 1, Hold = 10, Release = 50 };
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [1] = 0.5f }, 200);
        Assert.InRange(o[0][^1], 0.49f, 0.51f);                          // pastor quiet: music at full level
        MixRig.Settle(e, i, o, new() { [0] = 0.3f, [1] = 0.5f }, 200);
        Assert.InRange(o[0][^1], 0.5f * 0.24f, 0.5f * 0.27f);           // pastor talking: music 12 dB down
    }

    [Fact]
    public void A_compressor_can_listen_to_another_channel()
    {
        var s = MixRig.Plain();
        s.Outputs[0].Source = "ch:2";
        s.Channels[1].Comp = new CompSettings { On = true, Key = "ch1", Threshold = -30, Ratio = 20, Attack = 1, Release = 50, Knee = 0 };
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [1] = 0.5f }, 200);
        Assert.True(o[0][^1] > 0.45f);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f, [1] = 0.5f }, 200);
        Assert.True(o[0][^1] < 0.1f);
    }

    [Fact]
    public void An_insert_before_the_dynamics_runs_before_the_compressor()
    {
        // A limiter before the compressor stops the compressor from ever hearing the peak.
        var s = MixRig.Plain();
        s.Outputs[0].Source = "ch:1";
        var item = new RackItem { Type = "comp", Pre = true, Params = new() { ["threshold"] = -20, ["ratio"] = 20, ["attack"] = 0.05, ["release"] = 50, ["knee"] = 0 } };
        s.Channels[0].Rack.Add(item);
        s.Channels[0].Eq.Bands[0].Gain = 12; // after the insert, the EQ lifts the lows
        s.Channels[0].Eq.Bands[0].Freq = 1000;
        s.Channels[0].Eq.Bands[0].Q = 0.7;
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f }, 200);
        var pre = o[0][^1];
        s.Channels[0].Rack[0].Pre = false;
        e.Load(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f }, 200);
        var post = o[0][^1];
        Assert.True(pre > post * 2, $"before {pre}, after {post}");
    }

    [Fact]
    public void New_settings_change_by_path_and_stay_in_range()
    {
        var s = MixState.CreateDefault();
        Assert.True(MixParams.Apply(s, "ch3.drive.on", true));
        Assert.True(MixParams.Apply(s, "ch3.drive.drive", 40.0));
        Assert.True(MixParams.Apply(s, "ch3.dynEq.1.range", -30.0));
        Assert.True(MixParams.Apply(s, "ch3.gate.mode", "Ducker"));
        Assert.True(MixParams.Apply(s, "ch3.gate.key", "ch21"));
        Assert.True(MixParams.Apply(s, "ch3.order", "DynamicsFirst"));
        var c = s.Channels[2];
        Assert.True(c.Drive.On);
        Assert.Equal(24, c.Drive.Drive);
        Assert.Equal(-18, c.DynEq.Bands[1].Range);
        Assert.Equal(GateMode.Ducker, c.Gate.Mode);
        Assert.Equal("ch21", c.Gate.Key);
        Assert.Equal(ProcessOrder.DynamicsFirst, c.Order);
        Assert.True(MixParams.Apply(s, "ch3.gate.key", "ch3"));
        Assert.Equal("", s.Channels[2].Gate.Key);   // cannot listen to itself
        var back = MixState.FromJson(s.ToJson())!;
        Assert.Equal(ProcessOrder.DynamicsFirst, back.Channels[2].Order);
    }

    [Fact]
    public void Undo_takes_back_a_knob_turn_as_one_step_and_leaves_the_fader_alone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nx-undo-" + Guid.NewGuid().ToString("n"));
        using var desk = new Desk(dir, network: false);
        for (var g = 1; g <= 6; g++) Assert.True(desk.Set("ch1.eq.2.gain", MixRig.J((double)g)));
        Assert.True(desk.Set("ch1.fader", MixRig.J(-10.0)));
        Assert.Equal(6, desk.Mix.Channels[0].Eq.Bands[2].Gain);
        Assert.NotNull(desk.Status.UndoText);
        Assert.True(desk.Undo());
        Assert.Equal(0, desk.Mix.Channels[0].Eq.Bands[2].Gain);   // the whole turn, in one step
        Assert.Equal(-10, desk.Mix.Channels[0].Fader);              // the fader stays where it is
        Assert.True(desk.Redo());
        Assert.Equal(6, desk.Mix.Channels[0].Eq.Bands[2].Gain);
    }

    [Fact]
    public void Paste_and_presets_change_the_sound_but_not_the_trim_level_or_routing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nx-preset-" + Guid.NewGuid().ToString("n"));
        using var desk = new Desk(dir, network: false);
        desk.Set("ch2.trim", MixRig.J(4.0));
        desk.Set("ch2.fader", MixRig.J(-7.0));
        Assert.True(desk.LoadPreset("ch2", "builtin-leadvox", "all"));
        var c = desk.Mix.Channels[1];
        Assert.True(c.Comp.On);
        Assert.True(c.Hpf.On);
        Assert.Contains(c.Rack, r => r.Type == "deesser");
        Assert.Equal(4, c.Trim);
        Assert.Equal(-7, c.Fader);

        var saved = desk.SavePreset("ch2", "Sunday lead");
        Assert.Contains(desk.Status.Presets, p => p.Name == "Sunday lead");
        Assert.True(desk.LoadPreset("ch9", saved.Id, "eq"));
        var nine = desk.Mix.Channels[8];
        Assert.Equal(c.Eq.Bands[1].Gain, nine.Eq.Bands[1].Gain);
        Assert.False(nine.Comp.On);                                // only the EQ was asked for
        Assert.True(desk.DeletePreset(saved.Id));
        Assert.False(desk.DeletePreset("builtin-leadvox"));       // our own starting points stay
    }
}
