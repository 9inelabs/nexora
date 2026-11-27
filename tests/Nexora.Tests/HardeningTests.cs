using System.Text.Json;
using Nexora.Audio.Engine;
using Nexora.Audio.Ipc;
using Nexora.Audio.Mix;
using Xunit;

namespace Nexora.Tests;

/// <summary>Phase 7: finer recall scopes, channel safes per section, recovery, the event log and diagnostics.</summary>
public class RecallScopeTests
{
    private static (MixState Now, MixState Saved) Pair()
    {
        var saved = MixState.CreateDefault();
        saved.Channels[0].Fader = -10;
        saved.Channels[0].Mute = true;
        saved.Channels[0].Eq.Bands[0].Gain = 6;
        saved.Channels[0].Comp.Threshold = -30;
        saved.Channels[0].Trim = 8;
        var now = MixState.CreateDefault();
        return (now, saved);
    }

    [Fact]
    public void An_eq_only_recall_brings_back_the_eq_and_leaves_faders_mutes_and_dynamics()
    {
        var (now, saved) = Pair();
        var after = SceneRecall.Apply(now, saved, RecallScope.Eq);
        var ch = after.Channels[0];
        Assert.Equal(6, ch.Eq.Bands[0].Gain);
        Assert.Equal(now.Channels[0].Fader, ch.Fader);
        Assert.False(ch.Mute);
        Assert.Equal(now.Channels[0].Comp.Threshold, ch.Comp.Threshold);
        Assert.Equal(now.Channels[0].Trim, ch.Trim);
    }

    [Fact]
    public void Fader_and_mute_are_separate_scopes()
    {
        var (now, saved) = Pair();
        var faders = SceneRecall.Apply(now, saved, RecallScope.Fader);
        Assert.Equal(-10, faders.Channels[0].Fader);
        Assert.False(faders.Channels[0].Mute);
        var mutes = SceneRecall.Apply(now, saved, RecallScope.Mute);
        Assert.True(mutes.Channels[0].Mute);
        Assert.Equal(now.Channels[0].Fader, mutes.Channels[0].Fader);
    }

    [Fact]
    public void Input_and_dynamics_scopes_bring_back_only_their_own_part()
    {
        var (now, saved) = Pair();
        var input = SceneRecall.Apply(now, saved, RecallScope.Input);
        Assert.Equal(8, input.Channels[0].Trim);
        Assert.Equal(0, input.Channels[0].Eq.Bands[0].Gain, 3);
        var dyn = SceneRecall.Apply(now, saved, RecallScope.Dynamics);
        Assert.Equal(-30, dyn.Channels[0].Comp.Threshold);
        Assert.Equal(now.Channels[0].Trim, dyn.Channels[0].Trim);
    }

    [Fact]
    public void A_channel_safe_for_its_eq_keeps_its_eq_while_everything_else_is_recalled()
    {
        var (now, saved) = Pair();
        now.Channels[0].Safe = RecallScope.Eq | RecallScope.Dynamics;
        now.Channels[0].Eq.Bands[0].Gain = -3;
        var after = SceneRecall.Apply(now, saved, RecallScope.All);
        var ch = after.Channels[0];
        Assert.Equal(-3, ch.Eq.Bands[0].Gain);
        Assert.Equal(now.Channels[0].Comp.Threshold, ch.Comp.Threshold);
        Assert.Equal(-10, ch.Fader);
        Assert.True(ch.Mute);
        Assert.Equal(8, ch.Trim);
        // The section safe is the live strip's own: it stays after the recall.
        Assert.Equal(RecallScope.Eq | RecallScope.Dynamics, ch.Safe);
    }

    [Fact]
    public void A_fully_safe_channel_keeps_everything()
    {
        var (now, saved) = Pair();
        now.Channels[0].RecallSafe = true;
        var after = SceneRecall.Apply(now, saved, RecallScope.All);
        Assert.Equal(now.Channels[0].Fader, after.Channels[0].Fader);
        Assert.Equal(0, after.Channels[0].Eq.Bands[0].Gain, 3);
    }

    [Fact]
    public void The_preview_lists_a_partly_safe_channel_as_kept()
    {
        var (now, saved) = Pair();
        now.Channels[0].Safe = RecallScope.Eq;
        var after = SceneRecall.Apply(now, saved, RecallScope.All);
        var (_, safe, _) = RecallDiff.Compare(now, after, saved);
        Assert.Contains(now.Channels[0].Name.Length > 0 ? now.Channels[0].Name : now.Channels[0].Id, safe);
    }

