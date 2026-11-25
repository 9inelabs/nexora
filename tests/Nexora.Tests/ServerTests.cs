using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Nexora.Core.Model;
using Nexora.Core.Net;
using Nexora.Core.Protocol;
using Nexora.Core.Security;
using Nexora.Server;

namespace Nexora.Tests;

public sealed class ServerFixture : IAsyncDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "nexora-test-" + Guid.NewGuid().ToString("N")[..8]);
    public LibraryStore Store { get; }
    public LiveServer Server { get; private set; }
    public int Port { get; } = FreePort();
    public const string Code = "123456";
    public UserInfo Admin { get; } = new("u-admin", "Admin", "AD", Role.Admin, "Studio manager");
    public UserInfo Daniel { get; } = new("u-daniel", "Daniel", "DA", Role.Scripture | Role.Lyrics, "");
    public UserInfo Trainee { get; } = new("u-trainee", "Tom", "TO", Role.Trainee | Role.Scripture, "");

    public ServerFixture()
    {
        Directory.CreateDirectory(Dir);
        Store = new LibraryStore(Path.Combine(Dir, "lib.db"));
        Store.SaveUser(Admin, "1111");
        Store.SaveUser(Daniel, "2222");
        Store.SaveUser(Trainee, "3333");
        Server = NewServer();
    }

    public LiveServer NewServer() => new(Store, new LiveServerOptions
    {
        Port = Port, Announce = false, PairingCode = () => Code, ServerName = () => "TEST-LIVE", Version = "test",
        LogsDir = Path.Combine(Dir, "logs"),
    });

    public async Task RestartAsync()
    {
        await Server.DisposeAsync();
        Server = NewServer();
        await Server.StartAsync();
    }

    public NexoraClient Client(string pcId, ClientKind kind = ClientKind.Terminal, string code = Code)
    {
        var c = new NexoraClient(() => new Uri($"ws://127.0.0.1:{Port}{Wire.Path}"),
            () => new Hello(code, pcId, pcId.ToUpperInvariant(), kind, "test"));
        c.Start();
        return c;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        Store.Dispose();
        try { Directory.Delete(Dir, true); } catch { }
    }
}

public class ServerTests
{
    static ServerTests()
    {
        NexoraClient.RetryDelay = TimeSpan.FromMilliseconds(300);
    }

