using Nexora.Audio;
using Nexora.Audio.Dsp;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;

namespace Nexora.Tests;

/// <summary>Phase 5 of the complete desk: the correlation meter, other sample rates, offline editing and the preset library.</summary>
public class WorkflowTests
{
    private static float[] Sine(double freq, double amp, int n, double rate = 48000, double phase = 0) =>
        Enumerable.Range(0, n).Select(i => (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate + phase))).ToArray();

    private static float Correlation(float[] l, float[] r)
    {
        var m = new CorrelationMeter(48000);
        for (var b = 0; b + 64 <= l.Length; b += 64) m.Push(l[b..(b + 64)], r[b..(b + 64)], 64);
        return m.Value;
    }

    [Fact]
    public void Correlation_reads_plus_one_for_mono_minus_one_for_opposite_and_near_zero_for_unrelated()
    {
        var a = Sine(440, 0.5, 48000);
        Assert.True(Correlation(a, a) > 0.99);
        Assert.True(Correlation(a, a.Select(v => -v).ToArray()) < -0.99);
        // A quarter turn apart, or two unrelated tones: neither helps nor fights in mono.
        Assert.InRange(Correlation(a, Sine(440, 0.5, 48000, phase: Math.PI / 2)), -0.1, 0.1);
        Assert.InRange(Correlation(a, Sine(1013, 0.5, 48000)), -0.15, 0.15);
    }

    [Fact]
    public void Correlation_says_nothing_in_silence()
    {
        var quiet = new float[48000];
        Assert.True(float.IsNaN(Correlation(quiet, quiet)));
    }

    [Fact]
    public void The_stream_leaves_at_48k_whatever_rate_the_desk_runs_at()
    {
        foreach (var rate in new[] { 44100.0, 88200, 96000 })
        {
            var tap = new StreamTap(48000);
            var rs = new StreamResampler(rate, 128);
            var tone = Sine(1000, 0.5, (int)rate, rate);
            for (var b = 0; b + 128 <= tone.Length; b += 128) rs.Write(tap, tone[b..(b + 128)], tone[b..(b + 128)], 128);
            // One second in: very close to one second of 48 kHz out.
            Assert.InRange(tap.Written, 47_800, 48_100);
            var outBuf = new float[2 * 24000];
            tap.Read(12000, outBuf);
            var left = Enumerable.Range(0, 24000).Select(i => outBuf[i * 2]).ToArray();
            // Still a 1 kHz tone at the same level: count the zero crossings in half a second (about 1000).
            var crossings = Enumerable.Range(1, left.Length - 1).Count(i => left[i - 1] < 0 && left[i] >= 0);
            Assert.InRange(crossings, 495, 505);
            var rms = Math.Sqrt(left.Sum(v => (double)v * v) / left.Length);
            Assert.InRange(20 * Math.Log10(rms / (0.5 / Math.Sqrt(2))), -0.5, 0.3);
        }
    }

    [Fact]
    public void At_96k_the_stream_drops_what_48k_cannot_carry()
    {
        var tap = new StreamTap(48000);
        var rs = new StreamResampler(96000, 128);
        // 30 kHz would fold back to 18 kHz at 48 kHz if it were not filtered first.
        var tone = Sine(30000, 0.5, 96000, 96000);
        for (var b = 0; b + 128 <= tone.Length; b += 128) rs.Write(tap, tone[b..(b + 128)], tone[b..(b + 128)], 128);
        var outBuf = new float[2 * 24000];
        tap.Read(12000, outBuf);
        var rms = Math.Sqrt(Enumerable.Range(0, 24000).Sum(i => (double)outBuf[i * 2] * outBuf[i * 2]) / 24000);
        Assert.True(20 * Math.Log10(rms / (0.5 / Math.Sqrt(2))) < -20);
    }

    [Fact]
    public void At_48k_the_stream_passes_straight_through()
    {
        var rs = new StreamResampler(48000, 64);
        Assert.True(rs.Through);
        var tap = new StreamTap(48000);
        var x = Sine(500, 0.3, 64);
        rs.Write(tap, x, x, 64);
        Assert.Equal(64, tap.Written);
    }

    [Fact]
    public void The_test_device_runs_at_96k_when_asked_and_says_which_rates_it_has()
    {
        using var d = new SimulatedDevice();
        d.Open(128, 96000);
        Assert.Equal(96000, d.Format!.SampleRate);
        Assert.Contains(96000.0, d.Format.SampleRates);
        d.Close();
        using var e = new SimulatedDevice();
        e.Open(128, 12345); // a rate it cannot do: it stays at 48 kHz
        Assert.Equal(48000, e.Format!.SampleRate);
    }

    [Fact]
    public void A_mix_at_96k_feeds_the_stream_at_48k_and_meters_its_correlation()
    {
        var m = MixRig.Plain();
        var e = new MixEngine(32, 32, 128, 96000);
        e.Load(m);
        Assert.Equal(96000, e.SampleRate);
        var input = Enumerable.Range(0, 32).Select(_ => new float[128]).ToArray();
        var output = Enumerable.Range(0, 32).Select(_ => new float[128]).ToArray();
        var tone = Sine(500, 0.3, 96000, 96000);
        for (var b = 0; b + 128 <= tone.Length; b += 128)
        {
            Array.Copy(tone, b, input[0], 0, 128);
            e.Process(input, output, 128);
        }
        // One second at 96 kHz is one second of stream at 48 kHz.
        Assert.InRange(e.StreamTap.Written, 47_800, 48_100);
        // A centred mono channel is the same in left and right.
        Assert.True(e.MainPhase.Value > 0.99);
    }

    [Fact]
    public void Offline_pauses_the_failsafe_and_the_link_and_remembers_the_x32()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nx-offline-" + Guid.NewGuid().ToString("n"));
        using var desk = new Desk(dir, network: false);
        desk.Configure(s => s with { Armed = true, X32Host = "192.168.1.50" });
        desk.SetOfflineAsync(true).GetAwaiter().GetResult();
        Assert.True(desk.Settings.Offline);
        Assert.False(desk.X32.Connected);
        Assert.Equal("192.168.1.50", desk.Settings.X32Host);
        Assert.False(desk.ConnectX32Async("192.168.1.50").GetAwaiter().GetResult());
        // Changes to the mix still work offline: that is the point.
        Assert.True(desk.Set("ch1.fader", MixRig.J(-5.0)));
        Assert.Equal(-5, desk.Mix.Channels[0].Fader);
        // It survives a restart, and the failsafe setting is kept for when it goes back online.
        desk.Dispose();
        using var again = new Desk(dir, network: false);
        Assert.True(again.Settings.Offline);
        Assert.True(again.Settings.Armed);
    }

    [Fact]
    public void Preset_library_has_folders_search_and_moving()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nx-presetlib-" + Guid.NewGuid().ToString("n"));
        var store = new PresetStore(dir);
        Assert.Contains("Voices", store.Folders());
        Assert.Contains("Drums", store.Folders());
        var strip = new Strip { Kind = StripKind.Channel };
        var p = store.Save("Pastor Mike lapel", strip, "  Sunday / speech ");
        Assert.Equal("Sunday   speech", store.Load(p.Id)!.Folder);
        Assert.Contains(store.List(), x => x.Id == p.Id && x.Folder == "Sunday   speech" && !x.BuiltIn);

        var info = store.List().First(x => x.Id == p.Id);
        Assert.True(info.Matches("pastor lapel"));
        Assert.True(info.Matches("SUNDAY"));
        Assert.False(info.Matches("kick"));
        Assert.True(store.List().First(x => x.Id == "builtin-kick").Matches("drums kick"));

        Assert.True(store.Move(p.Id, "Guests", "Guest speaker"));
        var moved = store.List().First(x => x.Id == p.Id);
        Assert.Equal("Guests", moved.Folder);
        Assert.Equal("Guest speaker", moved.Name);
        Assert.False(store.Move("builtin-kick", "Mine"));
        Assert.Equal(new string('x', 30), PresetStore.CleanFolder(new string('x', 50)));
    }
}
