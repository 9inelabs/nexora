using System.Diagnostics;
using Nexora.Core.Live;
using Nexora.Core.Model;
using Nexora.Core.Net;
using Nexora.Core.Protocol;

namespace Nexora.Tests;

/// <summary>The Minister Timer: its colours, the Stage Monitor routing and the Live Output clock.</summary>
public class MinisterTimerTests
{
    static MinisterTimerTests() => NexoraClient.RetryDelay = TimeSpan.FromMilliseconds(300);

    private static readonly DateTime Now = new(2026, 11, 30, 10, 0, 0, DateTimeKind.Utc);

    private static MinisterTimerShow Running(double secondsLeft, bool countOver = true) =>
        new("Guest Minister", "Sermon", 30 * 60, true, 0, 600, 300, countOver, true, MinisterLook.FullScreen, Now.AddSeconds(secondsLeft));

    [Theory]
    [InlineData(1477, MinisterColour.Green, "24:37")]
    [InlineData(601, MinisterColour.Green, "10:01")]
    [InlineData(600, MinisterColour.Yellow, "10:00")]
    [InlineData(525, MinisterColour.Yellow, "8:45")]
    [InlineData(300, MinisterColour.Red, "5:00")]
    [InlineData(171, MinisterColour.Red, "2:51")]
    [InlineData(0.4, MinisterColour.Red, "0:01")]
    [InlineData(-134, MinisterColour.Over, "−2:14")]
    public void Colours_follow_the_time_left(double left, MinisterColour colour, string text)
    {
        var t = Running(left);
        Assert.Equal(colour, t.Colour(Now));
        Assert.Equal(text, t.Text(Now));
    }

    [Fact]
    public void Messages_under_the_digits()
    {
        var local = new DateTime(2026, 11, 30, 10, 42, 0);
        Assert.Equal("of 30:00 · ends 11:06 AM", Running(1477).Message(Now, local, false).Replace(' ', ' '));
        Assert.Equal("Please start to close", Running(171).Message(Now, local, false));
        Assert.Equal("Over by 2 minutes 14 seconds", Running(-134).Message(Now, local, false));
        Assert.Equal("Over by 1 minute", Running(-60).Message(Now, local, false));
    }

    [Fact]
    public void Without_counting_over_it_stops_at_zero_but_stays_red()
    {
        var t = Running(-30, countOver: false);
        Assert.Equal("0:00", t.Text(Now));
        Assert.Equal(MinisterColour.Over, t.Colour(Now));
    }

    [Fact]
    public void Paused_timer_keeps_its_time()
    {
        var t = new MinisterTimerShow("A", "", 600, false, 125, 300, 120);
        Assert.Equal("2:05", t.Text(Now.AddHours(1)));
        Assert.Equal(MinisterColour.Yellow, t.Colour(Now));
        Assert.Equal(125 / 600.0, t.Fraction(Now), 3);
    }

    [Fact]
    public void Goes_straight_to_the_Stage_Monitor_and_never_the_audience()
    {
        var live = new LiveState();
        live.SetScreens([
            new ScreenInfo("main", "Main Audience Screen", ScreenKind.Audience, 0, true, 0),
            new ScreenInfo("stage", "Stage Monitor", ScreenKind.Stage, 0, false, 1),
        ]);
        var key = live.RegisterSource("pc2", "TERMINAL-02", SectionKind.MinisterTimer, "Franklyn").Key;
        live.SetContent(key, new SlideContent("Guest Minister 30:00", Minister: Running(1800)));
        Assert.Contains(live.Frame("stage").Items, i => i.Content.Minister is not null);
        Assert.DoesNotContain(live.Frame("main").Items, i => i.Content.Minister is not null);

        // Taken down on the Stage Monitor by the Live Output operator: the next send respects that.
        live.TakeDown(key, "stage");
        live.SetContent(key, new SlideContent("Guest Minister 29:00", Minister: Running(1740)));
        Assert.DoesNotContain(live.Frame("stage").Items, i => i.Content.Minister is not null);
    }

    [Fact]
    public async Task Runs_on_the_Live_Output_clock_and_keeps_todays_list()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        var c = f.Client("pc2");
        await WaitFor(() => c.Status == ConnectionStatus.Connected);
        Assert.True((await c.LoginAsync(f.Admin.Id, "1111")).Ok);
        await c.OpenSectionsAsync([SectionKind.MinisterTimer]);

        var show = new MinisterTimerShow("Guest Minister", "Sermon", 1800, true, 1800);
        await c.SendContentAsync(SectionKind.MinisterTimer, new SlideContent("Guest Minister", Minister: show));
        // The first snapshot (with the screen list) can arrive a moment after sign-in.
        await WaitFor(() => c.Snapshot.Screens.Any(s => s.Kind == ScreenKind.Stage));
        var stage = c.Snapshot.Screens.First(s => s.Kind == ScreenKind.Stage).Id;
        await WaitFor(() => c.Snapshot.FrameFor(stage)?.Items.Any(i => i.Content.Minister is { EndsUtc: not null }) == true);
        var sent = c.Snapshot.FrameFor(stage)!.Items.First(i => i.Content.Minister is not null).Content.Minister!;
        Assert.InRange(sent.Remaining(DateTime.UtcNow), 1790, 1800.5);

        await c.SendAsync(new SaveMinisters([new MinisterEntry("m1", "Guest Minister", "Sermon", 30), new MinisterEntry("m2", "Pastor", "Exhortation", 10)]));
        await WaitFor(() => f.Store.Ministers.Count == 2);
        Assert.Equal("Exhortation", f.Store.Ministers[1].Purpose);
        await c.DisposeAsync();
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
}
