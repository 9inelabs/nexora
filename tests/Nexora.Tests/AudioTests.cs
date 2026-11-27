using System.Net;
using System.Net.Sockets;
using Nexora.Audio.Asio;
using Nexora.Audio.Checks;
using Nexora.Audio.Engine;
using Nexora.Audio.Ipc;
using Nexora.Audio.X32;

namespace Nexora.Tests;

public class SampleConvertTests
{
    [Theory]
    [InlineData(SampleFormat.Int16, 1e-4)]
    [InlineData(SampleFormat.Int24, 1e-6)]
    [InlineData(SampleFormat.Int32, 1e-7)]
    [InlineData(SampleFormat.Int32In24, 1e-6)]
    [InlineData(SampleFormat.Float32, 0)]
    [InlineData(SampleFormat.Float64, 0)]
    public unsafe void Round_trip_keeps_the_signal(SampleFormat f, double tolerance)
    {
        var src = new float[] { 0f, 0.5f, -0.5f, 0.999f, -1f, 0.123456f, -0.000031f, 0.25f };
        var bytes = new byte[src.Length * 8];
        var back = new float[src.Length];
        fixed (byte* p = bytes)
        {
            SampleConvert.FromFloat(f, src, p, src.Length);
            SampleConvert.ToFloat(f, p, back, src.Length);
        }
        for (var i = 0; i < src.Length; i++) Assert.InRange(back[i] - src[i], -tolerance - 1e-4, tolerance + 1e-4);
    }

    [Fact]
    public unsafe void Over_full_scale_is_clipped_not_wrapped()
    {
        var src = new[] { 1.7f, -3f, float.NaN };
        var raw = new int[3];
        fixed (int* p = raw) SampleConvert.FromFloat(SampleFormat.Int32, src, p, 3);
        Assert.Equal(int.MaxValue, raw[0]);
        Assert.True(raw[1] <= -int.MaxValue);
        Assert.Equal(0, raw[2]);
    }

    [Fact]
    public unsafe void Int32_full_scale_reads_as_one()
    {
        var raw = new[] { int.MaxValue, int.MinValue, 0 };
        var f = new float[3];
        fixed (int* p = raw) SampleConvert.ToFloat(SampleFormat.Int32, p, f, 3);
        Assert.Equal(1f, f[0], 5);
        Assert.Equal(-1f, f[1], 5);
        Assert.Equal(0f, f[2]);
    }

    [Fact]
    public void Asio_buffer_sizes_offered_are_sensible()
    {
        Assert.Equal([32, 64, 128, 256, 512, 1024], AsioDevice.AllowedSizes(32, 1024, 64, -1));
        Assert.Equal([64], AsioDevice.AllowedSizes(64, 64, 64, 0));
        Assert.Contains(128, AsioDevice.AllowedSizes(16, 2048, 128, 16));
        Assert.Contains(96, AsioDevice.AllowedSizes(16, 2048, 96, 16));
    }
}

public class EngineCoreTests
{
    private static EngineCore Core(int frames = 64)
    {
        var c = new EngineCore(32, 32, frames, 48000);
        for (var i = 0; i < 32; i++)
            for (var n = 0; n < frames; n++)
                c.In[i][n] = (i + 1) / 100f;
        return c;
    }

    [Fact]
    public void Starts_as_straight_through()
    {
        var c = Core();
        c.Process(64);
        for (var o = 0; o < 32; o++) Assert.Equal((o + 1) / 100f, c.Out[o][10]);
    }

    [Fact]
    public void Patch_sends_any_input_to_any_output_and_off_is_silent()
    {
        var c = Core();
        var patch = EngineCore.StraightThrough(32, 32);
        patch[0] = 4;              // output 1 plays input 5
        patch[1] = EngineCore.Off; // output 2 silent
        patch[2] = 99;             // nonsense becomes off
        c.SetPatch(patch);
        c.Process(64);
        Assert.Equal(0.05f, c.Out[0][3]);
        Assert.All(c.Out[1].Take(64), v => Assert.Equal(0f, v));
        Assert.All(c.Out[2].Take(64), v => Assert.Equal(0f, v));
        Assert.Equal(EngineCore.Off, c.Patch[2]);
    }

