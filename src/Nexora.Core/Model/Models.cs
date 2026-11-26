namespace Nexora.Core.Model;

/// <summary>What a user is allowed to do. A user can hold several roles.</summary>
[Flags]
public enum Role
{
    None = 0,
    Admin = 1,
    LiveOutput = 2,
    Scripture = 4,
    Lyrics = 8,
    Camera = 16,
    Announcements = 32,
    LowerThirds = 64,
    Countdown = 128,
    /// <summary>Can open sections and prepare, but nothing they do reaches a screen.</summary>
    Trainee = 256,
}

/// <summary>An operator section that a terminal can open.</summary>
public enum SectionKind
{
    Scripture,
    Lyrics,
    Camera,
    Announcements,
    LowerThirds,
    Countdown,
    /// <summary>Video and pictures played by the Live Output operator (not a terminal section).</summary>
    Media,
    /// <summary>A big green, yellow and red timer on the Stage Monitor for a guest minister.</summary>
    MinisterTimer,
}

/// <summary>Live Output layers, bottom to top (plan section 11).</summary>
public enum Layer
{
    Idle = 1,
    Camera = 2,
    Media = 3,
    Scripture = 4,
    Lyrics = 5,
    Overlays = 6,
}

public enum ScreenKind
{
    /// <summary>Full themed output, for example the LED wall.</summary>
    Audience,
    /// <summary>Plain, high-contrast output read from the stage.</summary>
    Stage,
    /// <summary>The recording and the stream: camera with lyrics and Scripture as lower thirds.</summary>
    Broadcast,
}

/// <summary>How a terminal's section feeds one screen.</summary>
public enum RouteState
{
    NotRouted,
    Live,
    Standby,
}

/// <summary>Master override on a screen, drawn above every layer.</summary>
public enum MasterMode
{
    None,
    Logo,
    Black,
}

public enum ClientKind
{
    Terminal,
    OutputEngine,
}

public sealed record SectionDefinition(
    SectionKind Kind,
    string Title,
    string Description,
    Role Role,
    Layer Layer,
    bool IsText,
    bool Pickable = true);

public static class Sections
{
    public static readonly IReadOnlyList<SectionDefinition> All =
    [
        new(SectionKind.Scripture, "Scripture", "Fast reference lookup, every translation, styled verses.", Role.Scripture, Layer.Scripture, true),
        new(SectionKind.Lyrics, "Song Lyrics", "Song library, setlists and lyric animations.", Role.Lyrics, Layer.Lyrics, true),
        new(SectionKind.Camera, "Camera Switcher", "Switch the cameras and send the feed over NDI.", Role.Camera, Layer.Camera, false),
        new(SectionKind.Announcements, "Announcements", "Notice slides and loops before and after service.", Role.Announcements, Layer.Overlays, false),
        new(SectionKind.LowerThirds, "Lower Thirds", "Minister names and titles over cameras.", Role.LowerThirds, Layer.Overlays, false),
        new(SectionKind.Countdown, "Countdown", "Timers, sermon points and the Stage Monitor.", Role.Countdown, Layer.Overlays, false),
        new(SectionKind.MinisterTimer, "Minister Timer", "A big timer on the Stage Monitor so ministers keep to time.", Role.Countdown, Layer.Overlays, false),
        new(SectionKind.Media, "Media", "Videos and pictures played on Live Output.", Role.LiveOutput, Layer.Media, false, Pickable: false),
    ];

    public static IEnumerable<SectionDefinition> Pickable => All.Where(s => s.Pickable);

    public static SectionDefinition Get(SectionKind kind) => All.First(s => s.Kind == kind);

    public static bool UserMayOpen(Role roles, SectionKind kind) =>
        roles.HasFlag(Role.Admin) || roles.HasFlag(Get(kind).Role);

    public static string SourceKey(string pcId, SectionKind kind) => $"{pcId}:{kind}";
}

public sealed record UserInfo(string Id, string Name, string Initials, Role Roles, string Title)
{
    public bool IsAdmin => Roles.HasFlag(Role.Admin);
    public bool MayRunLiveOutput => Roles.HasFlag(Role.Admin) || Roles.HasFlag(Role.LiveOutput);
    public bool IsTrainee => Roles.HasFlag(Role.Trainee) && !Roles.HasFlag(Role.Admin);

    public static string MakeInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Length >= 2 ? parts[0][..2].ToUpperInvariant() : parts[0].ToUpperInvariant();
        return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}";
    }
}

/// <summary>The church's own branding. A setting, never hard-coded.</summary>
public sealed record Branding(
    string ChurchName,
    string Address,
    string LogoLine,
    string? LogoPngBase64,
    bool ShowOnSignIn,
    bool LogoWhenIdle,
    bool CornerWatermark)
{
    public static Branding Default => new("Your Church", "", "Welcome home", null, true, true, false);

    public string Initials => UserInfo.MakeInitials(ChurchName);
}

