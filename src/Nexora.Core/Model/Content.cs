namespace Nexora.Core.Model;

// The content types that sections send to Live Output, beyond Scripture. Each one carries everything
// the Live Output PC needs to draw it, so the screen never waits for a terminal.

// ---------------- Lyrics ----------------

/// <summary>How lyrics come in and go out (plan section 8).</summary>
public enum LyricAnimation { Cut, Fade, SlideUp, LineByLine, WordByWord, ZoomIn }

/// <summary>One lyric slide as drawn by the Live Output PC.</summary>
public sealed record LyricSlide(
    string SongId,
    string SongTitle,
    string PartLabel,
    IReadOnlyList<string> Lines,
    ScriptureTheme Theme,
    LyricAnimation Animation = LyricAnimation.Fade,
    int AnimationMs = 450,
    /// <summary>Copyright line and CCLI number, shown on the first slide when the church asks.</summary>
    string? Footer = null,
    int Index = 0,
    int Count = 0)
{
    public string PlainText => string.Join("\n", Lines);
}

// ---------------- Announcements ----------------

public sealed record AnnouncementSlide(
    string Id,
    string Title,
    string Details,
    string DateText,
    /// <summary>A picture by its media file name: a background for the words, or the whole slide.</summary>
    string? ImageId,
    /// <summary>True for a ready-made graphic from the church's designers: the picture is the slide.</summary>
    bool ImageOnly,
    int Seconds,
    /// <summary>The last day it shows. After this date it leaves the loop by itself.</summary>
    DateOnly? Expires)
{
    public bool IsExpired(DateOnly today) => Expires is { } e && today > e;
}

/// <summary>
/// One announcement, or a loop that moves on by itself. The Live Output PC works out the current
/// slide from its own clock, so the loop keeps running even if the terminal is switched off.
/// </summary>
public sealed record AnnouncementShow(
    IReadOnlyList<AnnouncementSlide> Slides,
    ScriptureTheme Theme,
    bool Loop,
    int StartIndex,
    /// <summary>Set by the Live Output PC when it receives the show.</summary>
    DateTime StartUtc,
    TransitionKind Transition = TransitionKind.Fade)
{
    /// <summary>The slide showing at this moment and when it started.</summary>
    public (int Index, AnnouncementSlide? Slide) Current(DateTime utcNow)
    {
        if (Slides.Count == 0) return (0, null);
        var start = Math.Clamp(StartIndex, 0, Slides.Count - 1);
        if (!Loop) return (start, Slides[start]);
        var total = Slides.Sum(s => Math.Max(2, s.Seconds));
        var t = Math.Max(0, (utcNow - StartUtc).TotalSeconds);
        // Count from the start slide, wrapping round.
        var before = Slides.Take(start).Sum(s => Math.Max(2, s.Seconds));
        var pos = (t + before) % total;
        for (var i = 0; i < Slides.Count; i++)
        {
            var len = Math.Max(2, Slides[i].Seconds);
            if (pos < len) return (i, Slides[i]);
            pos -= len;
        }
        return (Slides.Count - 1, Slides[^1]);
    }
}

// ---------------- Lower thirds ----------------

public enum LowerThirdStyle { Bar, Card, Minimal }

public enum OverlayAnimation { Slide, Fade, Wipe }

/// <summary>A minister's name and title, or any caption, over the picture.</summary>
public sealed record LowerThirdShow(
    string Name,
    string Title,
    string? ImageId,
    LowerThirdStyle Style,
    string Accent,
    OverlayAnimation Animation,
    /// <summary>0 = stay until cleared.</summary>
    int HideAfterSeconds,
    /// <summary>Set by the Live Output PC when it receives the item.</summary>
    DateTime StartUtc,
    int AnimationMs = 500)
{
    public bool Expired(DateTime utcNow) => HideAfterSeconds > 0 && utcNow >= StartUtc.AddSeconds(HideAfterSeconds);
}

public sealed record Person(string Id, string Name, string Title, string? ImageId);

// ---------------- Countdown and stage ----------------

public enum CountdownMode { Duration, ToTime }

/// <summary>How a countdown appears on audience screens. The Stage Monitor always shows it.</summary>
public enum CountdownLook { StageOnly, Badge, FullScreen }

public enum ZeroAction { None, ShowLogo, StopAnnouncements }

public enum CountdownState { Normal, Warning, Zero, Overtime }

