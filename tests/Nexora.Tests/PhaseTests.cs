using System.Diagnostics;
using Nexora.Core.Live;
using Nexora.Core.Model;
using Nexora.Core.Net;
using Nexora.Core.Protocol;
using Nexora.Core.Songs;
using Nexora.Server;

namespace Nexora.Tests;

/// <summary>Lyrics, countdowns, video, modes, the output engine's status and backups (Phases 3 to 8).</summary>
public class PhaseTests
{
    static PhaseTests() => NexoraClient.RetryDelay = TimeSpan.FromMilliseconds(300);

    private static async Task WaitFor(Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("Condition not met in time");
            await Task.Delay(10);
        }
    }

    private static async Task<NexoraClient> SignedIn(ServerFixture f, string pc, UserInfo user, string pin)
    {
        var c = f.Client(pc);
        await WaitFor(() => c.Status == ConnectionStatus.Connected);
        var r = await c.LoginAsync(user.Id, pin);
        Assert.True(r.Ok, r.Error);
        return c;
    }

    // ---------------- Songs ----------------

    [Fact]
    public void Pasted_lyrics_split_into_labelled_parts()
    {
        var parts = SongText.Split("Verse 1\nAmazing grace\nHow sweet the sound\n\nChorus\nMy chains are gone\n\n[Bridge]\nThe earth shall soon");
        Assert.Equal(["Verse 1", "Chorus", "Bridge"], parts.Select(p => p.Label));
        Assert.Equal(2, parts[0].Lines.Count);
        Assert.Equal("V1", SongText.Code("Verse 1"));
        Assert.Equal("C", SongText.Code("Chorus"));
    }

    [Fact]
    public void Long_parts_break_into_slides_by_line_count()
    {
        var part = new SongPart("Verse 1", "a\nb\nc\nd\ne");
        Assert.Single(SongText.Slides(part, 0));
        var two = SongText.Slides(part, 2);
        Assert.Equal(3, two.Count);
        Assert.Equal(["e"], two[2]);
    }

    [Fact]
    public void Song_order_repeats_the_chorus_without_retyping()
    {
        var parts = SongText.Split("Verse 1\nx\n\nChorus\ny\n\nVerse 2\nz");
        var order = SongText.ParseOrder("V1 C V2 C C", parts);
        Assert.Equal(5, order.Count);
        Assert.Equal("C V2 C", SongText.OrderText(order.Skip(1).Take(3)));
    }

    [Fact]
    public void Search_finds_songs_by_words_ignoring_accents_and_case()
    {
        var songs = new[]
        {
            new Song { Id = "1", Title = "Great Is Thy Faithfulness", Parts = [new SongPart("Verse 1", "Great is Thy faithfulness O God my Father")] },
            new Song { Id = "2", Title = "Way Maker", Parts = [new SongPart("Verse 1", "You are here moving in our midst")] },
        };
        Assert.Equal("2", SongText.Search(songs, "MOVING in our").Single().Id);
        Assert.Equal("1", SongText.Search(songs, "faithfulness").First().Id);
    }

    [Fact]
    public void Plain_text_import_keeps_title_and_parts()
    {
        var song = SongImport.PlainText("Verse 1\nLine one\n\nChorus\nLine two", "Test Song");
        Assert.NotNull(song);
        Assert.Equal("Test Song", song!.Title);
        Assert.Equal(2, song.Parts.Count);
    }

    // ---------------- Countdowns and video timing ----------------

    [Fact]
    public void Countdown_warns_then_reaches_zero_then_counts_overtime()
    {
        var now = DateTime.UtcNow;
        var cd = new CountdownShow("Service starts", CountdownMode.Duration, true, 300, null, 60, true, CountdownLook.Badge, ZeroAction.None, now.AddSeconds(120));
        Assert.Equal(CountdownState.Normal, cd.State(now));
        Assert.Equal(CountdownState.Warning, cd.State(now.AddSeconds(70)));
        Assert.Equal(CountdownState.Zero, cd.State(now.AddSeconds(120.5)));
        Assert.Equal(CountdownState.Overtime, cd.State(now.AddSeconds(130)));
        var paused = cd with { Running = false, RemainingSeconds = 42 };
        Assert.Equal(42, paused.Remaining(now.AddHours(1)));
    }

    private static MediaInfo Clip(double seconds) => new("clip.mp4", "Clip", MediaKind.Video, seconds, 1920, 1080, "h264", null, null);

    [Fact]
    public void Video_position_follows_the_clock_and_loops()
    {
        var start = DateTime.UtcNow;
        var p = new MediaPlay(Clip(20), true, start, 0, false, 0, 0, 100, null, false, 1);
        Assert.Equal(5, p.Position(start.AddSeconds(5)), 3);
        Assert.Equal(15, p.Left(start.AddSeconds(5)), 3);
        Assert.False(p.Finished(start.AddSeconds(19)));
        Assert.True(p.Finished(start.AddSeconds(21)));
        var loop = p with { Loop = true };
        Assert.Equal(5, loop.Position(start.AddSeconds(25)), 3);
        Assert.False(loop.Finished(start.AddSeconds(100)));
        var paused = p with { Playing = false, PausedAt = 7 };
        Assert.Equal(7, paused.Position(start.AddSeconds(50)));
    }

    [Fact]
    public void Screen_returns_to_the_verse_when_a_video_ends()
    {
        var live = new LiveState();
        live.SetScreens([new ScreenInfo("main", "Main", ScreenKind.Audience, 0, true, 0)]);
        var verse = live.RegisterSource("pc1", "T1", SectionKind.Scripture, "Dan").Key;
        var media = live.RegisterSource(LiveState.LiveOutputPc, "Live Output", SectionKind.Media, "Op").Key;
        live.Pin(verse, "main");
        live.Pin(media, "main");
        live.SetContent(verse, new SlideContent("John 3:16"));
        var start = DateTime.UtcNow;
        live.SetContent(media, new SlideContent("Clip", Media: new MediaPlay(Clip(10), true, start, 0, false, 0, 0, 100, null, false, 1)));
        Assert.Contains(live.Frame("main").Items, i => i.Content.Media is not null);

        live.Tick(start.AddSeconds(5));
        Assert.Contains(live.Frame("main").Items, i => i.Content.Media is not null);

        live.Tick(start.AddSeconds(11));
        Assert.DoesNotContain(live.Frame("main").Items, i => i.Content.Media is not null);
        Assert.Contains(live.Frame("main").Items, i => i.Content.Text == "John 3:16");
    }

    [Fact]
    public void Countdown_reports_its_zero_action_once()
    {
        var live = new LiveState();
        var key = live.RegisterSource("pc1", "T1", SectionKind.Countdown, "Dan").Key;
        var now = DateTime.UtcNow;
        live.SetContent(key, new SlideContent("Starts", Countdown: new CountdownShow("Starts", CountdownMode.Duration, true, 10, null, 5, false,
            CountdownLook.Badge, ZeroAction.ShowLogo, now.AddSeconds(10))));
        Assert.Empty(live.Tick(now.AddSeconds(5)));
        var actions = live.Tick(now.AddSeconds(11));
        Assert.Equal(ZeroAction.ShowLogo, Assert.Single(actions).Action);
        Assert.Empty(live.Tick(now.AddSeconds(12)));
    }

    // ---------------- Live Output operator on the server ----------------

    [Fact]
    public async Task Video_from_Live_Output_goes_on_every_audience_screen()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var op = await SignedIn(f, "live", f.Admin, "1111");
        await op.SendAsync(new MediaCommand(new MediaPlay(Clip(30), true, DateTime.UtcNow, 0, false, 500, 500, 80, null, false, 7)));
        await WaitFor(() => op.Snapshot.Frames.Any(fr => fr.Items.Any(i => i.Content.Media is { Id: 7 })));
        var audience = op.Snapshot.Screens.Where(s => s.Kind is ScreenKind.Audience or ScreenKind.Broadcast).Select(s => s.Id).ToList();
        foreach (var id in audience)
            Assert.Contains(op.Snapshot.FrameFor(id)!.Items, i => i.Content.Media is not null);

        await op.SendAsync(new MediaCommand(null));
        await WaitFor(() => !op.Snapshot.Frames.Any(fr => fr.Items.Any(i => i.Content.Media is not null)));
    }

    [Fact]
    public async Task Only_the_Live_Output_operator_can_play_videos_or_change_modes()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var daniel = await SignedIn(f, "pc1", f.Daniel, "2222");
        Notice? notice = null;
        daniel.NoticeReceived += n => notice = n;
        await daniel.SendAsync(new SetModes(new LiveModes(Rehearsal: true)));
        await WaitFor(() => notice is not null);
        Assert.False(daniel.Snapshot.CurrentModes.Rehearsal);
    }

    [Fact]
    public async Task Rehearsal_mode_reaches_every_PC_and_blocks_recording()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var op = await SignedIn(f, "live", f.Admin, "1111");
        await op.SendAsync(new SetModes(new LiveModes(Rehearsal: true, Booth: "5 minutes")));
        await WaitFor(() => op.Snapshot.CurrentModes.Rehearsal);
        Assert.Equal("5 minutes", op.Snapshot.CurrentModes.Booth);
        Assert.All(op.Snapshot.Frames, fr => Assert.True(fr.Rehearsal || op.Snapshot.Screens.First(s => s.Id == fr.ScreenId).Kind != ScreenKind.Audience));

        Notice? notice = null;
        op.NoticeReceived += n => notice = n;
        await op.SendAsync(new EngineCommand(EngineAction.StartRecording));
        await WaitFor(() => notice is not null);
        Assert.Contains("Rehearsal", notice!.Text);
    }

    [Fact]
    public async Task Service_mode_locks_settings_changes()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var op = await SignedIn(f, "live", f.Admin, "1111");
        await op.SendAsync(new SetModes(new LiveModes(ServiceMode: true)));
        await WaitFor(() => op.Snapshot.CurrentModes.ServiceMode);
        Notice? notice = null;
        op.NoticeReceived += n => notice = n;
        await op.SendAsync(new SaveBroadcastSettings(new BroadcastSettings(StreamUrl: "rtmp://example/live")));
        await WaitFor(() => notice is not null);
        Assert.Contains("Service mode", notice!.Text);
        Assert.Null(f.Store.Broadcast.StreamUrl);
    }

    [Fact]
    public async Task Output_engine_status_reaches_signed_in_PCs()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var op = await SignedIn(f, "live", f.Admin, "1111");
        EngineStatus? seen = null;
        op.MessageReceived += m => { if (m is EngineStatusMessage e) seen = e.Status; };
        await using var engine = f.Client("live-output", ClientKind.OutputEngine);
        await WaitFor(() => engine.Status == ConnectionStatus.Connected);
        await engine.SendAsync(new EngineStatusMessage(new EngineStatus(Recording: true, Fps: 30, DiskFreeGb: 120)));
        await WaitFor(() => seen is not null);
        Assert.True(seen!.Recording);
        Assert.Equal(30, seen.Fps);
    }

    [Fact]
    public async Task A_terminal_cannot_pretend_to_be_the_output_engine()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var op = await SignedIn(f, "live", f.Admin, "1111");
        var seen = false;
        op.MessageReceived += m => { if (m is EngineStatusMessage) seen = true; };
        await using var daniel = await SignedIn(f, "pc1", f.Daniel, "2222");
        await daniel.SendAsync(new EngineStatusMessage(new EngineStatus(Recording: true)));
        await Task.Delay(400);
        Assert.False(seen);
    }

    // ---------------- Backups ----------------

    [Fact]
    public async Task Backup_contains_the_library_and_is_recognised()
    {
        await using var f = new ServerFixture();
        f.Store.SaveSongs([new Song { Id = "s1", Title = "Way Maker", Parts = [new SongPart("Verse 1", "You are here")] }]);
        var zip = Path.Combine(f.Dir, "backup.zip");
        f.Server.WriteBackup(zip);
        Assert.True(LiveServer.IsBackup(zip));
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.NotNull(archive.GetEntry("library/nexora-library.db"));

        var notBackup = Path.Combine(f.Dir, "other.zip");
        using (var z = System.IO.Compression.ZipFile.Open(notBackup, System.IO.Compression.ZipArchiveMode.Create)) z.CreateEntry("hello.txt");
        Assert.False(LiveServer.IsBackup(notBackup));
    }
}