    private static async Task WaitFor(Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("Condition not met in time");
            await Task.Delay(10);
        }
    }

    private static async Task<NexoraClient> SignedIn(ServerFixture f, string pc, UserInfo user, string pin, params SectionKind[] sections)
    {
        var c = f.Client(pc);
        await WaitFor(() => c.Status == ConnectionStatus.Connected);
        var r = await c.LoginAsync(user.Id, pin);
        Assert.True(r.Ok, r.Error);
        if (sections.Length > 0)
        {
            await c.OpenSectionsAsync(sections);
            await WaitFor(() => c.Snapshot.Sources.Count(s => s.PcId == pc && s.Online) == sections.Length);
        }
        return c;
    }

    [Fact]
    public async Task Wrong_pairing_code_is_refused()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var c = f.Client("pc1", code: "999999");
        await WaitFor(() => c.Status == ConnectionStatus.Rejected);
        Assert.Contains("pairing", c.RejectReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sign_in_shows_names_and_checks_the_pin()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var c = f.Client("pc1");
        await WaitFor(() => c.PublicInfo is not null);
        Assert.Contains(c.PublicInfo!.Users, u => u.Name == "Daniel");

        var bad = await c.LoginAsync(f.Daniel.Id, "0000");
        Assert.False(bad.Ok);
        var good = await c.LoginAsync(f.Daniel.Id, "2222");
        Assert.True(good.Ok);
        Assert.Equal("Daniel", c.User?.Name);
    }

    [Fact]
    public async Task Five_wrong_pins_lock_the_user_briefly()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var c = f.Client("pc1");
        await WaitFor(() => c.Status == ConnectionStatus.Connected);
        for (var i = 0; i < 5; i++) await c.LoginAsync(f.Daniel.Id, "0000");
        var r = await c.LoginAsync(f.Daniel.Id, "2222");
        Assert.False(r.Ok);
        Assert.Contains("Too many", r.Error);
    }

    [Fact]
    public async Task Pinned_terminal_slide_reaches_the_output_engine_in_under_a_tenth_of_a_second()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var output = f.Client("live", ClientKind.OutputEngine);
        await using var term = await SignedIn(f, "pc1", f.Daniel, "2222", SectionKind.Scripture);
        await using var op = await SignedIn(f, "live-op", f.Admin, "1111");

        var key = Sections.SourceKey("pc1", SectionKind.Scripture);
        await op.RouteAsync(key, "main", RouteAction.Pin);
        await WaitFor(() => output.Snapshot.Sources.Any(s => s.Key == key && s.RouteOn("main") == RouteState.Live));

        // Warm up, then measure several sends.
        await term.SendContentAsync(SectionKind.Scripture, new SlideContent("warm up"));
        await WaitFor(() => Text(output, "main") == "warm up");

        var worst = 0L;
        for (var i = 0; i < 10; i++)
        {
            var text = $"John 3:{16 + i}";
            var sw = Stopwatch.StartNew();
            await term.SendContentAsync(SectionKind.Scripture, new SlideContent(text));
            await WaitFor(() => Text(output, "main") == text);
            worst = Math.Max(worst, sw.ElapsedMilliseconds);
        }
        Assert.True(worst < 100, $"Slowest slide took {worst} ms");
    }

    [Fact]
    public async Task Both_screens_show_different_content()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var output = f.Client("live", ClientKind.OutputEngine);
        await using var t1 = await SignedIn(f, "pc1", f.Daniel, "2222", SectionKind.Scripture);
        await using var t2 = await SignedIn(f, "pc2", f.Daniel, "2222", SectionKind.Lyrics);
        await using var op = await SignedIn(f, "live-op", f.Admin, "1111");

        await op.RouteAsync(Sections.SourceKey("pc1", SectionKind.Scripture), "main", RouteAction.Pin);
        await op.RouteAsync(Sections.SourceKey("pc2", SectionKind.Lyrics), "stage", RouteAction.Pin);
        await t1.SendContentAsync(SectionKind.Scripture, new SlideContent("Verse"));
        await t2.SendContentAsync(SectionKind.Lyrics, new SlideContent("Lyric"));
        await WaitFor(() => Text(output, "main") == "Verse" && Text(output, "stage") == "Lyric");
    }

    [Fact]
    public async Task Only_live_output_operators_can_route()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var t1 = await SignedIn(f, "pc1", f.Daniel, "2222", SectionKind.Scripture);
        await t1.RouteAsync(Sections.SourceKey("pc1", SectionKind.Scripture), "main", RouteAction.Pin);
        await Task.Delay(200);
        Assert.All(f.Server.Snapshot().Sources, s => Assert.Equal(RouteState.NotRouted, s.RouteOn("main")));
    }

    [Fact]
    public async Task Trainee_content_never_reaches_a_screen()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var t = await SignedIn(f, "pc1", f.Trainee, "3333", SectionKind.Scripture);
        await using var op = await SignedIn(f, "live-op", f.Admin, "1111");
        await op.RouteAsync(Sections.SourceKey("pc1", SectionKind.Scripture), "main", RouteAction.Pin);
        await t.SendContentAsync(SectionKind.Scripture, new SlideContent("practice"));
        await Task.Delay(200);
        Assert.Empty(f.Server.Snapshot().FrameFor("main")!.Items);
    }

    [Fact]
    public async Task Terminal_that_drops_keeps_its_content_on_screen_and_comes_back_pinned()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var output = f.Client("live", ClientKind.OutputEngine);
        await using var op = await SignedIn(f, "live-op", f.Admin, "1111");
        var key = Sections.SourceKey("pc1", SectionKind.Scripture);

        var t1 = await SignedIn(f, "pc1", f.Daniel, "2222", SectionKind.Scripture);
        await op.RouteAsync(key, "main", RouteAction.Pin);
        await t1.SendContentAsync(SectionKind.Scripture, new SlideContent("John 3:16"));
        await WaitFor(() => Text(output, "main") == "John 3:16");

        await t1.DisposeAsync(); // terminal switched off
        await WaitFor(() => output.Snapshot.Sources.Single(s => s.Key == key).Online == false);
        Assert.Equal("John 3:16", Text(output, "main")); // audience screen never goes blank

        var sw = Stopwatch.StartNew();
        await using var t2 = await SignedIn(f, "pc1", f.Daniel, "2222", SectionKind.Scripture); // restarted terminal
        Assert.Equal(RouteState.Live, output.Snapshot.Sources.Single(s => s.Key == key).RouteOn("main"));
        await t2.SendContentAsync(SectionKind.Scripture, new SlideContent("John 3:17"));
        await WaitFor(() => Text(output, "main") == "John 3:17");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Clients_reconnect_by_themselves_and_stay_signed_in_after_a_dropped_link()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var t1 = await SignedIn(f, "pc1", f.Daniel, "2222", SectionKind.Scripture);

        t1.Reconnect(); // simulate a pulled cable on this side
        await WaitFor(() => t1.Status != ConnectionStatus.Connected, 2000);
        var sw = Stopwatch.StartNew();
        await WaitFor(() => t1.Status == ConnectionStatus.Connected && t1.User is not null);
        await WaitFor(() => f.Server.Snapshot().Sources.Single(s => s.PcId == "pc1").Online);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Took {sw.Elapsed}");
    }

    [Fact]
    public async Task Live_output_restart_restores_the_picture_and_terminals_reconnect()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var output = f.Client("live", ClientKind.OutputEngine);
        await using var op = await SignedIn(f, "live-op", f.Admin, "1111");
        await using var t1 = await SignedIn(f, "pc1", f.Daniel, "2222", SectionKind.Scripture);
        await op.RouteAsync(Sections.SourceKey("pc1", SectionKind.Scripture), "main", RouteAction.Pin);
        await t1.SendContentAsync(SectionKind.Scripture, new SlideContent("John 3:16"));
        await WaitFor(() => Text(output, "main") == "John 3:16");

        await f.RestartAsync();
        // The output engine reconnects and receives the saved picture again.
        await WaitFor(() => output.Status == ConnectionStatus.Connected && Text(output, "main") == "John 3:16");
        // Tokens do not survive a restart, so the terminal is asked to sign in again.
        await WaitFor(() => t1.Status == ConnectionStatus.Connected && t1.User is null);
    }

    [Fact]
    public async Task Admin_can_add_a_user_and_terminals_see_the_name_at_once()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var admin = await SignedIn(f, "live-op", f.Admin, "1111");
        await using var other = f.Client("pc2");
        await WaitFor(() => other.PublicInfo is not null);

        await admin.SendAsync(new SaveUser(new UserInfo("u-new", "Kwame Mensah", "", Role.Announcements, ""), "4567"));
        await WaitFor(() => other.PublicInfo!.Users.Any(u => u.Name == "Kwame Mensah" && u.Initials == "KM"));
        var r = await other.LoginAsync("u-new", "4567");
        Assert.True(r.Ok);
    }

    [Fact]
    public async Task Non_admin_cannot_change_users_or_branding()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var t = await SignedIn(f, "pc1", f.Daniel, "2222");
        await t.SendAsync(new SaveUser(new UserInfo("u-x", "Mallory", "", Role.Admin, ""), "9999"));
        await t.SendAsync(new SaveBranding(Branding.Default with { ChurchName = "Hacked" }));
        await Task.Delay(200);
        Assert.DoesNotContain(f.Store.Users(), u => u.Name == "Mallory");
        Assert.NotEqual("Hacked", f.Store.Branding.ChurchName);
    }

    [Fact]
    public async Task The_last_admin_cannot_be_removed()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var admin = await SignedIn(f, "live-op", f.Admin, "1111");
        await admin.SendAsync(new SaveUser(f.Admin with { Roles = Role.LiveOutput }, null));
        await Task.Delay(200);
        Assert.True(f.Store.User(f.Admin.Id)!.IsAdmin);
    }

    private static string? Text(NexoraClient c, string screen) =>
        c.Snapshot.FrameFor(screen)?.Items.LastOrDefault()?.Content.Text;
}