    [Fact]
    public void Old_scope_names_still_read_and_the_words_are_plain()
    {
        Assert.Equal(RecallScope.Levels | RecallScope.Processing, JsonSerializer.Deserialize<RecallScope>("\"Levels, Processing\"", MixState.Json));
        Assert.Equal(RecallScope.All, JsonSerializer.Deserialize<RecallScope>("\"All\"", MixState.Json));
        Assert.Equal("everything", RecallScopes.Text(RecallScope.All));
        Assert.Equal("levels only", RecallScopes.Text(RecallScope.Levels));
        Assert.Equal("EQ, dynamics", RecallScopes.Text(RecallScope.Eq | RecallScope.Dynamics));
        Assert.Equal("levels, EQ", RecallScopes.Text(RecallScope.Levels | RecallScope.Eq));
    }

    [Fact]
    public void A_channel_can_be_given_its_own_colour()
    {
        var s = MixState.CreateDefault();
        Assert.True(MixParams.Apply(s, "ch3.colour", JsonSerializer.SerializeToElement("#9BD66B")));
        Assert.Equal("#9BD66B", s.Channels[2].Colour);
        Assert.Equal("#9BD66B", MixState.FromJson(s.ToJson())!.Channels[2].Colour);
    }

    [Fact]
    public void A_stereo_partner_takes_the_same_section_safe()
    {
        var s = MixState.CreateDefault();
        Assert.True(MixParams.Apply(s, "ch1.linked", JsonSerializer.SerializeToElement(true)));
        Assert.True(MixParams.Apply(s, "ch1.safe", JsonSerializer.SerializeToElement("Eq, Dynamics")));
        Assert.Equal(RecallScope.Eq | RecallScope.Dynamics, s.Channels[1].Safe);
    }
}

public class EventLogTests
{
    [Fact]
    public void Keeps_the_newest_first_and_writes_a_file_per_day()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexaudio-ev-" + Guid.NewGuid().ToString("n")[..6]);
        var log = new EventLog(dir);
        log.Add("one");
        log.Add("two", EventLevel.Warning);
        log.Add("three", EventLevel.Problem);
        Assert.Equal(["three", "two", "one"], log.Latest().Select(e => e.Text));
        Assert.Equal(2, log.CountSince(DateTime.UtcNow.AddMinutes(-1), EventLevel.Warning));
        var file = Assert.Single(log.Files());
        var text = File.ReadAllText(file);
        Assert.Contains("PROBLEM  three", text);
        Assert.Contains("WARNING  two", text);
    }

    [Fact]
    public void Holds_at_most_the_last_500_in_memory()
    {
        var log = new EventLog();
        for (var i = 0; i < 600; i++) log.Add("e" + i);
        Assert.Equal(600, log.Count);
        var all = log.Latest(1000);
        Assert.Equal(EventLog.Keep, all.Count);
        Assert.Equal("e599", all[0].Text);
    }

    [Fact]
    public void Dropouts_are_added_up_and_reported_at_most_every_ten_seconds()
    {
        var w = new DropoutWatch();
        var t = new DateTime(2026, 11, 29, 10, 0, 0, DateTimeKind.Utc);
        Assert.Null(w.Update(0, 0, t));
        Assert.Equal("1 dropout (a buffer was late or the driver lost sync).", w.Update(1, 0, t.AddSeconds(1)));
        Assert.Null(w.Update(3, 1, t.AddSeconds(3)));      // three more, too soon to say
        Assert.Contains("3 dropouts", w.Update(3, 1, t.AddSeconds(12)));
        Assert.Equal(4, w.Recent);
        Assert.Null(w.Update(3, 1, t.AddMinutes(11)));
        Assert.Equal(0, w.Recent);                          // older than 10 minutes
        Assert.Null(w.Update(0, 0, t.AddMinutes(12)));      // the driver was opened again: totals start from 0
    }

    [Fact]
    public void The_clock_is_measured_over_ten_seconds()
    {
        var c = new ClockWatch();
        var t = DateTime.UtcNow;
        c.Update(0, t);
        c.Update(240_000, t.AddSeconds(5));
        Assert.Equal(0, c.Rate);
        c.Update(480_024, t.AddSeconds(10));
        Assert.Equal(48002.4, c.Rate, 1);
        Assert.Equal(50, c.Ppm(48000), 0);
    }

    [Fact]
    public async Task The_desk_puts_its_own_notes_in_the_event_log()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexaudio-evd-" + Guid.NewGuid().ToString("n")[..6]);
        using var desk = new Desk(dir, network: false);
        var saved = await desk.SaveAsync(null, "Sunday", "", "test", false, RecallScope.All, 0);
        Assert.True(desk.Recall(saved.Id, false));
        Assert.Contains(desk.Events.Latest(), e => e.Text.Contains("Recalled scene \"Sunday\""));
        Assert.Contains(desk.Status.Events, e => e.Text.Contains("Recalled scene"));
        Assert.True(Directory.Exists(Path.Combine(dir, "events")));
    }
}

