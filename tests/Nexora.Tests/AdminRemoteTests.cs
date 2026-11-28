using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nexora.Audio.Engine;
using Nexora.Audio.Ipc;
using Nexora.Audio.Mix;
using Nexora.Audio.Remote;

namespace Nexora.Tests;

/// <summary>The admin remote: NEX-Audio on a laptop runs the sound PC's whole desk over the church network.</summary>
public class AdminRemoteTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "nx-admin-" + Guid.NewGuid().ToString("n")[..8]);

    private static async Task Until(Func<bool> ok, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!ok() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(ok());
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>A sound PC: the desk, its engine and the link, with the admin remote on (or not).</summary>
    private sealed class SoundPc : IDisposable
    {
        public string Dir { get; } = TempDir();
        public Desk Desk { get; }
        public AudioEngine Engine { get; }
        public EngineServer Server { get; }
        public int Port { get; } = FreePort();

        public SoundPc(string pin = "2468", bool on = true)
        {
            Desk = new Desk(Dir, network: false);
            Engine = new AudioEngine(Dir, desk: Desk);
            Server = new EngineServer(Engine, EngineNames.For(Dir));
            Laptops(on, pin);
        }

        public void Laptops(bool on, string pin, int? port = null) =>
            Desk.ConfigureSurfaces(false, null, null, Desk.Settings.Remote with { Laptops = on, LaptopPin = pin, LaptopPort = port ?? Port });

        public EngineClient Laptop(string pin) =>
            new(EngineTarget.Remote("127.0.0.1", Port, pin, "FOH-LAPTOP", "Franklyn", "0.1.60"));

        public void Dispose()
        {
            Server.Dispose();
            Engine.Dispose();
            Desk.Dispose();
        }
    }

    [Fact]
    public void The_pin_never_travels_only_a_signed_challenge()
    {
        var challenge = LaptopLink.NewChallenge();
        var proof = LaptopLink.Proof("2468", challenge);
        Assert.DoesNotContain("2468", proof);
        Assert.True(LaptopLink.Check("2468", challenge, proof));
        Assert.True(LaptopLink.Check("2468", challenge, proof.ToLowerInvariant()));
        Assert.False(LaptopLink.Check("2469", challenge, proof));
        // A proof for one challenge is no good for the next.
        Assert.False(LaptopLink.Check("2468", LaptopLink.NewChallenge(), proof));
        Assert.False(LaptopLink.Check("", challenge, LaptopLink.Proof("", challenge)));
        Assert.False(LaptopLink.Check("2468", challenge, null));
    }

    [Fact]
    public void Settings_keep_the_laptop_pin_to_digits_and_its_port_apart_from_the_phones()
    {
        var r = new RemoteSettings { Laptops = true, LaptopPin = "12a34", LaptopPort = 80 }.Clean();
        Assert.Equal("1234", r.LaptopPin);
        Assert.Equal(LaptopLink.DefaultPort, r.LaptopPort);
        Assert.Equal("", new RemoteSettings { LaptopPin = "12" }.Clean().LaptopPin);
        var clash = new RemoteSettings { Port = 7411, LaptopPort = 7411 }.Clean();
        Assert.NotEqual(clash.Port, clash.LaptopPort);
        // The musicians' remote is untouched by any of this.
        var phones = new RemoteSettings { On = true, Pin = "9999", Mixes = ["mix1"], Laptops = true, LaptopPin = "2468" }.Clean();
        Assert.Equal("9999", phones.Pin);
        Assert.Equal(["mix1"], phones.Mixes);
    }

    [Fact]
    public void Names_sent_by_a_laptop_cannot_climb_out_of_their_folder()
    {
        Assert.Equal("passwd", LaptopLink.SafeName("../../etc/passwd", "x"));
        Assert.Equal("Kick.wav", LaptopLink.SafeName(@"C:\Samples\Kick.wav", "x"));
        Assert.Equal("x", LaptopLink.SafeName("..", "x"));
        Assert.Equal("x", LaptopLink.SafeName(null, "x"));
    }

    [Fact]
    public async Task A_laptop_with_the_pin_gets_the_whole_desk_and_both_screens_stay_in_step()
    {
        using var pc = new SoundPc();
        using var screen = new EngineClient(EngineNames.For(pc.Dir));
        using var laptop = pc.Laptop("2468");
        MixState? laptopMix = null;
        DeskStatus? laptopDesk = null;
        var screenSaw = new List<string>();
        var laptopSaw = new List<string>();
        laptop.MixState += m => laptopMix = m.Mix;
        laptop.Desk += d => laptopDesk = d;
        laptop.MixChanged += c => { lock (laptopSaw) laptopSaw.Add(c.Path); };
        screen.MixChanged += c => { lock (screenSaw) screenSaw.Add(c.Path); };

        await Until(() => laptop.Connected && screen.Connected && laptopMix is not null && laptopDesk is not null);
        Assert.Null(laptop.Problem);
        Assert.Equal(Environment.MachineName, laptop.RemoteName);
        Assert.Equal(pc.Desk.Mix.Channels.Count, laptopMix!.Channels.Count);
        // The sound PC shows who is on it.
        await Until(() => pc.Desk.Status.Laptops.Count == 1);
        var who = pc.Desk.Status.Laptops[0];
        Assert.Equal("FOH-LAPTOP", who.Name);
        Assert.Equal("Franklyn", who.User);
        Assert.True(pc.Desk.Status.LaptopsRunning);

        // A fader on the laptop: the desk moves, and the sound PC's own screen sees it.
        Assert.True(await laptop.SendAsync(new SetMixMsg("ch3.fader", JsonSerializer.SerializeToElement(-12.0))));
        await Until(() => Math.Abs(pc.Desk.Mix.Find("ch3")!.Fader - -12) < 0.01);
        await Until(() => { lock (screenSaw) return screenSaw.Contains("ch3.fader"); });
        // And the other way round.
        Assert.True(await screen.SendAsync(new SetMixMsg("ch5.mute", JsonSerializer.SerializeToElement(true))));
        await Until(() => { lock (laptopSaw) return laptopSaw.Contains("ch5.mute"); });

        // Desk commands too, not just levels: a new mix bus made on the laptop.
        var buses = pc.Desk.Mix.Buses.Count;
        await laptop.SendAsync(new DeskCommandMsg("bus.add", new() { ["kind"] = "Mix", ["name"] = "Drum wedge" }));
        await Until(() => pc.Desk.Mix.Buses.Count == buses + 1);
        await Until(() => laptopMix!.Buses.Any(b => b.Name == "Drum wedge"));
    }

    [Fact]
    public async Task A_wrong_pin_is_refused_and_the_laptop_waits_instead_of_guessing()
    {
        using var pc = new SoundPc();
        using var laptop = pc.Laptop("1111");
        var mixes = 0;
        laptop.MixState += _ => mixes++;
        await Until(() => laptop.Problem is not null && laptop.Problem.Contains("not right"));
        Assert.False(laptop.Connected);
        await Task.Delay(700);
        Assert.Equal(0, mixes);
        Assert.Empty(pc.Desk.Status.Laptops);
        Assert.Contains(pc.Desk.Events.Latest(), e => e.Text.Contains("wrong PIN"));

        // Put right on the laptop: in.
        laptop.Retarget(EngineTarget.Remote("127.0.0.1", pc.Port, "2468", "FOH-LAPTOP", "", ""));
        await Until(() => laptop.Connected && mixes > 0);
    }

    [Fact]
    public async Task Five_wrong_pins_lock_that_laptop_out_for_a_minute()
    {
        using var pc = new SoundPc();
        for (var i = 0; i < 5; i++)
        {
            using var guess = pc.Laptop("000" + i);
            await Until(() => guess.Problem is not null);
        }
        using var right = pc.Laptop("2468");
        await Until(() => right.Problem is not null);
        Assert.Contains("Too many", right.Problem);
        Assert.False(right.Connected);
    }

    [Fact]
    public async Task Nothing_reaches_a_laptop_while_the_admin_remote_is_off()
    {
        using var pc = new SoundPc(on: false);
        using var laptop = pc.Laptop("2468");
        await Until(() => laptop.Problem is not null);
        Assert.Contains("Cannot reach", laptop.Problem);
        Assert.False(pc.Desk.Status.LaptopsRunning);

        // No PIN, no listening, even when switched on.
        pc.Laptops(true, "");
        await Task.Delay(200);
        Assert.False(pc.Server.LaptopsListening);
        pc.Laptops(true, "2468");
        await Until(() => pc.Server.LaptopsListening);
        laptop.Retarget(laptop.Target);
        await Until(() => laptop.Connected);
    }

    [Fact]
    public async Task A_new_pin_or_switching_off_sends_every_laptop_away()
    {
        using var pc = new SoundPc();
        using var laptop = pc.Laptop("2468");
        await Until(() => laptop.Connected);
        await Until(() => pc.Desk.Status.Laptops.Count == 1);
        pc.Laptops(true, "1357");
        await Until(() => !laptop.Connected && laptop.Problem is not null);
        await Until(() => pc.Desk.Status.Laptops.Count == 0);
        // It does not get back in with the old PIN.
        await Task.Delay(800);
        Assert.False(laptop.Connected);

        laptop.Retarget(laptop.Target with { Pin = "1357" });
        await Until(() => laptop.Connected);
        pc.Laptops(false, "1357");
        await Until(() => !laptop.Connected);
        Assert.False(pc.Server.LaptopsListening);
    }

    [Fact]
    public async Task The_sound_pc_can_send_one_laptop_away()
    {
        using var pc = new SoundPc();
        using var screen = new EngineClient(EngineNames.For(pc.Dir));
        using var laptop = pc.Laptop("2468");
        await Until(() => laptop.Connected && screen.Connected);
        await Until(() => pc.Desk.Status.Laptops.Count == 1);
        await screen.SendAsync(new DeskCommandMsg("laptop.kick", new() { ["id"] = pc.Desk.Status.Laptops[0].Id.ToString() }));
        await Until(() => !laptop.Connected && laptop.Problem?.Contains("closed") == true);
        // It stays away until someone presses Connect on the laptop.
        await Task.Delay(800);
        Assert.False(laptop.Connected);
        Assert.True(screen.Connected);
    }

    [Fact]
    public async Task A_laptop_cannot_shut_the_engine_down()
    {
        using var pc = new SoundPc();
        using var laptop = pc.Laptop("2468");
        var quit = false;
        pc.Server.QuitRequested += () => quit = true;
        NoticeMsg? notice = null;
        laptop.Notice += n => notice = n;
        await Until(() => laptop.Connected);
        await laptop.SendAsync(new QuitMsg());
        await Until(() => notice is not null);
        Assert.Contains("only be shut down at the sound PC", notice!.Text);
        Assert.False(quit);
    }

    [Fact]
    public async Task Shows_and_samples_travel_over_the_link_but_nothing_else_can_be_read()
    {
        using var pc = new SoundPc();
        using var laptop = pc.Laptop("2468");
        var files = new List<FileMsg>();
        laptop.File += f => { lock (files) files.Add(f); };
        await Until(() => laptop.Connected);

        var bytes = Encoding.UTF8.GetBytes("RIFF pretend sample");
        await laptop.SendAsync(new DeskCommandMsg("file.put", new() { ["id"] = "a", ["name"] = "../Kick.wav", ["kind"] = "drum" },
            JsonSerializer.SerializeToElement(Convert.ToBase64String(bytes))));
        await Until(() => { lock (files) return files.Any(f => f.Id == "a"); });
        var put = files.Single(f => f.Id == "a");
        Assert.Null(put.Error);
        Assert.StartsWith(Path.Combine(pc.Desk.Dir, "remote", "drums"), put.Path);
        Assert.EndsWith("Kick.wav", put.Path);
        Assert.Equal(bytes, File.ReadAllBytes(put.Path));

        await laptop.SendAsync(new DeskCommandMsg("file.get", new() { ["id"] = "b", ["path"] = put.Path }));
        await Until(() => { lock (files) return files.Any(f => f.Id == "b"); });
        Assert.Equal(bytes, Convert.FromBase64String(files.Single(f => f.Id == "b").Data!));

        // The desk's own files (and anything else on the PC) stay where they are.
        await laptop.SendAsync(new DeskCommandMsg("file.get", new() { ["id"] = "c", ["path"] = Path.Combine(pc.Desk.Dir, "settings.json") }));
        await Until(() => { lock (files) return files.Any(f => f.Id == "c"); });
        var refused = files.Single(f => f.Id == "c");
        Assert.Null(refused.Data);
        Assert.NotNull(refused.Error);

        // A show saved for the laptop: kept on the sound PC in the remote folder, and its contents sent back.
        await laptop.SendAsync(new DeskCommandMsg("file.show", new() { ["id"] = "d", ["name"] = "Sunday", ["by"] = "Franklyn" }));
        await Until(() => { lock (files) return files.Any(f => f.Id == "d"); });
        var saved = files.Single(f => f.Id == "d");
        Assert.Null(saved.Error);
        Assert.Equal(EngineServer.RemoteShowPath(pc.Desk.Dir, "Sunday"), saved.Path);
        Assert.Equal(File.ReadAllBytes(saved.Path), Convert.FromBase64String(saved.Data!));
        // And a show sent from the laptop can be looked into and opened on the sound PC.
        ShowInspectMsg? info = null;
        laptop.ShowInfo += i => info = i;
        await laptop.SendAsync(new DeskCommandMsg("file.put", new() { ["id"] = "e", ["name"] = "Sunday.nexshow", ["kind"] = "show" },
            JsonSerializer.SerializeToElement(saved.Data)));
        await Until(() => { lock (files) return files.Any(f => f.Id == "e"); });
        await laptop.SendAsync(new DeskCommandMsg("show.inspect", new() { ["file"] = files.Single(f => f.Id == "e").Path }));
        await Until(() => info is not null);
        Assert.NotNull(info!.Contents);
    }

    [Fact]
    public async Task A_laptop_finds_the_sound_pc_on_the_network_by_itself()
    {
        using var pc = new SoundPc();
        await Until(() => pc.Server.LaptopsListening);
        var found = await LaptopLink.FindAsync(pc.Port, 1000);
        var me = Assert.Single(found, f => f.Port == pc.Port);
        Assert.Equal(Environment.MachineName, me.Name);
    }

    [Fact]
    public void The_new_messages_survive_the_trip_as_json()
    {
        var hello = Assert.IsType<LaptopHelloMsg>(AudioMessage.FromLine(new LaptopHelloMsg("abc", "SOUND-PC", "0.1.60").ToLine()));
        Assert.Equal("SOUND-PC", hello.PcName);
        var no = Assert.IsType<LaptopResultMsg>(AudioMessage.FromLine(new LaptopResultMsg(false, "nope").ToLine()));
        Assert.False(no.Ok);
        var f = Assert.IsType<FileMsg>(AudioMessage.FromLine(new FileMsg("1", "x", "AAAA").ToLine()));
        Assert.Equal("AAAA", f.Data);
    }
}
