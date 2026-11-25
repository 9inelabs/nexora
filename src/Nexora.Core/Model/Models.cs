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
    bool IsText);

public static class Sections
{
    public static readonly IReadOnlyList<SectionDefinition> All =
    [
        new(SectionKind.Scripture, "Scripture", "Fast reference lookup, every translation, styled verses.", Role.Scripture, Layer.Scripture, true),
        new(SectionKind.Lyrics, "Song Lyrics", "Song library, setlists and lyric animations.", Role.Lyrics, Layer.Lyrics, true),
        new(SectionKind.Camera, "Camera Switcher", "Switch the cameras and send the feed over NDI.", Role.Camera, Layer.Camera, false),
        new(SectionKind.Announcements, "Announcements", "Notice slides and loops before and after service.", Role.Announcements, Layer.Overlays, false),
        new(SectionKind.LowerThirds, "Lower Thirds", "Minister names and titles over cameras.", Role.LowerThirds, Layer.Overlays, false),
        new(SectionKind.Countdown, "Countdown", "Service and segment timers for screens and stage.", Role.Countdown, Layer.Overlays, false),
    ];

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
    int Order);

/// <summary>A piece of content a terminal sends. In Phase 1 this is a test slide.</summary>
public sealed record SlideContent(string Text, string? Caption = null, string? Label = null);

public sealed record LayerItem(
    Layer Layer,
    string SourceKey,
    string SourceLabel,
    SectionKind Section,
    SlideContent Content,
    /// <summary>True when it follows a pinned terminal live; false when it was taken as a snapshot.</summary>
    bool Follows,
    long Sequence);

public sealed record ScreenFrame(string ScreenId, MasterMode Master, IReadOnlyList<LayerItem> Items);

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
    long Version)
{
    public static LiveSnapshot Empty => new([], [], [], [], 0);

    public ScreenFrame? FrameFor(string screenId) => Frames.FirstOrDefault(f => f.ScreenId == screenId);
}