    [Fact]
    public void Test_tone_is_minus_18_dBFS_and_continuous()
    {
        var c = Core(48);
        var patch = EngineCore.StraightThrough(32, 32);
        patch[5] = EngineCore.Tone;
        c.SetPatch(patch);
        var all = new List<float>();
        for (var b = 0; b < 20; b++)
        {
            c.Process(48);
            all.AddRange(c.Out[5].Take(48));
        }
        Assert.InRange(all.Max(), 0.120f, 0.126f);
        // 1 kHz at 48 kHz: one cycle every 48 samples, so blocks of 48 repeat exactly.
        for (var n = 0; n < 48; n++) Assert.Equal(all[n], all[n + 48 * 7], 3);
    }

    [Fact]
    public void Meters_hold_the_peak_until_read_and_flag_clips()
    {
        var c = Core();
        c.In[3][7] = -0.995f;
        c.Process(64);
        var inP = new float[32];
        var outP = new float[32];
        var inC = new bool[32];
        var outC = new bool[32];
        c.TakeMeters(inP, outP, inC, outC);
        Assert.Equal(0.995f, inP[3], 4);
        Assert.Equal(0.995f, outP[3], 4);
        Assert.True(inC[3]);
        Assert.False(inC[2]);
        Assert.Equal(0.01f, inP[0], 4);

        // A new period starts after reading; clip flags stay until reset.
        c.In[3][7] = 0.04f;
        c.Process(64);
        c.TakeMeters(inP, outP, inC, outC);
        Assert.Equal(0.04f, inP[3], 4);
        Assert.True(inC[3]);
        c.ResetClips();
        c.TakeMeters(inP, outP, inC, outC);
        Assert.False(inC[3]);
    }

    [Theory]
    [InlineData(64, 211)]
    [InlineData(128, 339)]
    [InlineData(32, 50)]
    public void Latency_test_measures_a_known_loop_exactly(int frames, int delay)
    {
        var c = new EngineCore(32, 32, frames, 48000);
        var ring = new float[16384];
        long t = 0;
        c.StartLatencyTest(15, 31);
        for (var b = 0; b < 48000 * 6 / frames && c.LatencyTestRunning; b++)
        {
            for (var n = 0; n < frames; n++)
            {
                var back = t + n - delay;
                c.In[31][n] = back >= 0 ? ring[back % ring.Length] : 0f;
            }
            c.Process(frames);
            for (var n = 0; n < frames; n++) ring[(t + n) % ring.Length] = c.Out[15][n];
            t += frames;
        }
        var r = c.LatencyResults;
        Assert.NotNull(r);
        Assert.Equal(EngineCore.LatencyRounds, r!.Length);
        Assert.All(r, v => Assert.Equal(delay, v));
        Assert.Equal(delay, EngineCore.Median(r));
    }

    [Fact]
    public void Latency_test_reports_when_nothing_comes_back()
    {
        var c = new EngineCore(32, 32, 256, 48000);
        c.StartLatencyTest(0, 0);
        for (var b = 0; b < 48000 * 12 / 256 && c.LatencyTestRunning; b++)
        {
            Array.Clear(c.In[0]);
            c.Process(256);
        }
        var r = c.LatencyResults!;
        Assert.All(r, v => Assert.Equal(-1, v));
        Assert.Null(EngineCore.Median(r));
    }

    [Fact]
    public void Processing_32_channels_allocates_nothing()
    {
        var c = Core();
        var patch = EngineCore.StraightThrough(32, 32);
        patch[7] = EngineCore.Tone;
        c.SetPatch(patch);
        c.Process(64);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 2000; i++) c.Process(64);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}