/// <summary>
/// A timer that runs on the Live Output PC's own clock: the terminal sends how long is left (or the
/// time of day to count to) and the Live Output PC sets <see cref="EndsUtc"/> from its clock, so the
/// timer stays exact even if the Countdown terminal disconnects.
/// </summary>
public sealed record CountdownShow(
    string Label,
    CountdownMode Mode,
    bool Running,
    /// <summary>Seconds left, used while paused and to start a Duration timer.</summary>
    double RemainingSeconds,
    /// <summary>"09:00" for a ToTime timer, in the Live Output PC's local time.</summary>
    string? ToTime,
    int WarnAtSeconds,
    bool CountUpOvertime,
    CountdownLook AudienceLook,
    ZeroAction AtZero,
    /// <summary>Set by the Live Output PC.</summary>
    DateTime? EndsUtc = null)
{
    public double Remaining(DateTime utcNow) => Running && EndsUtc is { } e ? (e - utcNow).TotalSeconds : RemainingSeconds;

    public CountdownState State(DateTime utcNow)
    {
        var r = Remaining(utcNow);
        if (r <= -1) return CountUpOvertime ? CountdownState.Overtime : CountdownState.Zero;
        if (r <= 0) return CountdownState.Zero;
        return r <= WarnAtSeconds ? CountdownState.Warning : CountdownState.Normal;
    }

    public string Text(DateTime utcNow)
    {
        var r = Remaining(utcNow);
        if (r > -1 || !CountUpOvertime) return Format((int)Math.Ceiling(Math.Max(0, r)));
        return "+" + Format((int)Math.Floor(-r));
    }

    public static string Format(int s) => s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";

    /// <summary>The end time for a ToTime timer: the next time the clock reads that time.</summary>
    public static DateTime? NextLocal(string? hhmm, DateTime localNow)
    {
        if (string.IsNullOrWhiteSpace(hhmm)) return null;
        var t = hhmm.Trim().ToLowerInvariant().Replace(".", ":");
        var pm = t.EndsWith("pm");
        var am = t.EndsWith("am");
        t = t.Replace("am", "").Replace("pm", "").Trim();
        var parts = t.Split(':');
        if (!int.TryParse(parts[0], out var h)) return null;
        var m = parts.Length > 1 && int.TryParse(parts[1], out var mm) ? mm : 0;
        if (pm && h < 12) h += 12;
        if (am && h == 12) h = 0;
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        var target = localNow.Date.AddHours(h).AddMinutes(m);
        if (target <= localNow.AddSeconds(-5)) target = target.AddDays(1);
        return target;
    }
}

public sealed record CountdownPreset(string Id, string Label, CountdownMode Mode, int Seconds, string? ToTime,
    int WarnAtSeconds, bool CountUpOvertime, CountdownLook AudienceLook, ZeroAction AtZero);

/// <summary>The pastor's points for the day, shown only on the Stage Monitor.</summary>
public sealed record SermonPoints(IReadOnlyList<string> Points, int Current);

/// <summary>Which zones the Stage Monitor shows (the admin arranges them).</summary>
public sealed record StageLayout(
    bool Clock = true,
    bool Countdown = true,
    bool Next = true,
    bool SermonPoints = true,
    bool Booth = true,
    double TextScale = 1.0,
    bool Use24Hour = false)
{
    public static StageLayout Default => new();
}

// ---------------- Media (video playback in Live Output) ----------------

public enum MediaKind { Video, Image }

/// <summary>A video or picture in the Live Output PC's media list for the day.</summary>
public sealed record MediaInfo(
    string File,
    string Title,
    MediaKind Kind,
    double DurationSeconds,
    int Width,
    int Height,
    string? Codec,
    /// <summary>Empty when the pre-check found no problem.</summary>
    string? Problem,
    string? Thumbnail);

/// <summary>What the media layer is doing. Times are on the Live Output PC's clock.</summary>
public sealed record MediaPlay(
    MediaInfo Media,
    bool Playing,
    /// <summary>When the video's 0:00 was (or would have been) on the wall clock, while playing.</summary>
    DateTime StartUtc,
    /// <summary>Position while paused or cued.</summary>
    double PausedAt,
    bool Loop,
    int FadeInMs,
    int FadeOutMs,
    int Volume,
    string? AudioDevice,
    /// <summary>Show Scripture and Lyrics over the video anyway.</summary>
    bool ShowText,
    long Id)
{
    public double Position(DateTime utcNow)
    {
        if (!Playing) return PausedAt;
        var p = (utcNow - StartUtc).TotalSeconds;
        if (Loop && Media.DurationSeconds > 0) p %= Media.DurationSeconds;
        return Math.Max(0, p);
    }

    public double Left(DateTime utcNow) => Media.Kind == MediaKind.Image || Loop ? 0 : Math.Max(0, Media.DurationSeconds - Position(utcNow));

    public bool Finished(DateTime utcNow) =>
        Playing && !Loop && Media.Kind == MediaKind.Video && Media.DurationSeconds > 0 && Position(utcNow) >= Media.DurationSeconds;
}

// ---------------- Cameras ----------------

public enum CameraSourceKind
{
    /// <summary>An NDI stream on the network (the Camera Switcher's Program, or an NDI camera).</summary>
    Ndi,
    /// <summary>A capture card or webcam plugged into the Live Output PC (command-only mode).</summary>
    Device,
    /// <summary>A video file played as a camera (for testing without cameras).</summary>
    File,
}