/// <summary>A named output screen driven by the Live Output PC.</summary>
public sealed record ScreenInfo(
    string Id,
    string Name,
    ScreenKind Kind,
    /// <summary>0 = show in a normal window (for testing). 1, 2, 3 ... = that Windows display, full screen.</summary>
    int Display,
    /// <summary>When on, pinning Scripture live takes Lyrics down on this screen and the reverse.</summary>
    bool OneTextSource,
    int Order,
    /// <summary>The picture's size in pixels, as the screen or LED processor wants it. 0 = 1920×1080 (16:9).</summary>
    int PixelWidth = 0,
    int PixelHeight = 0,
    /// <summary>The part of the display to fill, in the display's own pixels. Width 0 = the whole display.</summary>
    int AreaX = 0,
    int AreaY = 0,
    int AreaWidth = 0,
    int AreaHeight = 0,
    /// <summary>Shows a test pattern instead of the content, to line the picture up on the wall.</summary>
    bool TestPattern = false,
    /// <summary>
    /// Stretch the picture to the whole window even if the shapes differ. For an LED processor that takes a
    /// 16:9 signal and squeezes it onto a wall of another shape: lay out in the wall's shape, send it stretched.
    /// </summary>
    bool StretchToFill = false)
{
    /// <summary>The layout canvas for this screen: the screen's shape with its shorter side 1080, so text sizes stay the same.</summary>
    public (double Width, double Height) Canvas() => CanvasFor(PixelWidth, PixelHeight);

    public static (double Width, double Height) CanvasFor(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return (1920, 1080);
        var aspect = (double)pixelWidth / pixelHeight;
        aspect = Math.Clamp(aspect, 0.2, 5);
        return aspect >= 1 ? (Math.Round(1080 * aspect), 1080) : (1080, Math.Round(1080 / aspect));
    }

    public bool HasArea => AreaWidth > 0 && AreaHeight > 0;
}

/// <summary>
/// A piece of content a terminal sends. Text and Caption are always filled (the Stage Monitor and
/// the routing panel use them); Scripture carries the verses and theme for the audience look.
/// </summary>
public sealed record SlideContent(
    string Text,
    string? Caption = null,
    string? Label = null,
    ScriptureSlide? Scripture = null,
    LyricSlide? Lyrics = null,
    /// <summary>What comes next (next lyric lines or next verse), for the Stage Monitor.</summary>
    string? Next = null,
    /// <summary>The section asks for the church logo in its place (Lyrics' Logo button).</summary>
    bool Logo = false,
    AnnouncementShow? Announcement = null,
    LowerThirdShow? LowerThird = null,
    CountdownShow? Countdown = null,
    SermonPoints? Points = null,
    MediaPlay? Media = null,
    CameraFeed? Camera = null,
    MinisterTimerShow? Minister = null)
{
    /// <summary>True when the content shows nothing (a blanked lyric slide).</summary>
    public bool IsBlank => Text.Length == 0 && Scripture is null && Lyrics is null && !Logo && Announcement is null &&
        LowerThird is null && Countdown is null && Points is null && Media is null && Camera is null && Minister is null;
}

public sealed record LayerItem(
    Layer Layer,
    string SourceKey,
    string SourceLabel,
    SectionKind Section,
    SlideContent Content,
    /// <summary>True when it follows a pinned terminal live; false when it was taken as a snapshot.</summary>
    bool Follows,
    long Sequence);

public sealed record ScreenFrame(string ScreenId, MasterMode Master, IReadOnlyList<LayerItem> Items, string? Booth = null, bool Rehearsal = false);

/// <summary>Church-wide switches on the Live Output PC.</summary>
public sealed record LiveModes(
    /// <summary>Blocks settings changes, imports and updates while a service runs.</summary>
    bool ServiceMode = false,
    /// <summary>Everything works, but nothing reaches the audience screens.</summary>
    bool Rehearsal = false,
    /// <summary>A short note from the media team on the Stage Monitor ("5 minutes").</summary>
    string? Booth = null);

/// <summary>What the output engine reports: recording, streaming, health and the devices it can see.</summary>
public sealed record EngineStatus(
    bool Recording = false,
    string? RecordingFile = null,
    double RecordingSeconds = 0,
    string? RecordProblem = null,
    bool Streaming = false,
    string StreamState = "Off",
    double StreamKbps = 0,
    int DroppedFrames = 0,
    string? StreamProblem = null,
    double Fps = 0,
    double CpuPercent = 0,
    double DiskFreeGb = 0,
    IReadOnlyList<string>? CameraDevices = null,
    IReadOnlyList<string>? AudioDevices = null,
    IReadOnlyList<string>? NdiSources = null,
    bool NdiAvailable = false,
    bool VideoAvailable = false,
    bool FfmpegAvailable = false,
    string? CameraProblem = null);

public sealed record SourceInfo(
    string Key,
    string PcId,
    string PcName,
    SectionKind Section,
    string? UserName,
    bool Online,
    SlideContent? Current,
    IReadOnlyDictionary<string, RouteState> Routes)
{
    public RouteState RouteOn(string screenId) =>
        Routes.TryGetValue(screenId, out var s) ? s : RouteState.NotRouted;

    public string Label => $"{Sections.Get(Section).Title} · {PcName}";
}

public sealed record ClientInfo(
    string ConnectionId,
    string PcId,
    string PcName,
    ClientKind Kind,
    string? UserName,
    string Address,
    string AppVersion,
    IReadOnlyList<SectionKind> Sections);

public sealed record LiveSnapshot(
    IReadOnlyList<ScreenInfo> Screens,
    IReadOnlyList<SourceInfo> Sources,
    IReadOnlyList<ScreenFrame> Frames,
    IReadOnlyList<ClientInfo> Clients,
    long Version,
    LiveModes? Modes = null,
    EngineStatus? Engine = null)
{
    public static LiveSnapshot Empty => new([], [], [], [], 0);

    public LiveModes CurrentModes => Modes ?? new LiveModes();

    public ScreenFrame? FrameFor(string screenId) => Frames.FirstOrDefault(f => f.ScreenId == screenId);
}
