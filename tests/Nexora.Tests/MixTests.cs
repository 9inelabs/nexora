using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Nexora.Audio;
using Nexora.Audio.Dsp;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;
using Nexora.Audio.Plugins;
using Nexora.Audio.X32;

namespace Nexora.Tests;

internal static class MixRig
{
    public const int Frames = 64;
    public const double Rate = 48000;

    public static (MixEngine Engine, float[][] In, float[][] Out) Make(MixState s, Func<RackItem, Processor?>? plugins = null)
    {
        var e = new MixEngine(32, 32, Frames, Rate, plugins);
        e.Load(s);
        var input = Enumerable.Range(0, 32).Select(_ => new float[Frames]).ToArray();
        var output = Enumerable.Range(0, 32).Select(_ => new float[Frames]).ToArray();
        return (e, input, output);
    }

    /// <summary>Runs enough blocks for gains to settle, with a steady value on the given inputs.</summary>
    public static void Settle(MixEngine e, float[][] input, float[][] output, Dictionary<int, float> dc, int blocks = 60)
    {
        for (var b = 0; b < blocks; b++)
        {
            foreach (var x in input) Array.Clear(x);
            foreach (var (ch, v) in dc) Array.Fill(input[ch], v);
            e.Process(input, output, Frames);
        }
    }

    public static MixState Plain()
    {
        var s = MixState.CreateDefault();
        foreach (var c in s.Channels) c.Hpf.On = false;
        return s;
    }

    public static JsonElement J(object v) => JsonSerializer.SerializeToElement(v, MixState.Json);
}

public class MixEngineTests
{
    [Fact]
    public void A_channel_reaches_the_main_mix_at_minus_3_dB_in_the_centre()
    {
        var s = MixRig.Plain();
        s.Outputs[0].Source = "main:l";
        s.Outputs[1].Source = "main:r";
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Assert.InRange(o[0][^1], 0.5f * 0.7071f - 0.01f, 0.5f * 0.7071f + 0.01f);
        Assert.InRange(o[1][^1], 0.5f * 0.7071f - 0.01f, 0.5f * 0.7071f + 0.01f);
    }

    [Fact]
    public void The_default_outputs_pass_each_input_straight_through()
    {
        var (e, i, o) = MixRig.Make(MixRig.Plain());
        MixRig.Settle(e, i, o, new() { [4] = 0.25f });
        Assert.Equal(0.25f, o[4][^1], 3);
        Assert.Equal(0f, o[5][^1], 5);
    }