public class AudioEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexora-audio-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (Exception) { }
    }

    private static async Task Until(Func<bool> ok, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!ok() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(ok());
    }

    [Fact]
    public async Task Runs_the_test_device_and_remembers_it_after_a_restart()
    {
        using (var e = new AudioEngine(_dir))
        {
            Assert.Contains(SimulatedDevice.DeviceName, e.Status.Drivers);
            await e.OpenAsync(SimulatedDevice.DeviceName, 64);
            await Until(() => e.Status.State == EngineState.Running);
            var s = e.Status;
            Assert.Equal(32, s.Inputs);
            Assert.Equal(32, s.Outputs);
            Assert.Equal(64, s.BufferSize);
            Assert.Equal(48000, s.SampleRate);
            var patch = EngineCore.StraightThrough(32, 32);
            patch[0] = EngineCore.Tone;
            e.SetPatch(patch);
        }
        // A new engine (as after a crash or a PC restart) starts the sound again by itself, with the same patch.
        using (var e = new AudioEngine(_dir))
        {
            await Until(() => e.Status.State == EngineState.Running);
            Assert.Equal(EngineCore.Tone, e.Status.Patch[0]);
            await e.CloseAsync();
            Assert.Equal(EngineState.Stopped, e.Status.State);
        }
        using (var e = new AudioEngine(_dir))
        {
            await Task.Delay(300);
            Assert.Equal(EngineState.Stopped, e.Status.State); // stopped on purpose stays stopped
        }
    }

    [Fact]
    public async Task Latency_test_on_the_test_device_finds_its_loop()
    {
        using var e = new AudioEngine(_dir);
        await e.OpenAsync(SimulatedDevice.DeviceName, 64);
        await Until(() => e.Status.State == EngineState.Running);
        e.StartLatencyTest(31, 31);
        await Until(() => e.Status.Latency is not null, 15000);
        var r = e.Status.Latency!;
        Assert.Equal(SimulatedDevice.LoopDelayFor(64), r.MedianSamples);
        Assert.Equal(EngineCore.LatencyRounds, r.Heard);
        Assert.InRange(r.Milliseconds!.Value, 4.3, 4.5);
    }

    [Fact]
    public async Task A_missing_driver_is_reported_in_plain_words()
    {
        using var e = new AudioEngine(_dir);
        await e.OpenAsync("No Such Driver", 64);
        Assert.Equal(EngineState.Problem, e.Status.State);
        Assert.Contains("not on this PC", e.Status.Message);
    }

    [Fact]
    public async Task Screen_and_engine_talk_over_the_local_link()
    {
        var pipe = EngineNames.For(_dir);
        using var engine = new AudioEngine(_dir);
        using var server = new EngineServer(engine, pipe);
        using var client = new EngineClient(pipe);
        EngineStatus? last = null;
        MetersMsg? meters = null;
        client.Status += s => last = s;
        client.Meters += m => meters = m;
        await Until(() => client.Connected && last is not null);

        Assert.True(await client.SendAsync(new OpenDriverMsg(SimulatedDevice.DeviceName, 128)));
        await Until(() => last?.State == EngineState.Running);
        Assert.Equal(128, last!.BufferSize);
        await Until(() => meters is { In.Length: 32 } && meters.In[0] > 0);
        Assert.Equal(32, meters!.Out.Length);

        var patch = EngineCore.StraightThrough(32, 32);
        patch[3] = EngineCore.Off;
        await client.SendAsync(new SetPatchMsg(patch));
        await Until(() => last!.Patch.Count == 32 && last.Patch[3] == EngineCore.Off);

        var quit = false;
        server.QuitRequested += () => quit = true;
        await client.SendAsync(new QuitMsg());
        await Until(() => quit && last!.State == EngineState.Stopped);
    }

    [Fact]
    public void Messages_survive_the_trip_as_json()
    {
        var m = new MetersMsg([0.5f, 0f], [1f], [true, false], [false]);
        var back = Assert.IsType<MetersMsg>(AudioMessage.FromLine(m.ToLine()));
        Assert.Equal(m.In, back.In);
        Assert.Equal(m.InClip, back.InClip);
        var s = new StatusMsg(EngineStatus.Empty with { State = EngineState.Problem, Message = "x", Latency = new LatencyResult(1, 2, [10, -1, 12], 12, 48000, DateTime.UtcNow) });
        var sb = Assert.IsType<StatusMsg>(AudioMessage.FromLine(s.ToLine()));
        Assert.Equal(EngineState.Problem, sb.Status.State);
        Assert.Equal(2, sb.Status.Latency!.Heard);
        Assert.Null(AudioMessage.FromLine("not json"));
    }
}

