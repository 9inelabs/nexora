using System.IO.Compression;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;
using Nexora.Audio.Plugins;
using Nexora.Audio.Record;
using Nexora.Audio.Show;

namespace Nexora.Tests;

/// <summary>Show control: the recorder and virtual soundcheck, show files, backups, the cue list, macros and recall previews.</summary>
public class ShowControlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexaudio-show-" + Guid.NewGuid().ToString("N"));
    private string Other => _dir + "-other";

    public void Dispose()
    {
        foreach (var d in new[] { _dir, Other })
            try { Directory.Delete(d, true); } catch { }
    }

    private Desk NewDesk(string? dir = null) => new(dir ?? _dir, launcher: new InProcessHostLauncher(), network: false);

    private static float Signal(int ch, long n) => ch switch
    {
        0 => 0.5f * MathF.Sin(n * 0.05f),
        1 => 0.25f,
        _ => 0,
    };

    private static void Feed(EngineCore core, long start)
    {
        for (var ch = 0; ch < core.Inputs; ch++)
            for (var n = 0; n < 64; n++) core.In[ch][n] = Signal(ch, start + n);
    }

    private static async Task Until(Func<bool> ok, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!ok() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(ok(), "timed out");
    }

    [Theory]
    [InlineData(RecordFormat.Pcm24, 1e-6)]
    [InlineData(RecordFormat.Float32, 1e-7)]
    public void A_wav_file_reads_back_what_was_written(RecordFormat format, double tolerance)
    {
        Directory.CreateDirectory(_dir);
        var f = Path.Combine(_dir, "t.wav");
        var data = Enumerable.Range(0, 2000).Select(i => 0.9f * MathF.Sin(i * 0.01f)).ToArray();
        using (var w = new WavWriter(f, 48000, 2, format)) w.Write(data);
        using var r = new WavReader(f);
        Assert.Equal(48000, r.SampleRate);
        Assert.Equal(2, r.Channels);
        Assert.Equal(1000, r.Frames);
        var back = new float[2000];
        Assert.Equal(1000, r.Read(back));
        for (var i = 0; i < data.Length; i++) Assert.True(Math.Abs(back[i] - data[i]) < tolerance + 1.2e-7, $"sample {i}");
    }

    [Fact]
    public async Task Virtual_soundcheck_records_every_channel_then_plays_it_back_into_the_desk()
    {
        using var desk = NewDesk();
        desk.Tape.Configure(s => s with { Armed = [1, 2, 3], RecordMain = true });
        var core = new EngineCore(32, 32, 64, 48000) { Mix = desk.Attach(32, 32, 64, 48000), Tape = desk.Tape };

        var take = desk.StartRecording("Rehearsal", "Test");
        const int blocks = 300;
        for (var b = 0; b < blocks; b++)
        {
            Feed(core, b * 64L);
            core.Process(64);
        }
        desk.Tape.StopRecording();

        var info = Assert.Single(desk.Tape.Takes());
        Assert.Equal("Rehearsal", info.Name);
        Assert.Equal(3, info.Tracks);
        Assert.True(info.CanSoundcheck);
        Assert.Equal(blocks * 64 / 48000.0, info.Seconds, 3);
        var folder = Directory.GetDirectories(desk.Tape.Folder).Single();
        Assert.True(File.Exists(Path.Combine(folder, "Main mix.wav")));
        Assert.Equal(4, Directory.GetFiles(folder, "*.wav").Length);

        // Play it back: the mics go quiet, but channels 1 and 2 still hear the recording. Channel 2 is kept live.
        desk.Tape.SetLive(2, true);
        desk.Tape.Play(take.Id);
        Assert.True(desk.Tape.Playback);
        await Until(() => desk.Tape.Status.State == TapeState.Playing);
        await Task.Delay(100); // let the disk thread fill up
        var ch1 = new List<float>();
        for (var b = 0; b < 100; b++)
        {
            foreach (var x in core.In) Array.Fill(x, 0.1f); // what the microphones hear now
            core.Process(64);
            ch1.AddRange(core.In[0]);
            Assert.Equal(0.1f, core.In[1][0]); // kept live
            Assert.Equal(0f, core.In[2][5]); // recorded silence, not the live 0.1
            Assert.Equal(0.1f, core.In[3][0]); // not recorded, so live
        }
        for (var n = 0; n < ch1.Count; n++) Assert.True(Math.Abs(ch1[n] - Signal(0, n)) < 1e-5, $"sample {n}: {ch1[n]} vs {Signal(0, n)}");

        // Back to the live inputs: nothing is replaced any more.
        desk.Tape.SetPlayback(false);
        foreach (var x in core.In) Array.Fill(x, 0.1f);
        core.Process(64);
        Assert.Equal(0.1f, core.In[0][10]);
    }

    [Fact]
    public void Recording_is_refused_during_playback_and_without_sound()
    {
        using var desk = NewDesk();
        var ex = Assert.Throws<InvalidOperationException>(() => desk.StartRecording(null, ""));
        Assert.Contains("Start the sound", ex.Message);
        desk.Attach(32, 32, 64, 48000);
        desk.Tape.SetPlayback(true);
        Assert.Throws<InvalidOperationException>(() => desk.StartRecording(null, ""));
    }

    [Fact]
    public void A_folder_of_wav_files_from_another_program_can_be_played_into_the_desk()
    {
        var src = Path.Combine(Other, "reaper");
        Directory.CreateDirectory(src);
        foreach (var n in new[] { "01 Kick", "02 Snare" })
            using (var w = new WavWriter(Path.Combine(src, n + ".wav"), 48000, 1, RecordFormat.Pcm24))
                w.Write(new float[4800]);
        using var desk = NewDesk();
        desk.Attach(32, 32, 64, 48000);
        var take = desk.Tape.Import(src);
        Assert.True(take.Imported);
        Assert.Equal(["01 Kick", "02 Snare"], take.Tracks.Select(t => t.Name));
        Assert.Equal(0.1, desk.Tape.Takes().Single().Seconds, 3);
        desk.Tape.Load(take.Id);
        desk.Tape.Delete(take.Id);
        Assert.Empty(desk.Tape.Takes());
        Assert.True(File.Exists(Path.Combine(src, "01 Kick.wav"))); // the other program's files are left alone
    }

    [Fact]
    public async Task A_show_file_carries_the_mix_scenes_cues_and_macros_to_another_desk()
    {
        string file = Path.Combine(_dir, "Easter.nexshow");
        using (var desk = NewDesk())
        {
            desk.Set("ch1.fader", MixRig.J(-4.0));
            var snap = await desk.SaveAsync(null, "Sermon", "", "Test", true, RecallScope.Levels, 0);
            desk.SaveMacro(new Macro { Name = "Choir off", Key = 2, Steps = [new MacroStep { Action = MacroAction.Mute, Strip = "ch5,ch6" }] });
            desk.SaveCue(new Cue { Name = "Sermon", SnapshotId = snap.Id });
            desk.SaveShow(file, "Easter", "Test");
        }
        Assert.Equal("Easter", ShowFiles.Inspect(file).Manifest.Name);

        using var other = NewDesk(Other);
        other.Set("ch1.fader", MixRig.J(-20.0));
        var c = other.OpenShow(file, takeMix: false, "Test");
        Assert.Equal(1, c.Snapshots);
        Assert.Equal(1, c.Cues);
        Assert.Equal(-20.0, other.Mix.Channels[0].Fader); // the live mix stays unless asked
        Assert.Equal("Choir off", Assert.Single(other.Status.Show.Macros).Name);
        Assert.Single(other.Status.Snapshots);

        other.OpenShow(file, takeMix: true, "Test");
        Assert.Equal(-4.0, other.Mix.Channels[0].Fader);
        Assert.Contains(other.Status.Backups, b => b.Reason == "before opening");
    }

    [Fact]
    public void A_damaged_show_file_changes_nothing_and_unknown_parts_are_left_out()
    {
        using var desk = NewDesk();
        desk.SaveCue(new Cue { Name = "Welcome" });
        var bad = Path.Combine(_dir, "bad.nexshow");
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                w.Write("""{"format":"nexaudio-show","version":1,"name":"Bad","savedUtc":"2026-01-01T00:00:00Z","savedBy":"","app":"x"}""");
            using (var w = new StreamWriter(zip.CreateEntry("scenes/abc.json").Open())) w.Write("{ not json");
            using (var w = new StreamWriter(zip.CreateEntry("../evil.json").Open())) w.Write("{}");
        }
        var ex = Assert.Throws<InvalidOperationException>(() => desk.OpenShow(bad, true, ""));
        Assert.Contains("damaged", ex.Message);
        Assert.Equal("Welcome", Assert.Single(desk.Status.Show.Cues).Name);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "evil.json")));
        Assert.Throws<InvalidOperationException>(() => desk.OpenShow(Path.Combine(_dir, "missing.nexshow"), true, ""));
    }

    [Fact]
    public async Task Go_runs_the_cue_list_in_order_with_its_macros()
    {
        using var desk = NewDesk();
        desk.Set("ch1.fader", MixRig.J(-10.0));
        var a = await desk.SaveAsync(null, "Worship", "", "", true, RecallScope.Levels, 0);
        desk.Set("ch1.fader", MixRig.J(0.0));
        var b = await desk.SaveAsync(null, "Sermon", "", "", true, RecallScope.Levels, 0);
        desk.SaveMacro(new Macro { Id = "m1", Name = "Band off", Steps = [new MacroStep { Action = MacroAction.Mute, Strip = "ch3, ch4" }] });
        desk.SaveCue(new Cue { Name = "Worship", SnapshotId = a.Id });
        desk.SaveCue(new Cue { Name = "Sermon", SnapshotId = b.Id, MacroId = "m1" });

        Assert.Equal("Worship", desk.PreviewCue(null)!.Name);
        Assert.True(desk.GoCue());
        Assert.Equal(-10.0, desk.Mix.Channels[0].Fader);
        Assert.True(desk.GoCue());
        Assert.Equal(0.0, desk.Mix.Channels[0].Fader);
        await Until(() => desk.Mix.Channels[3].Mute);
        Assert.True(desk.Mix.Channels[2].Mute);
        Assert.False(desk.GoCue()); // the end of the list
        Assert.True(desk.BackCue());
        Assert.Equal(-10.0, desk.Mix.Channels[0].Fader);

        // Deleting a macro takes it off its cues.
        desk.DeleteMacro("m1");
        Assert.All(desk.Status.Show.Cues, c => Assert.Null(c.MacroId));
    }

    [Fact]
    public async Task A_macro_reports_steps_it_cannot_do_and_carries_on()
    {
        using var desk = NewDesk();
        string? notice = null;
        desk.Notice += (t, _) => notice = t;
        desk.SaveMacro(new Macro
        {
            Id = "m", Name = "Mixed",
            Steps = [new MacroStep { Action = MacroAction.Mute, Strip = "nope" }, new MacroStep { Action = MacroAction.Fader, Strip = "ch2", Value = -6 }],
        });
        Assert.True(await desk.RunMacroAsync("m"));
        Assert.Equal(-6, desk.Mix.Channels[1].Fader);
        Assert.Contains("nope", notice);
    }

    [Fact]
    public async Task The_preview_lists_what_a_recall_would_change_and_what_the_safes_keep()
    {
        using var desk = NewDesk();
        desk.Set("ch1.fader", MixRig.J(-5.0));
        desk.Set("ch2.fader", MixRig.J(-5.0));
        var snap = await desk.SaveAsync(null, "Quiet", "", "", true, RecallScope.Levels, 2);
        desk.Set("ch1.fader", MixRig.J(0.0));
        desk.Set("ch1.mute", MixRig.J(true));
        desk.Set("ch2.fader", MixRig.J(0.0));
        desk.Set("ch2.recallSafe", MixRig.J(true));
        desk.Set("ch3.eq.1.gain", MixRig.J(6.0)); // processing is outside a levels-only snapshot

        var p = desk.Preview(snap.Id, true)!;
        Assert.Equal(2, p.Fade);
        Assert.Contains(p.Changes, c => c.StripId == "ch1" && c.What == "Fader" && c.To == "−5.0 dB");
        Assert.Contains(p.Changes, c => c.StripId == "ch1" && c.What == "Mute" && c.To == "on");
        Assert.DoesNotContain(p.Changes, c => c.StripId == "ch2");
        Assert.DoesNotContain(p.Changes, c => c.StripId == "ch3");
        Assert.Single(p.KeptSafe);
        Assert.Equal(0.0, desk.Mix.Channels[0].Fader); // nothing changed yet

        var all = desk.Preview(snap.Id, true, RecallScope.All)!;
        Assert.Contains(all.Changes, c => c.StripId == "ch3" && c.What == "Processing" && c.To.Contains("EQ"));
    }

    [Fact]
    public void Backups_are_kept_and_can_be_brought_back()
    {
        using var desk = NewDesk();
        desk.Set("ch1.fader", MixRig.J(-3.0));
        Assert.NotNull(desk.BackupNow("by hand"));
        desk.Set("ch1.fader", MixRig.J(-30.0));
        var b = desk.Status.Backups.First(x => x.Reason == "by hand");
        desk.RestoreBackup(b.File, takeMix: true, "");
        Assert.Equal(-3.0, desk.Mix.Channels[0].Fader);
        Assert.Throws<InvalidOperationException>(() => desk.RestoreBackup("..\\x.nexshow", true, ""));
    }

    [Fact]
    public void Cue_and_macro_lists_are_repaired_when_read()
    {
        var s = new ShowState
        {
            CurrentCueId = "gone",
            Cues = [new Cue { Id = "a", Name = "", Fade = 99 }, new Cue { Id = "a", Name = "Twin" }],
            Macros = [new Macro { Key = 3, Name = "One" }, new Macro { Key = 3, Name = "Two" }, new Macro { Key = 40 }],
        };
        s.Repair();
        Assert.Null(s.CurrentCueId);
        Assert.Equal("Cue", s.Cues[0].Name);
        Assert.Equal(30, s.Cues[0].Fade);
        Assert.NotEqual(s.Cues[0].Id, s.Cues[1].Id);
        Assert.Equal([3, 0, 0], s.Macros.Select(m => m.Key));
    }
}