    [Fact]
    public void Mute_silences_and_minus_6_dB_halves()
    {
        var s = MixRig.Plain();
        s.Outputs[0].Source = "main:l";
        s.Channels[0].Pan = -1; // all left: full level on L
        s.Channels[0].Fader = -6.0206;
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.8f });
        Assert.InRange(o[0][^1], 0.39f, 0.41f);

        s.Channels[0].Mute = true;
        e.Load(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.8f });
        Assert.Equal(0f, o[0][^1], 4);
    }

    [Fact]
    public void A_pre_fader_monitor_send_carries_on_when_the_fader_is_down()
    {
        var s = MixRig.Plain();
        var mix1 = s.BusesOf(StripKind.Mix).First();
        s.Channels[2].Fader = Db.Floor;
        Assert.True(MixParams.Apply(s, $"ch3.send.{mix1.Id}.level", MixRig.J(0.0)));
        Assert.Equal(SendTap.Pre, s.Channels[2].SendTo(mix1.Id)!.Tap);
        mix1.Fader = 0;
        s.Outputs[9].Source = mix1.Id;
        s.Outputs[0].Source = "main:l";
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [2] = 0.3f });
        Assert.InRange(o[9][^1], 0.29f, 0.31f);
        Assert.Equal(0f, o[0][^1], 4);
    }

    [Fact]
    public void Channels_line_up_when_one_has_a_look_ahead_limiter()
    {
        var s = MixRig.Plain();
        s.Outputs[0].Source = "main:l";
        s.Channels[0].Pan = -1;
        s.Channels[1].Pan = -1;
        s.Channels[1].Mute = true;
        MixParams.AddRackItem(s, "ch1", Effects.Default("limiter"));
        var (e, i, o) = MixRig.Make(s);
        var lat = e.MixLatency;
        Assert.Equal(48, lat); // 1 ms at 48 kHz

        int ArrivalOf(int ch)
        {
            var (en, inp, outp) = MixRig.Make(s);
            for (var b = 0; b < 40; b++)
            {
                foreach (var x in inp) Array.Clear(x);
                if (b == 20) inp[ch][0] = 0.5f;
                en.Process(inp, outp, MixRig.Frames);
                for (var n = 0; n < MixRig.Frames; n++)
                    if (Math.Abs(outp[0][n]) > 0.05f) return (b - 20) * MixRig.Frames + n;
            }
            return -1;
        }

        var a = ArrivalOf(0);
        s.Channels[0].Mute = true;
        s.Channels[1].Mute = false;
        var b2 = ArrivalOf(1);
        Assert.Equal(lat, a);
        Assert.Equal(a, b2);
    }

    [Fact]
    public void The_tone_output_plays_minus_18_dBFS()
    {
        var s = MixRig.Plain();
        s.Outputs[3].Source = "tone";
        var (e, i, o) = MixRig.Make(s);
        var peak = 0f;
        for (var b = 0; b < 50; b++)
        {
            e.Process(i, o, MixRig.Frames);
            peak = Math.Max(peak, o[3].Max(Math.Abs));
        }
        Assert.InRange(peak, 0.12f, 0.13f);
    }

    [Fact]
    public void Every_built_in_effect_makes_clean_sound()
    {
        foreach (var info in Effects.All)
        {
            var item = Effects.Default(info.Type);
            var p = Effects.Create(item, MixRig.Rate, MixRig.Frames)!;
            p.Configure(item);
            var l = new float[MixRig.Frames];
            var r = new float[MixRig.Frames];
            var rnd = new Random(1);
            for (var b = 0; b < 200; b++)
            {
                for (var n = 0; n < MixRig.Frames; n++)
                {
                    l[n] = (float)(Math.Sin((b * MixRig.Frames + n) * 0.05) * 0.5 + (rnd.NextDouble() - 0.5) * 0.1);
                    r[n] = l[n];
                }
                p.Run(l, r, MixRig.Frames);
                Assert.All(l, v => Assert.True(float.IsFinite(v) && Math.Abs(v) < 4, $"{info.Type} gave {v}"));
            }
        }
    }

    [Fact]
    public void Mixing_allocates_nothing_once_running()
    {
        var s = MixRig.Plain();
        var mix1 = s.BusesOf(StripKind.Mix).First();
        foreach (var c in s.Channels) MixParams.Apply(s, $"{c.Id}.send.{mix1.Id}.level", MixRig.J(-10.0));
        var fx = s.BusesOf(StripKind.Fx).First();
        foreach (var c in s.Channels.Take(8)) MixParams.Apply(s, $"{c.Id}.send.{fx.Id}.level", MixRig.J(-10.0));
        s.Channels[0].Comp.On = true;
        s.Channels[0].Gate.On = true;
        var (e, i, o) = MixRig.Make(s);
        for (var b = 0; b < 200; b++) e.Process(i, o, MixRig.Frames);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var b = 0; b < 1000; b++) e.Process(i, o, MixRig.Frames);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}

public class MixParamsTests
{
    [Fact]
    public void Settings_change_by_path_and_stay_in_range()
    {
        var s = MixState.CreateDefault();
        Assert.True(MixParams.Apply(s, "ch3.fader", MixRig.J(-10.0)));
        Assert.Equal(-10, s.Channels[2].Fader);
        Assert.True(MixParams.Apply(s, "ch3.fader", MixRig.J(40.0)));
        Assert.Equal(10, s.Channels[2].Fader);
        Assert.True(MixParams.Apply(s, "ch3.comp.ratio", MixRig.J(4.0)));
        Assert.Equal(4, s.Channels[2].Comp.Ratio);
        Assert.True(MixParams.Apply(s, "out.5.source", MixRig.J("main:l")));
        Assert.Equal("main:l", s.Outputs[4].Source);
        Assert.False(MixParams.Apply(s, "ch99.fader", MixRig.J(0.0)));
        Assert.False(MixParams.Apply(s, "ch3.nothing", MixRig.J(0.0)));
    }

