using System.Text.Json;
using Nexora.Audio.Dsp;
using Nexora.Audio.Drums;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;
using Nexora.Audio.Show;

namespace Nexora.Tests;

/// <summary>Drum triggers: detection, layers, blend, the library, scenes and show files.</summary>
public class DrumTriggerTests
{
    private const int Rate = 48000;
    private const int Block = 64;

    /// <summary>A drum-like hit: a 1 kHz crack that starts at once and dies away.</summary>
    private static void AddHit(float[] buf, int at, float amp, double freq = 1000, double decayMs = 60)
    {
        for (var i = at; i < buf.Length; i++)
        {
            var t = (i - at) / (double)Rate;
            buf[i] += (float)(amp * Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-t * 1000 / decayMs));
        }
    }

    /// <summary>A sample that is a steady value, so what is playing is easy to see.</summary>
    private static DrumSample Dc(string name, float v, int len = 4800) => new(name, Enumerable.Repeat(v, len).ToArray());

    private static (DrumTrigger T, float[] Out) Run(TriggerSettings s, DrumSample[][] layers, float[] input)
    {
        var t = new DrumTrigger(Rate, Block);
        t.Configure(new TriggerConfig { Settings = s, Layers = layers });
        var output = new float[input.Length];
        var buf = new float[Block];
        for (var at = 0; at + Block <= input.Length; at += Block)
        {
            Array.Copy(input, at, buf, 0, Block);
            t.Run(buf, null, Block);
            Array.Copy(buf, 0, output, at, Block);
        }
        return (t, output);
    }

    private static TriggerHit[] History(DrumTrigger t)
    {
        var buf = new TriggerHit[64];
        var n = t.ReadHistory(buf, out _);
        return buf[..n];
    }

    private static TriggerSettings Sample100(double threshold = -24) => new() { On = true, Blend = 100, Threshold = threshold, Dynamics = 0, NaturalGate = false };

    [Fact]
    public void A_hit_over_the_threshold_plays_the_sample_within_two_milliseconds()
    {
        var input = new float[Rate / 2];
        AddHit(input, 4800, 0.5f);
        var (t, o) = Run(Sample100(), [[Dc("a", 0.3f)]], input);
        Assert.Equal(1, t.Hits);
        var first = Array.FindIndex(o, x => Math.Abs(x) > 0.1f);
        Assert.InRange(first, 4800, 4800 + (int)(0.002 * Rate));
        // Blend 100: before the hit, nothing of the natural mic.
        Assert.All(o[..4800], x => Assert.Equal(0f, x, 6));
    }

    [Fact]
    public void A_soft_hit_under_the_threshold_plays_nothing_and_is_shown_as_too_soft()
    {
        var input = new float[Rate / 2];
        AddHit(input, 4800, Dsp.Db2Lin(-27));
        var (t, o) = Run(Sample100(-24), [[Dc("a", 0.3f)]], input);
        Assert.Equal(0, t.Hits);
        Assert.All(o, x => Assert.True(Math.Abs(x) < 0.05f));
        var h = Assert.Single(History(t));
        Assert.Equal(HitKind.TooSoft, h.Kind);
    }

    [Fact]
    public void The_hold_stops_one_hit_firing_twice()
    {
        var input = new float[Rate / 2];
        // A hit and a second, harder one 20 ms later (a flam): with a 40 ms hold, one sample, not two.
        AddHit(input, 4800, 0.5f);
        AddHit(input, 4800 + 960, 0.9f);
        var s = Sample100();
        s.Hold = 40;
        var (t, _) = Run(s, [[Dc("a", 0.3f)]], input);
        Assert.Equal(1, t.Hits);
        // With a short hold, both fire.
        s = Sample100();
        s.Hold = 5;
        (t, _) = Run(s, [[Dc("a", 0.3f)]], input);
        Assert.Equal(2, t.Hits);
    }

    [Fact]
    public void A_drum_still_ringing_never_fires_again_by_itself()
    {
        var input = new float[Rate];
        foreach (var freq in new[] { 90.0, 200, 1000 })
        {
            var input2 = new float[Rate];
            AddHit(input2, 4800, 0.9f, freq, decayMs: 300); // a long, ringing floor tom
            var s = Sample100(-40);
            s.Hold = 5;
            s.FocusLow = 40;
            var (t, _) = Run(s, [[Dc("a", 0.3f)]], input2);
            Assert.Equal(1, t.Hits);
        }
    }

    [Fact]
    public void A_ghost_note_right_after_a_loud_hit_still_fires()
    {
        var input = new float[Rate];
        AddHit(input, 4800, 0.9f, decayMs: 40);
        AddHit(input, 4800 + 4800, Dsp.Db2Lin(-20), decayMs: 40); // 100 ms later, 19 dB softer
        var (t, _) = Run(Sample100(-24), [[Dc("a", 0.3f)]], input);
        Assert.Equal(2, t.Hits);
    }

    [Fact]
    public void Soft_hits_play_the_soft_layer_and_hard_hits_the_hard_one()
    {
        var input = new float[Rate];
        AddHit(input, 4800, Dsp.Db2Lin(-20));   // just over the threshold
        AddHit(input, 28800, Dsp.Db2Lin(-1));   // as hard as it gets
        var layers = new[] { new[] { Dc("soft", 0.1f) }, [Dc("medium", 0.2f)], [Dc("hard", 0.3f)], [Dc("full", 0.4f)] };
        var (t, o) = Run(Sample100(), layers, input);
        var h = History(t);
        Assert.Equal(2, h.Length);
        Assert.Equal(0, h[0].Layer);
        Assert.Equal(3, h[1].Layer);
        Assert.Equal(0.1f, o[4800 + 200], 2);
        Assert.Equal(0.4f, o[28800 + 200], 2);
    }

    [Fact]
    public void Dynamics_makes_softer_hits_quieter()
    {
        var input = new float[Rate];
        AddHit(input, 4800, Dsp.Db2Lin(-20));
        AddHit(input, 28800, Dsp.Db2Lin(-1));
        var s = Sample100();
        s.Dynamics = 100;
        var (_, o) = Run(s, [[Dc("a", 0.5f)]], input);
        Assert.True(o[4800 + 200] < o[28800 + 200] * 0.25f);
        s.Dynamics = 0;
        (_, o) = Run(s, [[Dc("a", 0.5f)]], input);
        Assert.Equal(o[4800 + 200], o[28800 + 200], 3);
    }

    [Fact]
    public void Round_robin_takes_turns_between_a_layers_samples()
    {
        var input = new float[Rate];
        for (var i = 0; i < 4; i++) AddHit(input, 4800 + i * 9600, 0.5f, decayMs: 10);
        var takes = new[] { Dc("1", 0.1f, 2000), Dc("2", 0.2f, 2000) };
        var (_, o) = Run(Sample100(), [takes], input);
        var played = Enumerable.Range(0, 4).Select(i => MathF.Round(o[4800 + i * 9600 + 300], 2)).ToArray();
        Assert.Equal([0.1f, 0.2f, 0.1f, 0.2f], played);
        var s = Sample100();
        s.RoundRobin = false;
        (_, o) = Run(s, [takes], input);
        played = Enumerable.Range(0, 4).Select(i => MathF.Round(o[4800 + i * 9600 + 300], 2)).ToArray();
        Assert.Equal([0.1f, 0.1f, 0.1f, 0.1f], played);
    }

    [Fact]
    public void The_spill_filter_keeps_kick_boom_from_firing_the_snare()
    {
        var input = new float[Rate / 2];
        AddHit(input, 4800, 0.3f, freq: 50); // the kick's boom in the snare mic
        var s = Sample100(-24);
        s.FocusLow = 300;
        var (t, _) = Run(s, [[Dc("a", 0.3f)]], input);
        Assert.Equal(0, t.Hits);
        s.FocusLow = 20;
        (t, _) = Run(s, [[Dc("a", 0.3f)]], input);
        Assert.Equal(1, t.Hits);
    }

    [Fact]
    public void Blend_mixes_the_natural_mic_with_the_sample()
    {
        var input = new float[Rate / 2];
        Array.Fill(input, 0.02f, 0, 2400); // steady quiet sound before any hit
        var s = Sample100();
        s.Blend = 0;
        var (_, o) = Run(s, [[Dc("a", 0.3f)]], input);
        Assert.Equal(0.02f, o[1000], 4);
        s.Blend = 50;
        (_, o) = Run(s, [[Dc("a", 0.3f)]], input);
        Assert.Equal(0.01f, o[1000], 4);
    }

    [Fact]
    public void The_natural_gate_lets_the_mic_through_only_around_hits()
    {
        var input = new float[Rate];
        for (var i = 0; i < input.Length; i++) input[i] = 0.01f; // hiss and spill between hits
        AddHit(input, 9600, 0.5f);
        var s = Sample100();
        s.Blend = 50;
        s.NaturalGate = true;
        s.GateTime = 100;
        var (_, o) = Run(s, [[Dc("a", 0f)]], input);
        Assert.True(Math.Abs(o[4800]) < 1e-4, "closed before the hit");
        Assert.True(o.Skip(9600).Take(2400).Max(Math.Abs) > 0.15f, "open on the hit");
        Assert.True(Math.Abs(o[9600 + 4800 + 4800]) < 1e-3, "closed again after");
    }

    [Fact]
    public void With_no_samples_or_compare_on_the_natural_mic_plays_in_full()
    {
        var input = new float[Rate / 2];
        Array.Fill(input, 0.02f, 0, 2400);
        AddHit(input, 9600, 0.5f);
        var (t, o) = Run(Sample100(), [], input);
        Assert.Equal(0.02f, o[1000], 4);
        Assert.Equal(1, t.Hits); // the light still shows the hit
        var s = Sample100();
        s.Compare = true;
        (t, o) = Run(s, [[Dc("a", 0.3f)]], input);
        Assert.Equal(0.02f, o[1000], 4);
        Assert.Equal(1, t.Hits);
    }

    [Fact]
    public void Learn_sets_the_threshold_under_the_softest_of_eight_hits()
    {
        var t = new DrumTrigger(Rate, Block);
        t.Configure(new TriggerConfig { Settings = Sample100(), Layers = [] });
        t.StartLearn();
        var input = new float[Rate * 3];
        for (var i = 0; i < 8; i++) AddHit(input, 4800 + i * 14400, i == 3 ? Dsp.Db2Lin(-30) : 0.4f, decayMs: 20);
        var buf = new float[Block];
        for (var at = 0; at + Block <= input.Length; at += Block)
        {
            Array.Copy(input, at, buf, 0, Block);
            t.Run(buf, null, Block);
        }
        Assert.False(t.Learning);
        var r = t.LearnResult();
        Assert.NotNull(r);
        Assert.InRange(r!.Value.Threshold, -38, -34);
        Assert.InRange(r.Value.Hold, 20, 80);
    }

    // ---- The library and the desk ----------------------------------------------------------------

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "nexaudio-drums-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void The_starter_kit_is_made_with_four_strengths_and_two_takes_per_drum()
    {
        var dir = TempDir();
        try
        {
            var lib = new DrumLibrary(dir);
            lib.EnsureStarter();
            var list = lib.List();
            Assert.Equal(4 * 4 * 2, list.Count);
            foreach (var drum in new[] { "Kick", "Snare", "Rack tom", "Floor tom" })
                foreach (var layer in StarterKit.Layers(drum))
                    foreach (var p in layer)
                    {
                        var smp = lib.Load(p, 48000);
                        Assert.NotNull(smp);
                        var peak = smp!.Data.Max(MathF.Abs);
                        Assert.InRange(peak, 0.3f, 1f);
                    }
            // Harder layers are louder, and the two takes are not identical.
            var soft = lib.Load("Starter/Snare soft 1.wav", 48000)!.Data.Max(MathF.Abs);
            var full = lib.Load("Starter/Snare full 1.wav", 48000)!.Data.Max(MathF.Abs);
            Assert.True(full > soft * 1.5f);
            Assert.NotEqual(lib.Load("Starter/Snare hard 1.wav", 48000)!.Data[100], lib.Load("Starter/Snare hard 2.wav", 48000)!.Data[100]);
            // A sample is turned into the desk's own rate.
            var at96 = lib.Load("Starter/Kick full 1.wav", 96000)!;
            Assert.InRange(at96.Data.Length, lib.Load("Starter/Kick full 1.wav", 48000)!.Data.Length * 2 - 2, lib.Load("Starter/Kick full 1.wav", 48000)!.Data.Length * 2 + 2);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Library_paths_cannot_point_outside_the_library()
    {
        var lib = new DrumLibrary(TempDir());
        Assert.Null(lib.FullPath("../desk.json"));
        Assert.Null(lib.FullPath("Starter/../../x.wav"));
        Assert.Null(lib.FullPath("Kit/sub/x.wav"));
        Assert.Null(lib.FullPath("Kit/x.exe"));
        Assert.NotNull(lib.FullPath("Sunday kit/Snare (tight) 2.wav"));
    }

    [Fact]
    public void A_triggered_channel_in_the_mix_plays_the_starter_snare_and_lights_its_meter()
    {
        var dir = TempDir();
        try
        {
            var lib = new DrumLibrary(dir);
            lib.EnsureStarter();
            var s = MixRig.Plain();
            var ch = s.Channels[0];
            ch.Trigger = new TriggerSettings
            {
                On = true, Blend = 100, Threshold = -24,
                Layers = StarterKit.Layers("Snare").Select(l => new TriggerLayer { Samples = l }).ToList(),
            };
            ch.DirectTap = SendTap.Pre;
            s.Outputs[0].Source = "ch:1";
            var e = new MixEngine(32, 32, MixRig.Frames, MixRig.Rate) { Drums = lib };
            e.Load(s);
            var input = Enumerable.Range(0, 32).Select(_ => new float[MixRig.Frames]).ToArray();
            var output = Enumerable.Range(0, 32).Select(_ => new float[MixRig.Frames]).ToArray();
            var mic = new float[Rate / 2];
            AddHit(mic, 4800, 0.5f);
            var heard = new List<float>();
            for (var at = 0; at + MixRig.Frames <= mic.Length; at += MixRig.Frames)
            {
                Array.Copy(mic, at, input[0], 0, MixRig.Frames);
                e.Process(input, output, MixRig.Frames);
                heard.AddRange(output[0]);
            }
            var m = e.TakeMeters();
            Assert.InRange(m[0].Trigger, 0.1f, 1f);
            // The snare sample, not the 1 kHz tone: the output is far from the input.
            var diff = heard.Zip(mic).Skip(4800).Take(4800).Sum(p => Math.Abs(p.First - p.Second));
            Assert.True(diff > 50, $"diff {diff}");
            Assert.Empty(e.TriggerProblems());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_missing_sample_falls_back_to_the_natural_mic_and_is_reported()
    {
        var dir = TempDir();
        try
        {
            var lib = new DrumLibrary(dir);
            var s = MixRig.Plain();
            var ch = s.Channels[0];
            ch.Trigger = new TriggerSettings { On = true, Blend = 100, Layers = [new TriggerLayer { Samples = ["Gone/Snare.wav"] }] };
            ch.DirectTap = SendTap.Pre;
            s.Outputs[0].Source = "ch:1";
            var (e, i, o) = MixRig.Make(s);
            e.Drums = lib;
            e.Load(s);
            MixRig.Settle(e, i, o, new() { [0] = 0.25f });
            Assert.Equal(0.25f, o[0][^1], 3);
            Assert.Equal(("ch1", 1), Assert.Single(e.TriggerProblems()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Trigger_settings_change_by_path_and_stay_in_range()
    {
        var s = MixRig.Plain();
        Assert.True(MixParams.Apply(s, "ch3.trigger.on", MixRig.J(true)));
        Assert.True(MixParams.Apply(s, "ch3.trigger.threshold", MixRig.J(-90)));
        Assert.True(MixParams.Apply(s, "ch3.trigger.blend", MixRig.J(250)));
        Assert.True(MixParams.Apply(s, "ch3.trigger.layers", MixRig.J(new[] { new TriggerLayer { Samples = ["Starter/Snare soft 1.wav"] }, new(), new(), new(), new(), new() })));
        var t = s.Find("ch3")!.Trigger;
        Assert.True(t.On);
        Assert.Equal(-60, t.Threshold);
        Assert.Equal(100, t.Blend);
        Assert.Equal(TriggerSettings.MaxLayers, t.Layers.Count);
        Assert.True(MixParams.Apply(s, "ch3.trigger.layers.0.samples", MixRig.J(new[] { "A/1.wav", "A/2.wav" })));
        Assert.Equal(2, s.Find("ch3")!.Trigger.Layers[0].Samples.Count);
        // Buses have no trigger.
        Assert.True(MixParams.Apply(s, "mix1.trigger.on", MixRig.J(true)) || true);
        Assert.False(s.Find("mix1")?.Trigger.On ?? false);
    }

    [Fact]
    public void Scenes_recall_triggers_unless_left_out_of_the_scope()
    {
        var saved = MixRig.Plain();
        saved.Channels[1].Trigger = new TriggerSettings { On = true, Threshold = -30, Layers = [new TriggerLayer { Samples = ["Starter/Kick full 1.wav"] }] };
        var now = MixRig.Plain();
        var all = SceneRecall.Apply(now, saved, RecallScope.All);
        Assert.True(all.Channels[1].Trigger.On);
        Assert.Equal(-30, all.Channels[1].Trigger.Threshold);
        var noTrig = SceneRecall.Apply(now, saved, RecallScope.All & ~RecallScope.Trigger);
        Assert.False(noTrig.Channels[1].Trigger.On);
        Assert.Contains(RecallScope.Trigger, RecallScopes.Sections);
        Assert.True((RecallScope.Processing & RecallScope.Trigger) != 0);
    }

    [Fact]
    public void A_show_file_carries_your_own_samples_but_not_the_starter_kit()
    {
        var a = TempDir();
        var b = TempDir();
        try
        {
            var lib = new DrumLibrary(a);
            lib.EnsureStarter();
            var wav = Path.Combine(a, "my snare.wav");
            File.Copy(Path.Combine(lib.Root, "Starter", "Snare full 1.wav"), wav);
            var path = lib.Import(wav, "Sunday kit");
            Assert.Equal("Sunday kit/my snare.wav", path);
            var show = Path.Combine(a, "test" + ShowFiles.Extension);
            ShowFiles.Export(a, show, "Test", "me");
            var c = ShowFiles.Inspect(show);
            Assert.Equal(1, c.Samples);
            ShowFiles.Import(b, show, takeMix: false);
            var other = new DrumLibrary(b);
            Assert.True(other.Exists("Sunday kit/my snare.wav"));
            Assert.False(Directory.Exists(Path.Combine(b, "drums", "Starter")));
        }
        finally
        {
            Directory.Delete(a, true);
            Directory.Delete(b, true);
        }
    }

    [Fact]
    public void The_built_in_triggered_presets_use_the_starter_kit()
    {
        var p = PresetStore.BuiltIns.Single(x => x.Id == "builtin-trig-snare");
        Assert.Equal("Drums", p.Folder);
        Assert.True(p.Sound.Trigger.On);
        Assert.Equal(4, p.Sound.Trigger.Layers.Count);
        Assert.All(p.Sound.Trigger.Layers, l => Assert.Equal(2, l.Samples.Count));
        Assert.All(p.Sound.Trigger.AllSamples(), x => Assert.StartsWith("Starter/", x));
    }

    [Fact]
    public void The_trigger_survives_a_save_and_load_of_the_mix()
    {
        var s = MixRig.Plain();
        s.Channels[4].Trigger = new TriggerSettings { On = true, Pitch = -2, Key = "ch6", Layers = [new TriggerLayer { Samples = ["K/a.wav", "K/b.wav"] }] };
        var back = MixState.FromJson(s.ToJson())!;
        var t = back.Channels[4].Trigger;
        Assert.True(t.On);
        Assert.Equal(-2, t.Pitch);
        Assert.Equal("ch6", t.Key);
        Assert.Equal(["K/a.wav", "K/b.wav"], t.Layers[0].Samples);
        // An old mix file with no trigger in it opens with the trigger off.
        var old = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(s.ToJson())!;
        foreach (var ch in old["channels"]!.AsArray()) ch!.AsObject().Remove("trigger");
        var fromOld = MixState.FromJson(old.ToJsonString())!;
        Assert.False(fromOld.Channels[4].Trigger.On);
        Assert.Single(fromOld.Channels[4].Trigger.Layers);
    }
}