/// <summary>What the camera layer shows.</summary>
public sealed record CameraFeed(string Label, CameraSourceKind Kind, string Source, TransitionKind Transition = TransitionKind.Cut, int TransitionMs = 0);

/// <summary>The house style for lower thirds.</summary>
public sealed record LowerThirdDefaults(LowerThirdStyle Style, string Accent, OverlayAnimation Animation, int HideAfterSeconds)
{
    public static LowerThirdDefaults Default => new(LowerThirdStyle.Bar, "#E9C77E", OverlayAnimation.Slide, 8);
}

/// <summary>Recording and streaming settings, kept on the Live Output PC.</summary>
public sealed record BroadcastSettings(
    string? RecordFolder = null,
    string? AudioDevice = null,
    int Height = 1080,
    int Fps = 30,
    int VideoKbps = 6000,
    string? StreamUrl = null,
    /// <summary>Stored encrypted on the Live Output PC; sent only to its own output engine.</summary>
    string? StreamKey = null,
    int SoundDelayMs = 0,
    bool CleanRecording = false,
    bool StreamingEnabled = true,
    string ServiceName = "Sunday Service");

/// <summary>The built-in looks for Lyrics and Announcements (the same controls as Scripture themes).</summary>
public static class SectionThemes
{
    public static readonly IReadOnlyList<ScriptureTheme> Lyrics =
    [
        new()
        {
            Id = "lyrics-classic", Name = "Worship", FontSize = 80, MinFontSize = 52, FontWeight = 700, LineSpacing = 1.25,
            Darken = 0.3, RefPosition = RefPosition.Below, RefFontSize = 24, RefColor = "#A4A7AE", RefUppercase = false,
            RefShowTranslation = false, VerseNumbers = VerseNumberStyle.Hidden, ShadowBlur = 22, ShadowDistance = 4,
        },
        new()
        {
            Id = "lyrics-warm", Name = "Warm gradient", Background = BackgroundKind.Gradient, BackgroundColor = "#1E1208",
            GradientTo = "#4A2A12", GradientAngle = 140, Darken = 0, FontFamily = "Sora", FontWeight = 600, FontSize = 78,
            MinFontSize = 50, RefPosition = RefPosition.Below, RefFontSize = 24, RefColor = "#E9C77E", RefUppercase = false,
            RefShowTranslation = false, VerseNumbers = VerseNumberStyle.Hidden,
        },
        new()
        {
            Id = "lyrics-lower", Name = "Lower third (camera)", Layout = ScriptureLayout.LowerThird, FontSize = 52, MinFontSize = 38,
            BoxEnabled = true, BoxOpacity = 0.78, BoxColor = "#121316", Align = TextAlign.Center, MarginX = 160, MarginY = 60,
            RefPosition = RefPosition.Hidden, ShadowBlur = 0, ShadowDistance = 0, LowerThirdHeight = 260,
            VerseNumbers = VerseNumberStyle.Hidden, RefShowTranslation = false,
        },
        new()
        {
            Id = "lyrics-outline", Name = "Outline over video", Background = BackgroundKind.Color, BackgroundColor = "#000000",
            Darken = 0, FontSize = 84, MinFontSize = 54, FontWeight = 800, OutlineWidth = 4, OutlineColor = "#000000",
            ShadowBlur = 0, ShadowDistance = 0, RefPosition = RefPosition.Hidden, VerseNumbers = VerseNumberStyle.Hidden,
            RefShowTranslation = false,
        },
    ];

    public static readonly IReadOnlyList<ScriptureTheme> Announcements =
    [
        new()
        {
            Id = "ann-clean", Name = "Clean", Background = BackgroundKind.Gradient, BackgroundColor = "#101114", GradientTo = "#1F2125",
            GradientAngle = 160, Darken = 0.25, FontFamily = "Sora", FontWeight = 700, FontSize = 96, MinFontSize = 56, Align = TextAlign.Left,
            MarginX = 150, MarginY = 120, RefPosition = RefPosition.Below, RefFontSize = 40, RefColor = "#E9C77E", RefUppercase = false,
            RefShowTranslation = false, VerseNumbers = VerseNumberStyle.Hidden, ShadowBlur = 0, ShadowDistance = 0,
        },
        new()
        {
            Id = "ann-photo", Name = "Photo with box", Background = BackgroundKind.Color, BackgroundColor = "#0C0C0E", Darken = 0.2,
            FontFamily = "Sora", FontWeight = 700, FontSize = 84, MinFontSize = 50, Align = TextAlign.Left, BoxEnabled = true,
            BoxOpacity = 0.7, BoxColor = "#0C0C0E", MarginX = 140, MarginY = 140, RefPosition = RefPosition.Below, RefFontSize = 36,
            RefColor = "#E9C77E", RefUppercase = false, RefShowTranslation = false, VerseNumbers = VerseNumberStyle.Hidden,
        },
    ];
}