    [Fact]
    public void Buses_and_effects_can_be_added_and_removed()
    {
        var s = MixState.CreateDefault();
        var before = s.BusesOf(StripKind.Mix).Count();
        var bus = MixParams.AddBus(s, StripKind.Mix, "Drummer");
        Assert.NotNull(bus);
        Assert.Equal(before + 1, s.BusesOf(StripKind.Mix).Count());
        Assert.True(MixParams.AddRackItem(s, "ch1", Effects.Default("deesser")));
        var id = s.Channels[0].Rack[0].Id;
        Assert.True(MixParams.Apply(s, $"ch1.rack.{id}.bypass", MixRig.J(true)));
        Assert.True(s.Channels[0].Rack[0].Bypass);
        Assert.True(MixParams.RemoveRackItem(s, "ch1", id));
        Assert.True(MixParams.RemoveBus(s, bus!.Id));
        Assert.Equal(before, s.BusesOf(StripKind.Mix).Count());
    }
}

public class SceneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexora-scenes-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Scenes_save_and_load_and_snapshots_keep_their_order()
    {
        var store = new SceneStore(_dir);
        var mix = MixState.CreateDefault();
        mix.Channels[0].Fader = -12;
        var a = store.Save(new SavedMix { Name = "Sunday", Mix = mix }, false);
        var s1 = store.Save(new SavedMix { Name = "Worship", Mix = mix }, true);
        var s2 = store.Save(new SavedMix { Name = "Sermon", Mix = mix }, true);
        Assert.Equal(-12, store.Load(a.Id, false)!.Mix.Channels[0].Fader);
        Assert.Equal(["Worship", "Sermon"], store.Snapshots.Select(x => x.Name));
        store.Reorder([s2.Id, s1.Id]);
        Assert.Equal(["Sermon", "Worship"], store.Snapshots.Select(x => x.Name));
        Assert.True(store.Delete(s1.Id, true));
        Assert.Single(store.Snapshots);
    }

    [Fact]
    public void A_levels_only_recall_keeps_the_eq_and_safe_strips_are_left_alone()
    {
        var live = MixState.CreateDefault();
        live.Channels[0].Comp.Ratio = 8;
        live.Channels[1].RecallSafe = true;
        live.Channels[1].Fader = -3;
        var saved = MixState.CreateDefault();
        saved.Channels[0].Fader = -20;
        saved.Channels[0].Comp.Ratio = 2;
        saved.Channels[1].Fader = -30;
        var r = SceneRecall.Apply(live, saved, RecallScope.Levels);
        Assert.Equal(-20, r.Channels[0].Fader);
        Assert.Equal(8, r.Channels[0].Comp.Ratio);
        Assert.Equal(-3, r.Channels[1].Fader);
        var all = SceneRecall.Apply(live, saved, RecallScope.All);
        Assert.Equal(2, all.Channels[0].Comp.Ratio);
    }

    [Fact]
    public void A_fade_glides_the_faders()
    {
        var a = MixState.CreateDefault();
        var b = MixState.CreateDefault();
        a.Channels[0].Fader = 0;
        b.Channels[0].Fader = -20;
        var half = SceneRecall.Blend(a, b, 0.5);
        Assert.InRange(half.Channels[0].Fader, -19.9, -0.1);
        Assert.Equal(-20, SceneRecall.Blend(a, b, 1).Channels[0].Fader);
    }
}

public class ShadowMixTests
{
    [Theory]
    [InlineData(10.0)]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(-30.0)]
    [InlineData(-50.0)]
    [InlineData(-70.0)]
    public void The_X32_fader_law_round_trips(double db)
    {
        Assert.Equal(db, X32Client.FaderToDb(X32Client.DbToFader(db)), 1);
    }

    [Fact]
    public void Unity_is_three_quarters_up_like_on_the_desk()
    {
        Assert.Equal(0.75f, X32Client.DbToFader(0), 3);
    }

    [Fact]
    public void The_desk_mix_follows_NEX_Audio_and_back()
    {
        var s = MixState.CreateDefault();
        s.Channels[4].Fader = -10;
        s.Channels[4].Mute = true;
        var d = ShadowMix.Desired(s);
        Assert.Equal(X32Client.DbToFader(-10), d["/ch/05/mix/fader"], 3);
        Assert.Equal(0, d["/ch/05/mix/on"]);
        var back = ShadowMix.FromDesk(s, "/ch/05/mix/fader", X32Client.DbToFader(-20));
        Assert.Equal("ch5.fader", back!.Value.Path);
        Assert.Equal(-20.0, (double)back.Value.Value, 1);
        Assert.Equal(("main.mute", (object)true), ShadowMix.FromDesk(s, "/main/st/mix/on", 0));
        Assert.Null(ShadowMix.FromDesk(s, "/config/whatever", 1));
    }
}

