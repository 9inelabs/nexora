using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace Nexora.Tests;

/// <summary>Phase 4 of the complete desk: more compressor characters, the 8-band bus EQ, colour strips, the effects rack, nodes and new effects.</summary>
public class ProcessingTests
{
    private const int Block = 64;

    private static float[] Sine(double freq, double amp, int n, double rate = 48000) =>
        Enumerable.Range(0, n).Select(i => (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate))).ToArray();

    /// <summary>Runs a sine through the processor and returns the output (mono).</summary>
    private static float[] Run(Processor p, float[] input)
    {
        var output = new float[input.Length];
        for (var b = 0; b + Block <= input.Length; b += Block)
        {
            var buf = input.AsSpan(b, Block).ToArray();
            p.Run(buf, null, Block);
            Array.Copy(buf, 0, output, b, Block);
        }
        return output;
    }

    private static double Rms(float[] x, int from) => Math.Sqrt(x.Skip(from).Sum(v => (double)v * v) / (x.Length - from));
    private static double Db(double ratio) => 20 * Math.Log10(ratio);

    private static RackItem Item(string type, params (string Key, double Value)[] p)
    {
        var r = Effects.Default(type);
        foreach (var (k, v) in p) r.Params[k] = v;
        return r;
    }

    // ---- Compressor characters ---------------------------------------------------------------

    [Fact]
    public void The_fast_limit_character_holds_peaks_whatever_the_ratio_is_set_to()
    {
        var c = new Compressor(48000, Block);
        c.Configure(new CompSettings { On = true, Style = CompStyle.Limit, Threshold = -12, Ratio = 2, Attack = 30, Release = 100, Knee = 0 });
        var y = Run(c, Sine(1000, 0.9, 48000));
        // 0.9 is about -1 dB, 11 dB over: a 2:1 would leave about -6.5 dB (0.47); the limit holds close to -12 dB (0.25).
        Assert.InRange(y.Skip(24000).Max(Math.Abs), 0.2f, 0.33f);
    }

    [Fact]
    public void Peak_and_average_detection_hear_a_sine_the_same_and_vintage_compresses()
    {
        double Out(CompStyle style, CompDetect detect)
        {
            var c = new Compressor(48000, Block);
            c.Configure(new CompSettings { On = true, Style = style, Detect = detect, Threshold = -20, Ratio = 4, Attack = 5, Release = 100, Knee = 0 });
            return Rms(Run(c, Sine(500, 0.5, 48000)), 24000);
        }
        var input = 0.5 / Math.Sqrt(2);
        var peak = Out(CompStyle.Clean, CompDetect.Peak);
        var rms = Out(CompStyle.Clean, CompDetect.Rms);
        var vintage = Out(CompStyle.Vintage, CompDetect.Auto);
        // The average detector reads a sine at its peak (+3 dB), so both reduce by about the same amount.
        Assert.InRange(Db(peak / rms), -1.5, 1.5);
        Assert.True(peak < input * 0.6, $"peak detection reduced to {peak}");
        Assert.True(vintage < input * 0.6, $"vintage reduced to {vintage}");
    }

    [Fact]
    public void The_rack_compressor_takes_the_new_characters_and_detection()
    {
        var r = Effects.Default("comp");
        r.Text["style"] = "Vintage";
        r.Text["detect"] = "Rms";
        var c = Compressor.FromRack(r);
        Assert.Equal(CompStyle.Vintage, c.Style);
        Assert.Equal(CompDetect.Rms, c.Detect);
        Assert.Contains("Limit", Effects.Get("comp")!.Choices[0].Options);
    }

    // ---- 8-band bus EQ -------------------------------------------------------------------------

    [Fact]
    public void Buses_get_eight_EQ_bands_and_channels_keep_six()
    {
        var s = MixState.CreateDefault();
        s.Repair();
        Assert.All(s.Channels, c => Assert.Equal(6, c.Eq.Bands.Count));
        Assert.All(s.AllStrips().Where(x => x.Kind != StripKind.Channel), b => Assert.Equal(8, b.Eq.Bands.Count));
        Assert.Equal(8, s.Centre.Eq.Bands.Count);
    }

    [Fact]
    public void An_older_six_band_bus_EQ_gains_two_flat_bands_and_sounds_the_same()
    {
        var s = MixState.CreateDefault();
        var mix = s.Find("mix1")!;
        mix.Eq.Bands = EqSettings.DefaultBands();
        mix.Eq.Bands[0].Gain = 4;
        mix.Eq.Bands[3].Gain = -3;
        mix.Eq.Bands[5].Gain = 2;
        var before = FilterSettings.From(mix);
        s.Repair();
        Assert.Equal(8, mix.Eq.Bands.Count);
        var after = FilterSettings.From(mix);
        foreach (var f in new[] { 50.0, 200, 1000, 2500, 6000, 12000 })
            Assert.Equal(StripFilters.ResponseDb(before, f, 48000), StripFilters.ResponseDb(after, f, 48000), 3);
        Assert.Equal(FilterShape.HighShelf, mix.Eq.Bands[^1].Shape);
    }

    [Fact]
    public void A_bus_EQ_pasted_on_a_channel_drops_back_to_six_bands_keeping_its_shelves()
    {
        var s = MixState.CreateDefault();
        s.Repair();
        s.Find("mix1")!.Eq.Bands[^1].Gain = 3;
        Assert.True(MixParams.Paste(s, "ch1", s.Find("mix1")!, "eq"));
        s.Repair();
        Assert.Equal(6, s.Channels[0].Eq.Bands.Count);
        Assert.Equal(FilterShape.HighShelf, s.Channels[0].Eq.Bands[^1].Shape);
        Assert.Equal(3, s.Channels[0].Eq.Bands[^1].Gain);
    }

    // ---- Colour strip --------------------------------------------------------------------------

    [Fact]
    public void A_clean_flat_colour_strip_leaves_the_sound_alone()
    {
        var r = Item("colour");
        r.Text["character"] = "Clean";
        var p = new ColourStrip(48000, Block);
        p.Configure(r);
        var x = Sine(1000, 0.3, 9600);
        var y = Run(p, x);
        Assert.InRange(Db(Rms(y, 4800) / Rms(x, 4800)), -0.1, 0.1);
    }

    [Fact]
    public void The_colour_strip_EQ_compressor_and_character_all_work()
    {
        var x = Sine(8000, 0.3, 19200);
        double Through(params (string, double)[] p)
        {
            var r = Item("colour", p);
            r.Text["character"] = "Clean";
            var proc = new ColourStrip(48000, Block);
            proc.Configure(r);
            return Rms(Run(proc, x), 9600) / Rms(x, 9600);
        }
        Assert.InRange(Db(Through(("hg", 6), ("hf", 4000))), 4.5, 7);
        // Tube drive on a loud sine rounds its peaks (the level is matched, so it is the shape that changes).
        var tube = Item("colour", ("drive", 18));
        var tp = new ColourStrip(48000, Block);
        tp.Configure(tube);
        var loud = Sine(200, 0.9, 19200);
        var y = Run(tp, loud);
        Assert.True(y.Skip(9600).Max(Math.Abs) < 0.85f);
        // The compressor.
        var c = Item("colour", ("cthr", -30), ("cratio", 8));
        c.Text["character"] = "Clean";
        c.Text["compstyle"] = "Fet";
        var cp = new ColourStrip(48000, Block);
        cp.Configure(c);
        Run(cp, Sine(1000, 0.5, 9600));
        Assert.True(cp.Reduction > 6, $"reduction {cp.Reduction}");
    }

    [Fact]
    public void A_colour_strip_ducker_listening_to_the_pastor_dips_the_music()
    {
        var s = MixRig.Plain();
        foreach (var c in s.Channels) c.ToMain = false;
        s.Channels[1].ToMain = true;
        s.Outputs[0].Source = "main:l";
        var r = Item("colour", ("dthr", -30), ("drange", 12));
        r.Text["character"] = "Clean";
        r.Text["dynamics"] = "Ducker";
        r.Text["key"] = "ch1";
        Assert.True(MixParams.AddRackItem(s, "ch2", r));
        s.Repair();
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [1] = 0.4f }, 200);
        var open = o[0][^1];
        MixRig.Settle(e, i, o, new() { [0] = 0.5f, [1] = 0.4f }, 200);
        var ducked = o[0][^1];
        Assert.InRange(Db(ducked / open), -13, -11);
    }

    [Fact]
    public void A_rack_item_that_listens_to_a_channel_that_is_gone_listens_to_itself()
    {
        var s = MixState.CreateDefault();
        var r = Effects.Default("gate");
        r.Text["key"] = "ch99";
        MixParams.AddRackItem(s, "ch2", r);
        var self = Effects.Default("gate");
        self.Text["key"] = "ch3";
        MixParams.AddRackItem(s, "ch3", self);
        s.Repair();
        Assert.Equal("", s.Channels[1].Rack[0].Text["key"]);
        Assert.Equal("", s.Channels[2].Rack[0].Text["key"]);
    }

    // ---- Effects rack --------------------------------------------------------------------------

    [Theory]
    [InlineData(60)]
    [InlineData(200)]
    [InlineData(600)]
    [InlineData(1800)]
    [InlineData(5000)]
    [InlineData(12000)]
    public void The_six_band_multiband_adds_back_up_flat_with_no_reduction(double freq)
    {
        var r = Item("multiband6");
        for (var b = 1; b <= 6; b++) r.Params[$"t{b}"] = 0;
        var p = new Multiband6(48000, Block);
        p.Configure(r);
        var x = Sine(freq, 0.25, 48000);
        var y = Run(p, x);
        Assert.InRange(Db(Rms(y, 24000) / Rms(x, 24000)), -0.3, 0.3);
    }

    [Fact]
    public void The_six_band_multiband_compresses_only_its_own_band_and_can_expand()
    {
        var r = Item("multiband6", ("t4", -30), ("r4", 6));
        for (var b = 1; b <= 6; b++) if (b != 4) r.Params[$"t{b}"] = 0;
        var p = new Multiband6(48000, Block);
        p.Configure(r);
        var inBand = Sine(1500, 0.5, 48000);
        Assert.True(Db(Rms(Run(p, inBand), 24000) / Rms(inBand, 24000)) < -6);
        var p2 = new Multiband6(48000, Block);
        p2.Configure(r);
        var outBand = Sine(150, 0.5, 48000);
        Assert.InRange(Db(Rms(Run(p2, outBand), 24000) / Rms(outBand, 24000)), -0.5, 0.5);

        // Expand: a quiet sound below the threshold is turned down.
        var ex = Item("multiband6", ("t3", -20), ("r3", 3));
        ex.Text["mode"] = "Expand";
        for (var b = 1; b <= 6; b++) if (b != 3) ex.Params[$"t{b}"] = -100;
        var p3 = new Multiband6(48000, Block);
        p3.Configure(ex);
        var quiet = Sine(520, 0.01, 48000); // about -40 dB peak, in the middle of band 3 (300 to 900 Hz)
        var dbx = Db(Rms(Run(p3, quiet), 24000) / Rms(quiet, 24000)); Assert.True(dbx < -10, $"expand {dbx}");
    }

    [Fact]
    public void The_six_band_dynamic_EQ_moves_only_when_its_band_is_loud()
    {
        var r = Item("dyneq6", ("t4", -30), ("g4", -10));
        double Change(double amp)
        {
            var p = new DynEq6(48000, Block);
            p.Configure(r);
            var x = Sine(2000, amp, 48000);
            return Db(Rms(Run(p, x), 24000) / Rms(x, 24000));
        }
        Assert.True(Change(0.5) < -6);
        Assert.InRange(Change(0.005), -0.5, 0.5);
    }

    [Fact]
    public void The_pools_refuse_a_37th_colour_strip_and_a_stereo_rack_item_takes_two_slots()
    {
        var s = MixState.CreateDefault();
        var n = 0;
        foreach (var st in s.AllStrips())
            for (var k = 0; k < 4 && n < Effects.MaxColourStrips; k++, n++)
                Assert.True(MixParams.AddRackItem(s, st.Id, Effects.Default("colour")));
        Assert.Equal(Effects.MaxColourStrips, Effects.Usage(s)[EffectPool.ColourStrip]);
        Assert.NotNull(Effects.WhyNot(s, s.Channels[31], "colour"));

        var t = MixState.CreateDefault();
        var stereo = t.AllStrips().Where(x => x.Stereo).Take(4).ToList();
        Assert.Equal(4, stereo.Count);
        foreach (var st in stereo)
        {
            Assert.Null(Effects.WhyNot(t, st, "multiband6"));
            MixParams.AddRackItem(t, st.Id, Effects.Default("multiband6"));
        }
        Assert.Equal(8, Effects.Usage(t)[EffectPool.Rack]);
        Assert.Contains("full", Effects.WhyNot(t, t.Channels[0], "dyneq6")!);
        Assert.Null(Effects.WhyNot(t, t.Channels[0], "reverb"));
    }

    // ---- Nodes and new effects -------------------------------------------------------------------

    [Fact]
    public void The_EQ_node_cuts_and_boosts()
    {
        var p = new EqNode(48000, Block);
        p.Configure(Item("eq", ("m1f", 1000), ("m1g", -9)));
        var x = Sine(1000, 0.3, 19200);
        Assert.InRange(Db(Rms(Run(p, x), 9600) / Rms(x, 9600)), -9.5, -8.5);
        var lc = new EqNode(48000, Block);
        lc.Configure(Item("eq", ("lowcut", 400)));
        var low = Sine(60, 0.3, 48000);
        Assert.True(Db(Rms(Run(lc, low), 24000) / Rms(low, 24000)) < -30);
    }

    [Fact]
    public void A_gate_node_keyed_from_another_channel_ducks()
    {
        var s = MixRig.Plain();
        foreach (var c in s.Channels) c.ToMain = false;
        s.Channels[1].ToMain = true;
        s.Outputs[0].Source = "main:l";
        var g = Item("gate", ("threshold", -30), ("range", 20));
        g.Text["mode"] = "Ducker";
        g.Text["key"] = "ch1";
        MixParams.AddRackItem(s, "ch2", g);
        s.Repair();
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [1] = 0.4f }, 200);
        var open = o[0][^1];
        MixRig.Settle(e, i, o, new() { [0] = 0.5f, [1] = 0.4f }, 200);
        Assert.InRange(Db(o[0][^1] / open), -21, -19);
    }

    [Fact]
    public void The_pitch_shifter_moves_a_tone_up_an_octave()
    {
        var p = new PitchShifter(48000, Block);
        p.Configure(Item("pitchshift", ("semis", 12)));
        var y = Run(p, Sine(220, 0.5, 48000));
        // Count rising zero crossings over the last half second.
        var crossings = 0;
        for (var n = 24001; n < y.Length; n++) if (y[n - 1] < 0 && y[n] >= 0) crossings++;
        Assert.InRange(crossings * 2, 400, 480);
    }

    [Fact]
    public void The_enhancer_lifts_the_top_of_a_voice_and_leaves_the_low_end()
    {
        double Gain(double f)
        {
            var p = new Enhancer(48000, Block);
            p.Configure(Item("enhancer", ("amount", 60), ("freq", 3000)));
            var x = Sine(f, 0.2, 48000);
            return Db(Rms(Run(p, x), 24000) / Rms(x, 24000));
        }
        var g6 = Gain(6000); Assert.True(g6 > 1.5, $"6k {g6} 150 {Gain(150)}");
        Assert.InRange(Gain(150), -0.3, 0.3);
    }

    [Fact]
    public void The_new_reverbs_have_tails_and_the_church_rings_longest()
    {
        double Tail(string style)
        {
            var r = Effects.Default("reverb", style);
            var p = new Reverb(48000, Block);
            p.Configure(r);
            var x = new float[48000 * 2];
            x[0] = 1;
            var y = new float[x.Length];
            for (var b = 0; b + Block <= x.Length; b += Block)
            {
                var l = x.AsSpan(b, Block).ToArray();
                var rr = x.AsSpan(b, Block).ToArray();
                p.Run(l, rr, Block);
                Array.Copy(l, 0, y, b, Block);
            }
            return Rms(y, 48000); // the second second
        }
        var church = Tail("church");
        var chamber = Tail("chamber");
        var ambience = Tail("ambience");
        Assert.True(chamber > 0);
        Assert.True(church > chamber && chamber > ambience, $"church {church}, chamber {chamber}, ambience {ambience}");
    }

    [Fact]
    public void Tap_tempo_turns_taps_into_beats_per_minute()
    {
        var t0 = new DateTime(2026, 11, 29, 10, 0, 0, DateTimeKind.Utc);
        Assert.Null(Effects.TempoFrom([t0]));
        Assert.Equal(120, Effects.TempoFrom([t0, t0.AddSeconds(0.5), t0.AddSeconds(1.0), t0.AddSeconds(1.5)]));
        Assert.Equal(75, Effects.TempoFrom([t0, t0.AddSeconds(0.8)]));
    }

    [Fact]
    public void Every_effect_can_be_made_and_cutoffs_at_zero_read_off()
    {
        foreach (var e in Effects.All)
            Assert.NotNull(Effects.Create(Effects.Default(e.Type), 48000, Block));
        var lowcut = Effects.Get("eq")!.Params.First(p => p.Key == "lowcut");
        Assert.Equal("Off", lowcut.Format(0));
        Assert.Equal("+7 st", Effects.Get("pitchshift")!.Params[0].Format(7));
    }

    // ---- Resources -----------------------------------------------------------------------------

    [Fact]
    public void The_engine_measures_what_each_strip_costs()
    {
        var s = MixRig.Plain();
        MixParams.AddRackItem(s, "ch1", Effects.Default("reverb", "church"));
        var (e, i, o) = MixRig.Make(s);
        e.TakeCosts();
        MixRig.Settle(e, i, o, new() { [0] = 0.3f }, 50);
        var cost = e.TakeCosts();
        Assert.Equal(s.AllStrips().Count(), cost.Length);
        Assert.All(cost, c => Assert.True(c >= 0));
        Assert.True(cost[0] > 0);
    }
}
