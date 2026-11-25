using Nexora.Core.Live;
using Nexora.Core.Model;

namespace Nexora.Tests;

public class LiveStateTests
{
    private static readonly ScreenInfo Main = new("main", "Main Audience Screen", ScreenKind.Audience, 0, true, 0);
    private static readonly ScreenInfo Stage = new("stage", "Stage Monitor", ScreenKind.Stage, 0, false, 1);

    private static (LiveState live, string scripture, string lyrics) Setup()
    {
        var live = new LiveState();
        live.SetScreens([Main, Stage]);
        var s = live.RegisterSource("pc1", "TERMINAL-01", SectionKind.Scripture, "Daniel").Key;
        var l = live.RegisterSource("pc2", "TERMINAL-02", SectionKind.Lyrics, "Esther").Key;
        return (live, s, l);
    }

    private static string? Showing(LiveState live, string screen, Layer layer) =>
        live.Frame(screen).Items.FirstOrDefault(i => i.Layer == layer)?.Content.Text;

    [Fact]
    public void Pinned_terminal_follows_every_change_live()
    {
        var (live, s, _) = Setup();
        live.Pin(s, "main");
        live.SetContent(s, new SlideContent("John 3:16"));
        Assert.Equal("John 3:16", Showing(live, "main", Layer.Scripture));
        live.SetContent(s, new SlideContent("John 3:17"));
        Assert.Equal("John 3:17", Showing(live, "main", Layer.Scripture));
    }

    [Fact]
    public void Unpinned_terminal_does_not_reach_the_screen()
    {
        var (live, s, _) = Setup();
        live.SetContent(s, new SlideContent("John 3:16"));
        Assert.Empty(live.Frame("main").Items);
        Assert.Empty(live.Frame("stage").Items);
    }

    [Fact]
    public void Take_down_removes_content_but_remembers_the_pin_and_bring_back_shows_current_item()
    {
        var (live, s, _) = Setup();
        live.Pin(s, "main");
        live.SetContent(s, new SlideContent("A"));
        live.TakeDown(s, "main");
        Assert.Empty(live.Frame("main").Items);
        Assert.Equal(RouteState.Standby, live.Source(s)!.Routes["main"]);

        live.SetContent(s, new SlideContent("B")); // operator keeps working while taken down
        Assert.Empty(live.Frame("main").Items);

        live.Pin(s, "main"); // bring back
        Assert.Equal("B", Showing(live, "main", Layer.Scripture));
    }

    [Fact]
    public void Two_screens_show_different_content()
    {
        var (live, s, l) = Setup();
        live.Pin(s, "main");
        live.Pin(l, "stage");
        live.SetContent(s, new SlideContent("John 3:16"));
        live.SetContent(l, new SlideContent("Amazing grace"));
        Assert.Equal("John 3:16", Showing(live, "main", Layer.Scripture));
        Assert.Null(Showing(live, "main", Layer.Lyrics));
        Assert.Equal("Amazing grace", Showing(live, "stage", Layer.Lyrics));
        Assert.Null(Showing(live, "stage", Layer.Scripture));
    }

    [Fact]
    public void One_text_source_rule_takes_the_other_text_terminal_down()
    {
        var (live, s, l) = Setup();
        live.Pin(l, "main");
        live.SetContent(l, new SlideContent("Amazing grace"));
        live.Pin(s, "main");
        live.SetContent(s, new SlideContent("John 3:16"));

        Assert.Equal(RouteState.Standby, live.Source(l)!.Routes["main"]);
        Assert.Null(Showing(live, "main", Layer.Lyrics));
        Assert.Equal("John 3:16", Showing(live, "main", Layer.Scripture));
    }

    [Fact]
    public void Screen_without_one_text_source_rule_shows_both()
    {
        var (live, s, l) = Setup();
        live.Pin(l, "stage");
        live.Pin(s, "stage");
        live.SetContent(l, new SlideContent("Lyric"));
        live.SetContent(s, new SlideContent("Verse"));
        Assert.Equal(RouteState.Live, live.Source(l)!.Routes["stage"]);
        Assert.Equal(2, live.Frame("stage").Items.Count);
    }