public class FailsafeTests
{
    private static Heartbeat Beat(long seq, bool flowing = true, bool armed = true, int epoch = 0, int timeoutMs = 500) =>
        new("pc1", "SOUND-PC", seq, "running", flowing, armed, false, epoch, "127.0.0.1", 0, 5, timeoutMs);

    [Fact]
    public void A_heartbeat_survives_the_network()
    {
        var hb = Beat(42);
        Assert.Equal(hb, Heartbeat.Decode(hb.Encode()));
        Assert.Null(Heartbeat.Decode("nonsense"u8));
    }

    [Fact]
    public async Task The_Live_Output_PC_hands_the_room_to_the_X32_once_when_NEX_Audio_goes_quiet()
    {
        var recalls = new List<(string, int)>();
        using var w = new FailsafeWatcher(0, (h, sc) =>
        {
            lock (recalls) recalls.Add((h, sc));
            return Task.FromResult(true);
        });
        using var udp = new UdpClient();
        var to = new IPEndPoint(IPAddress.Loopback, w.Port);
        // A 1.5 s timeout: a busy build machine can stall this loop for well over the usual 0.5 s
        // between two beats, which would hand over too early and fail the check below.
        for (var i = 1; i <= 10; i++)
        {
            var b = Beat(i, timeoutMs: 1500).Encode();
            await udp.SendAsync(b, b.Length, to);
            await Task.Delay(30);
        }
        lock (recalls) Assert.Empty(recalls);
        // Silence. The hand-over says the recall a few times (UDP can drop one); wait until it has finished.
        var done = new TaskCompletionSource();
        w.Fired += _ => done.TrySetResult();
        Assert.Same(done.Task, await Task.WhenAny(done.Task, Task.Delay(8000)));
        int count;
        lock (recalls)
        {
            Assert.NotEmpty(recalls);
            Assert.All(recalls, r => Assert.Equal(("127.0.0.1", 5), r));
            count = recalls.Count;
        }
        await Task.Delay(600);
        lock (recalls) Assert.Equal(count, recalls.Count); // only once per epoch
        Assert.True(w.Engines.Single().Fired);
    }

    [Fact]
    public async Task An_unarmed_desk_is_never_handed_over()
    {
        var fired = 0;
        using var w = new FailsafeWatcher(0, (_, _) => { Interlocked.Increment(ref fired); return Task.FromResult(true); });
        using var udp = new UdpClient();
        var b = Beat(1, armed: false).Encode();
        await udp.SendAsync(b, b.Length, new IPEndPoint(IPAddress.Loopback, w.Port));
        await Task.Delay(1200);
        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task No_sound_through_the_USB_card_also_hands_over()
    {
        var fired = 0;
        using var w = new FailsafeWatcher(0, (_, _) => { Interlocked.Increment(ref fired); return Task.FromResult(true); });
        using var udp = new UdpClient();
        for (var i = 1; i <= 30; i++)
        {
            var b = Beat(i, flowing: false).Encode();
            await udp.SendAsync(b, b.Length, new IPEndPoint(IPAddress.Loopback, w.Port));
            await Task.Delay(40);
        }
        Assert.True(fired > 0);
    }
}

public class LoudnessTests
{
    [Fact]
    public void A_minus_20_dBFS_tone_in_both_sides_reads_about_minus_20_LUFS()
    {
        var m = new LoudnessMeter(48000);
        var l = new float[480];
        var r = new float[480];
        var phase = 0.0;
        for (var b = 0; b < 500; b++) // 5 s
        {
            for (var n = 0; n < l.Length; n++)
            {
                l[n] = r[n] = (float)(0.1 * Math.Sin(phase));
                phase += 2 * Math.PI * 1000 / 48000;
            }
            m.Push(l, r, l.Length);
        }
        m.Update();
        Assert.InRange(m.ShortTerm, -20.7, -19.3);
        Assert.InRange(m.Integrated, -20.7, -19.3);
        Assert.InRange(m.TruePeakDb, -20.5, -19.5);
    }
}

public class PluginSandboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexora-plug-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static async Task Until(Func<bool> ok, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!ok() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(ok());
    }

    private PluginProcessor Make(string kind) =>
        new(new RackItem { Type = "plugin", Plugin = new PluginRef(kind, "", kind, "NEXORA") }, 48000, 64, _dir, new InProcessHostLauncher());