public class SmallTests
{
    [Fact]
    public void Pin_hash_round_trip()
    {
        var h = PinHasher.Hash("4821");
        Assert.True(PinHasher.Verify("4821", h));
        Assert.False(PinHasher.Verify("4822", h));
        Assert.DoesNotContain("4821", h);
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("123", false)]
    [InlineData("12345", false)]
    [InlineData("12a4", false)]
    public void Pin_rules(string pin, bool ok) => Assert.Equal(ok, PinHasher.IsValidPin(pin));

    [Fact]
    public void Messages_round_trip_over_the_wire()
    {
        var msg = new SendContent(SectionKind.Lyrics, new SlideContent("Amazing grace", "Verse 1", "Slide 1"));
        var back = Wire.Deserialize(Wire.Serialize(msg));
        Assert.Equal(msg, back);
        Assert.Null(Wire.Deserialize("{not json"));
        Assert.Null(Wire.Deserialize("{\"t\":\"unknown\"}"));
    }

    [Fact]
    public void Roles_with_several_flags_round_trip()
    {
        var u = new UserInfo("1", "Ama", "AM", Role.Scripture | Role.Countdown, "");
        var back = (PublicInfo)Wire.Deserialize(Wire.Serialize(new PublicInfo("x", Branding.Default, [u])))!;
        Assert.Equal(u.Roles, back.Users[0].Roles);
    }

