using System.Text.Json;
using Nexora.Audio.Mix;

namespace Nexora.Audio.Show;

/// <summary>One step in the order of service: recall a snapshot (or a scene) and/or run a macro.</summary>
public sealed class Cue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string Name { get; set; } = "";
    /// <summary>A note for the engineer: "Pastor to the pulpit, choir mics off".</summary>
    public string Notes { get; set; } = "";
    public string? SnapshotId { get; set; }
    public string? SceneId { get; set; }
    public string? MacroId { get; set; }
    /// <summary>Change what the snapshot recalls, for this cue only. Null = the snapshot's own.</summary>
    public RecallScope? Scope { get; set; }
    /// <summary>Fade time for this cue only. Null = the snapshot's own.</summary>
    public double? Fade { get; set; }
}

/// <summary>What one macro step does.</summary>
public enum MacroAction
{
    Mute,
    Unmute,
    /// <summary>Sets a fader to Value dB.</summary>
    Fader,
    SendOn,
    SendOff,
    Snapshot,
    Scene,
    NextCue,
    StartRecording,
    StopRecording,
    /// <summary>The channels play the recording (virtual soundcheck).</summary>
    PlaybackOn,
    /// <summary>The channels go back to the microphones.</summary>
    LiveInputs,
    ClearSolo,
    /// <summary>Waits Value seconds before the next step.</summary>
    Wait,
}

public sealed class MacroStep
{
    public MacroAction Action { get; set; }
    /// <summary>The strip (for mute, fader, send) or several strips separated by commas.</summary>
    public string? Strip { get; set; }
    public string? Bus { get; set; }
    /// <summary>The snapshot or scene.</summary>
    public string? Target { get; set; }
    public double Value { get; set; }
}

/// <summary>One button that does several things in a row.</summary>
public sealed class Macro
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string Name { get; set; } = "";
    public string Colour { get; set; } = "";
    /// <summary>F1 to F12 runs it from the keyboard (0 = no key).</summary>
    public int Key { get; set; }
    public List<MacroStep> Steps { get; set; } = [];
}

/// <summary>What happened on the church's screens (from NEXORA's Live Output PC).</summary>
public enum TriggerWhen
{
    /// <summary>A song's lyrics went up on the main screen. <see cref="ShowTrigger.Match"/> picks the song (empty: any song).</summary>
    SongStarts,
    /// <summary>A section went up on the main screen: Scripture, Announcements, Countdown, Media, MinisterTimer, Camera or LowerThirds.</summary>
    SectionLive,
    /// <summary>The main screen went to black or the logo, or everything was taken down.</summary>
    ScreenClear,
}

/// <summary>What a trigger does.</summary>
public enum TriggerAction
{
    Snapshot,
    Cue,
    NextCue,
    Macro,
}

/// <summary>When something goes live on the screens, NEX-Audio does something: "when Amazing Grace goes up, recall the Choir snapshot".</summary>
public sealed class ShowTrigger
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public bool On { get; set; } = true;
    public TriggerWhen When { get; set; }
    /// <summary>Part of the song title, or the section's name. Empty: any.</summary>
    public string Match { get; set; } = "";
    public TriggerAction Action { get; set; }
    /// <summary>The snapshot, cue or macro.</summary>
    public string? Target { get; set; }
}

/// <summary>One thing that happened on the screens, as NEX-Audio hears about it.</summary>
public sealed record LiveEvent(TriggerWhen When, string Name);

public static class ShowTriggers
{
    /// <summary>The triggers that this event sets off, in order.</summary>
    public static IReadOnlyList<ShowTrigger> Match(ShowState show, LiveEvent e)
    {
        if (!show.FollowNexora) return [];
        var name = Norm(e.Name);
        return show.Triggers.Where(t => t.On && t.When == e.When && (t.Action == TriggerAction.NextCue || !string.IsNullOrEmpty(t.Target)))
            .Where(t => Norm(t.Match) is var m && (m.Length == 0 || name.Contains(m, StringComparison.Ordinal)))
            .ToList();
    }

    private static string Norm(string? s) =>
        string.Join(' ', new string((s ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>The cue list and macros for the show. Saved to audio/show.json.</summary>
public sealed class ShowState
{
    public string Name { get; set; } = "";
    public List<Cue> Cues { get; set; } = [];
    public List<Macro> Macros { get; set; } = [];
    public string? CurrentCueId { get; set; }
    /// <summary>What NEX-Audio does when things go live on the screens.</summary>
    public List<ShowTrigger> Triggers { get; set; } = [];
    /// <summary>The triggers are switched on (off: they are kept but do nothing).</summary>
    public bool FollowNexora { get; set; }

    public static readonly JsonSerializerOptions Json = new(MixState.Json) { WriteIndented = true };

    public ShowState Clone() => JsonSerializer.Deserialize<ShowState>(JsonSerializer.Serialize(this, Json), Json)!;

    /// <summary>Fixes anything odd after reading a file: missing ids, too many entries, steps pointing nowhere.</summary>
    public void Repair()
    {
        Cues ??= [];
        Macros ??= [];
        Triggers ??= [];
        if (Triggers.Count > 64) Triggers = Triggers[..64];
        foreach (var t in Triggers)
        {
            if (string.IsNullOrWhiteSpace(t.Id)) t.Id = Guid.NewGuid().ToString("n")[..8];
            t.Match = (t.Match ?? "").Trim() is var m && m.Length > 60 ? m[..60] : m;
        }
        if (Cues.Count > 200) Cues = Cues[..200];
        if (Macros.Count > 48) Macros = Macros[..48];
        var ids = new HashSet<string>();
        foreach (var c in Cues)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || !ids.Add(c.Id)) c.Id = Guid.NewGuid().ToString("n")[..8];
            c.Name = Trim(c.Name, 60, "Cue");
            c.Notes = (c.Notes ?? "").Length > 400 ? c.Notes![..400] : c.Notes ?? "";
            if (c.Fade is { } f) c.Fade = Math.Clamp(f, 0, 30);
        }
        foreach (var m in Macros)
        {
            if (string.IsNullOrWhiteSpace(m.Id) || !ids.Add(m.Id)) m.Id = Guid.NewGuid().ToString("n")[..8];
            m.Name = Trim(m.Name, 30, "Macro");
            m.Key = m.Key is >= 1 and <= 12 ? m.Key : 0;
            m.Steps ??= [];
            if (m.Steps.Count > 32) m.Steps = m.Steps[..32];
            foreach (var s in m.Steps)
                s.Value = s.Action == MacroAction.Wait ? Math.Clamp(s.Value, 0, 60) : Math.Clamp(s.Value, Dsp.Db.Floor, 10);
        }
        // One key per macro.
        foreach (var g in Macros.Where(m => m.Key > 0).GroupBy(m => m.Key))
            foreach (var extra in g.Skip(1)) extra.Key = 0;
        if (CurrentCueId is not null && Cues.All(c => c.Id != CurrentCueId)) CurrentCueId = null;
    }

    private static string Trim(string? s, int max, string fallback)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return fallback;
        return s.Length > max ? s[..max] : s;
    }

    public int CurrentIndex => CurrentCueId is null ? -1 : Cues.FindIndex(c => c.Id == CurrentCueId);
    public Cue? Next => Cues.ElementAtOrDefault(CurrentIndex + 1);
}

/// <summary>A short list of the show for the screens.</summary>
public sealed record BackupInfo(string File, string Name, DateTime SavedUtc, long Bytes, string Reason);