public class RecoveryTests
{
    private static async Task Until(Func<bool> ok, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!ok() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(ok());
    }

    [Fact]
    public void A_sample_that_is_not_a_number_never_reaches_an_output()
    {
        var c = new EngineCore(4, 4, 64, 48000);
        for (var n = 0; n < 64; n++) c.In[0][n] = 0.25f;
        c.In[0][5] = float.NaN;
        c.In[1][7] = float.PositiveInfinity;
        c.Process(64);
        Assert.Equal(0, c.Out[0][5]);
        Assert.Equal(0, c.Out[1][7]);
        Assert.Equal(0.25f, c.Out[0][6]);
        Assert.Equal(2, c.BadSamples);
    }

    [Fact]
    public void A_mix_that_fails_plays_the_plain_patch_instead_of_stopping_the_sound()
    {
        // The mix was made for 32 frames; a 64 frame buffer makes it fail inside.
        var c = new EngineCore(4, 4, 64, 48000) { Mix = new MixEngine(4, 4, 32, 48000) };
        var s = MixState.CreateDefault();
        c.Mix!.Load(s);
        for (var n = 0; n < 64; n++) c.In[2][n] = 0.5f;
        c.Process(64);
        Assert.True(c.MixFaults > 0);
        Assert.Equal(0.5f, c.Out[2][40]); // straight through
        Assert.NotNull(c.LastFault);
    }

    [Fact]
    public async Task The_sound_comes_back_by_itself_after_the_cable_is_put_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexaudio-rec-" + Guid.NewGuid().ToString("n")[..6]);
        using var desk = new Desk(dir, network: false);
        using var e = new AudioEngine(dir, desk: desk);
        await e.OpenAsync(SimulatedDevice.DeviceName, 128);
        await Until(() => e.Status.State == EngineState.Running);

        e.PretendUnplug(true);
        await Until(() => e.Status.State == EngineState.Problem, 8000);
        Assert.Equal(1, e.Status.DeviceLosses);
        Assert.Contains(desk.Events.Latest(), ev => ev.Level == EventLevel.Problem && ev.Text.Contains("No sound is coming"));
        // It keeps trying (and failing) to open the device while the cable is out; nothing crashes.
        await Until(() => e.Status.Restarts >= 1, 10000);
        Assert.NotEqual(EngineState.Running, e.Status.State);

        e.PretendUnplug(false);
        await Until(() => e.Status.State == EngineState.Running, 10000);
        Assert.NotNull(desk.Mix);
        Assert.Contains(desk.Events.Latest(), ev => ev.Text.StartsWith("Sound running on"));
    }

    [Fact]
    public void The_report_says_what_happened_in_plain_words()
    {
        var e = EngineStatus.Empty with { Driver = "X-USB ASIO Driver", SampleRate = 48000, BufferSize = 64, MeasuredRate = 48000.5, Inputs = 32, Outputs = 32 };
        var events = new List<DeskEvent> { new(DateTime.UtcNow, EventLevel.Problem, "Lost the link to the X32.") };
        var text = DiagReport.Text(e, DeskStatus.Empty, events, DateTime.UtcNow);
        Assert.Contains("X-USB ASIO Driver", text);
        Assert.Contains("48000.5 Hz", text);
        Assert.Contains("steady", text);
        Assert.Contains("PROBLEM  Lost the link to the X32.", text);
        var dir = Path.Combine(Path.GetTempPath(), "nexaudio-rep-" + Guid.NewGuid().ToString("n")[..6]);
        var file = DiagReport.Write(dir, text, DateTime.UtcNow);
        Assert.True(File.Exists(file));
    }
}