public class X32Tests
{
    [Fact]
    public void Osc_encodes_like_the_x32_expects()
    {
        var bytes = new OscMessage("/ch/01/config/name", "Kick").Encode();
        // "/ch/01/config/name" is 18 bytes, padded to 20; ",s" padded to 4; "Kick" padded to 8.
        Assert.Equal(32, bytes.Length);
        Assert.Equal((byte)',', bytes[20]);
        Assert.Equal((byte)'s', bytes[21]);
        var back = OscMessage.Decode(bytes)!;
        Assert.Equal("/ch/01/config/name", back.Address);
        Assert.Equal("Kick", back.String(0));

        var mixed = OscMessage.Decode(new OscMessage("/x", 5, 0.75f, "ab", new byte[] { 1, 2, 3 }).Encode())!;
        Assert.Equal(5, mixed.Int(0));
        Assert.Equal(0.75f, mixed.Float(1));
        Assert.Equal("ab", mixed.String(2));
        Assert.Equal(new byte[] { 1, 2, 3 }, mixed.Args[3]);

        Assert.Equal(4, new OscMessage("/xinfo").Encode().Length % 4 == 0 ? 4 : 0);
        Assert.Null(OscMessage.Decode(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public async Task Connects_to_a_console_and_reads_its_channel_names()
    {
        using var fake = new FakeX32();
        fake.Names[1] = "Kick";
        fake.Names[2] = "Snare";
        fake.Colours[1] = 1;
        using var x = new X32Client(fake.Port);
        var info = await x.ConnectAsync("127.0.0.1");
        Assert.NotNull(info);
        Assert.Equal("X32", info!.Model);
        Assert.Equal("Church X32", info.Name);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (x.Channels[1].Name != "Snare" && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Equal("Kick", x.Channels[0].Label);
        Assert.Equal("Snare", x.Channels[1].Label);
        Assert.Equal("Ch 3", x.Channels[2].Label);
        Assert.Equal("#E0574F", x.Channels[0].ColourHex);
        Assert.True(x.Connected);

        // A rename on the desk arrives while connected.
        fake.Push(new OscMessage("/ch/03/config/name", "Bass"));
        until = DateTime.UtcNow.AddSeconds(3);
        while (x.Channels[2].Name != "Bass" && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Equal("Bass", x.Channels[2].Label);
    }

    [Fact]
    public async Task No_console_answering_means_not_connected()
    {
        using var x = new X32Client(FakeX32.FreePort());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await x.ConnectAsync("127.0.0.1"));
        Assert.False(x.Connected);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6));
    }

    /// <summary>Answers like an X32 would, on a local port.</summary>
    private sealed class FakeX32 : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private IPEndPoint? _client;

        public FakeX32()
        {
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _ = Task.Run(Loop);
        }

        public int Port { get; }
        public Dictionary<int, string> Names { get; } = [];
        public Dictionary<int, int> Colours { get; } = [];

        public static int FreePort()
        {
            using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
        }

        public void Push(OscMessage m)
        {
            if (_client is { } c) _udp.Send(m.Encode(), c);
        }

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(_stop.Token); }
                catch (Exception) { return; }
                _client = r.RemoteEndPoint;
                var m = OscMessage.Decode(r.Buffer);
                if (m is null) continue;
                OscMessage? reply = null;
                if (m.Address == "/xinfo") reply = new OscMessage("/xinfo", "127.0.0.1", "Church X32", "X32", "4.06");
                else if (m.Address.EndsWith("/config/name"))
                {
                    var ch = int.Parse(m.Address.Split('/')[2]);
                    reply = new OscMessage(m.Address, Names.GetValueOrDefault(ch, ""));
                }
                else if (m.Address.EndsWith("/config/color"))
                {
                    var ch = int.Parse(m.Address.Split('/')[2]);
                    reply = new OscMessage(m.Address, Colours.GetValueOrDefault(ch, 0));
                }
                if (reply is not null) await _udp.SendAsync(reply.Encode(), r.RemoteEndPoint);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
        }
    }
}

public class PcCheckTests
{
    [Fact]
    public void Gives_a_plain_answer_for_every_item()
    {
        var items = PcCheck.Run(isLiveOutputPc: true, Path.GetTempPath());
        Assert.Contains(items, i => i.Title == "Processor");
        Assert.Contains(items, i => i.Title == "Memory");
        Assert.Contains(items, i => i.Title == "X32 USB driver");
        Assert.Contains(items, i => i.Title == "Other jobs on this PC" && i.Level == CheckLevel.Warn);
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Found + i.Advice)));
    }
}