    /// <summary>Feeds blocks of a steady value, pausing a little each time like a real sound card.</summary>
    private static async Task<float> Pump(PluginProcessor p, float value, int blocks)
    {
        var l = new float[64];
        var r = new float[64];
        for (var b = 0; b < blocks; b++)
        {
            Array.Fill(l, value);
            Array.Fill(r, value);
            p.Run(l, r, 64);
            await Task.Delay(1);
        }
        return l[^1];
    }

    [Fact]
    public async Task A_plugin_in_its_own_host_changes_the_sound_two_blocks_later()
    {
        using var p = Make("builtin:gain");
        await Until(() => p.Status == "running");
        Assert.Equal(128, p.Latency);
        var outv = await Pump(p, 0.8f, 50);
        Assert.Equal(0.4f, outv, 3);
    }

    [Fact]
    public async Task The_plugins_own_delay_is_added_for_line_up()
    {
        using var p = Make("builtin:latency");
        await Until(() => p.Status == "running");
        Assert.Equal(128 + 64, p.Latency);
    }

    [Fact]
    public async Task A_crashing_plugin_lets_the_sound_through_and_is_started_again()
    {
        using var p = Make("builtin:crash");
        await Until(() => p.Status == "running");
        await Pump(p, 0.5f, 260); // the test plugin stops after 200 blocks
        await Until(() => p.Status != "running", 3000);
        var dry = await Pump(p, 0.5f, 10);
        Assert.Equal(0.5f, dry, 3); // straight through while the host is down
        p.Service();
        await Until(() => p.Status == "running");
    }
}

public class DeskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexora-desk-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task The_desk_keeps_the_mix_scenes_and_a_phase_1_patch()
    {
        using (var desk = new Desk(_dir, launcher: new InProcessHostLauncher(), network: false))
        {
            var patch = Enumerable.Range(0, 32).ToArray();
            patch[0] = EngineCore.Tone;
            patch[1] = EngineCore.Off;
            desk.ImportPatch(patch);
            Assert.Equal("tone", desk.Mix.Outputs[0].Source);
            Assert.Equal("off", desk.Mix.Outputs[1].Source);
            Assert.Equal("in:3", desk.Mix.Outputs[2].Source);

            Assert.True(desk.Set("ch1.fader", MixRig.J(-7.5)));
            var scene = await desk.SaveAsync(null, "Sunday 9am", "", "Franklyn", false, RecallScope.All, 0);
            Assert.True(desk.Set("ch1.fader", MixRig.J(0.0)));
            Assert.True(desk.Recall(scene.Id, false));
            Assert.Equal(-7.5, desk.Mix.Channels[0].Fader);
            Assert.Equal(scene.Id, desk.Settings.CurrentSceneId);
        }
        using (var again = new Desk(_dir, launcher: new InProcessHostLauncher(), network: false))
        {
            Assert.Equal(-7.5, again.Mix.Channels[0].Fader);
            Assert.Single(again.Status.Scenes);
        }
    }

    [Fact]
    public void A_running_engine_plays_the_mix()
    {
        using var desk = new Desk(_dir, launcher: new InProcessHostLauncher(), network: false);
        desk.Set("out.1.source", MixRig.J("tone"));
        var core = new EngineCore(32, 32, 64, 48000) { Mix = desk.Attach(32, 32, 64, 48000) };
        var peak = 0f;
        for (var b = 0; b < 50; b++)
        {
            core.Process(64);
            peak = Math.Max(peak, core.Out[0].Max(Math.Abs));
        }
        Assert.InRange(peak, 0.12f, 0.13f);
    }
}

public class StreamLinkTests
{
    [Fact]
    public async Task The_stream_mix_crosses_the_network_to_the_Live_Output_PC()
    {
        using var relay = new StreamRelay(0, 40);
        var tap = new StreamTap(48000);
        using var sender = new StreamSender(() => tap) { Host = "127.0.0.1", Port = relay.UdpPort };
        var l = new float[480];
        var r = new float[480];
        Array.Fill(l, 0.25f);
        Array.Fill(r, -0.25f);
        for (var i = 0; i < 40; i++)
        {
            tap.Write(l, r, l.Length);
            await Task.Delay(10);
        }
        Assert.True(relay.PacketsReceived > 10);
        Assert.True(relay.NexAudioActive);
    }
}