    [Theory]
    [InlineData("Daniel", "DA")]
    [InlineData("Kwame Mensah", "KM")]
    [InlineData("grace harvest chapel", "GC")]
    public void Initials(string name, string expected) => Assert.Equal(expected, UserInfo.MakeInitials(name));

    [Theory]
    [InlineData("192.168.1.20", "ws://192.168.1.20:7400/nexora")]
    [InlineData("192.168.1.20:7500", "ws://192.168.1.20:7500/nexora")]
    [InlineData("live-pc", "ws://live-pc:7400/nexora")]
    public void Typed_addresses_are_understood(string typed, string expected) =>
        Assert.Equal(expected, Nexora.Core.Config.ThisPcConfig.ParseAddress(typed, 7400)!.ToString());

    [Fact]
    public void Discovery_announcement_round_trip()
    {
        var a = new Announcement(Announcement.AppTag, "abc", "LIVE-PC", "Grace Chapel", 7400, "0.1.0");
        Assert.Equal(a, Announcement.Parse(a.ToBytes()));
        Assert.Null(Announcement.Parse("hello"u8));
    }

    [Fact]
    public async Task Discovery_finds_the_live_output_pc_on_this_machine()
    {
        var port = 17401 + Random.Shared.Next(500);
        await using var listener = new DiscoveryListener(port);
        await using var announcer = new DiscoveryAnnouncer(() =>
            new Announcement(Announcement.AppTag, "srv1", "LIVE-PC", "Grace", 7400, "t"), port);
        var sw = Stopwatch.StartNew();
        while (listener.Find("srv1") is null && sw.ElapsedMilliseconds < 5000) await Task.Delay(50);
        Assert.NotNull(listener.Find("srv1"));
    }

    [Fact]
    public void Store_keeps_branding_and_screens()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nx-{Guid.NewGuid():N}.db");
        using (var s = new LibraryStore(path))
        {
            s.Branding = Branding.Default with { ChurchName = "Grace Harvest Chapel" };
            Assert.Equal(2, s.Screens.Count);
            s.Screens = [new ScreenInfo("main", "LED Wall", ScreenKind.Audience, 2, true, 0)];
        }
        using (var s = new LibraryStore(path))
        {
            Assert.Equal("Grace Harvest Chapel", s.Branding.ChurchName);
            Assert.Equal("LED Wall", Assert.Single(s.Screens).Name);
        }
    }
}