    [Fact]
    public void Disconnected_terminal_keeps_its_content_on_screen_and_its_pin()
    {
        var (live, s, _) = Setup();
        live.Pin(s, "main");
        live.SetContent(s, new SlideContent("John 3:16"));
        live.SetOffline(s);
        Assert.Equal("John 3:16", Showing(live, "main", Layer.Scripture));

        // Reconnects: same PC, same section.
        live.RegisterSource("pc1", "TERMINAL-01", SectionKind.Scripture, "Daniel");
        Assert.Equal(RouteState.Live, live.Source(s)!.Routes["main"]);
        live.SetContent(s, new SlideContent("John 3:17"));
        Assert.Equal("John 3:17", Showing(live, "main", Layer.Scripture));
    }

    [Fact]
    public void Clear_removes_only_that_terminals_content()
    {
        var (live, s, l) = Setup();
        live.Pin(s, "stage");
        live.Pin(l, "stage");
        live.SetContent(s, new SlideContent("Verse"));
        live.SetContent(l, new SlideContent("Lyric"));
        live.SetContent(s, null);
        Assert.Null(Showing(live, "stage", Layer.Scripture));
        Assert.Equal("Lyric", Showing(live, "stage", Layer.Lyrics));
    }

    [Fact]
    public void Take_puts_a_snapshot_that_does_not_follow()
    {
        var (live, s, _) = Setup();
        live.SetContent(s, new SlideContent("A"));
        live.Take(s, "main");
        live.SetContent(s, new SlideContent("B"));
        Assert.Equal("A", Showing(live, "main", Layer.Scripture));
    }

    [Fact]
    public void Master_logo_and_black_toggle()
    {
        var (live, _, _) = Setup();
        live.ToggleMaster("main", MasterMode.Black);
        Assert.Equal(MasterMode.Black, live.Frame("main").Master);
        live.ToggleMaster("main", MasterMode.Black);
        Assert.Equal(MasterMode.None, live.Frame("main").Master);
        live.ToggleMaster("main", MasterMode.Logo);
        Assert.Equal(MasterMode.Logo, live.Frame("main").Master);
        Assert.Equal(MasterMode.None, live.Frame("stage").Master);
    }

    [Fact]
    public void Overlays_from_different_terminals_sit_together()
    {
        var live = new LiveState();
        live.SetScreens([Main]);
        var lt = live.RegisterSource("pc3", "T3", SectionKind.LowerThirds, null).Key;
        var cd = live.RegisterSource("pc4", "T4", SectionKind.Countdown, null).Key;
        live.Pin(lt, "main");
        live.Pin(cd, "main");
        live.SetContent(lt, new SlideContent("Pastor Samuel"));
        live.SetContent(cd, new SlideContent("04:55"));
        Assert.Equal(2, live.Frame("main").Items.Count(i => i.Layer == Layer.Overlays));
    }

    [Fact]
    public void Export_and_import_restore_routing_and_picture_after_restart()
    {
        var (live, s, _) = Setup();
        live.Pin(s, "main");
        live.SetContent(s, new SlideContent("John 3:16"));
        live.ToggleMaster("stage", MasterMode.Logo);
        var saved = live.Export();

        var restored = new LiveState();
        restored.SetScreens([Main, Stage]);
        restored.Import(saved);
        Assert.Equal("John 3:16", Showing(restored, "main", Layer.Scripture));
        Assert.Equal(MasterMode.Logo, restored.Frame("stage").Master);
        Assert.False(restored.Source(s)!.Online);
        Assert.Equal(RouteState.Live, restored.Source(s)!.Routes["main"]);
    }

    [Fact]
    public void Removing_a_screen_drops_its_routes()
    {
        var (live, s, _) = Setup();
        live.Pin(s, "stage");
        live.SetScreens([Main]);
        Assert.False(live.Source(s)!.Routes.ContainsKey("stage"));
    }
}
