namespace Nexora.Core.Model;

public enum TextAlign { Left, Center, Right, Justify }

public enum ScriptureLayout { FullScreen, LowerThird }

public enum RefPosition { Above, Below, Hidden }

public enum BackgroundKind { Color, Gradient, Image }

public enum TransitionKind { Cut, Fade, Slide }

public enum VerseNumberStyle { Hidden, Inline, Superscript }

/// <summary>
/// A named look for Scripture on the audience screen. Every size and position is in pixels
/// of a 1920×1080 screen, so it looks the same on any display.
/// </summary>
public sealed record ScriptureTheme
{
    public string Id { get; init; } = "classic";
    public string Name { get; init; } = "Classic";

    // Background
    public BackgroundKind Background { get; init; } = BackgroundKind.Color;
    public string BackgroundColor { get; init; } = "#0C0C0E";
    public string GradientTo { get; init; } = "#1B2B4A";
    public int GradientAngle { get; init; } = 135;
    /// <summary>An uploaded image, by its media file name.</summary>
    public string? ImageId { get; init; }
    /// <summary>0 to 0.9: darkens the background so the words stay readable.</summary>
    public double Darken { get; init; } = 0.35;
    public double Blur { get; init; }
    public string TintColor { get; init; } = "#1B2B4A";
    public double Tint { get; init; }

    // Font
    public string FontFamily { get; init; } = "Manrope";
    public int FontWeight { get; init; } = 600;
    public bool Italic { get; init; }
    public double LetterSpacing { get; init; }
    public double LineSpacing { get; init; } = 1.3;
    public double FontSize { get; init; } = 72;
    public bool AutoFit { get; init; } = true;
    public double MinFontSize { get; init; } = 48;

    // Colour and effects
    public string TextColor { get; init; } = "#FFFFFF";
    public string OutlineColor { get; init; } = "#000000";
    public double OutlineWidth { get; init; }
    public string ShadowColor { get; init; } = "#000000";
    public double ShadowBlur { get; init; } = 18;
    public double ShadowDistance { get; init; } = 4;
    public double ShadowOpacity { get; init; } = 0.6;
    public bool BoxEnabled { get; init; }
    public string BoxColor { get; init; } = "#000000";
    public double BoxOpacity { get; init; } = 0.55;
    public double BoxRadius { get; init; } = 18;

    // Layout
    public ScriptureLayout Layout { get; init; } = ScriptureLayout.FullScreen;
    public double MarginX { get; init; } = 140;
    public double MarginY { get; init; } = 110;
    /// <summary>Height of the text box at the bottom of the screen (lower-third layout).</summary>
    public double LowerThirdHeight { get; init; } = 330;
    public TextAlign Align { get; init; } = TextAlign.Center;
    /// <summary>Percent of the screen kept clear at every edge for projectors that cut the edges.</summary>
    public double SafeArea { get; init; } = 3;

    // Reference line ("John 3:16 (KJV)")
    public RefPosition RefPosition { get; init; } = RefPosition.Below;
    public string RefFontFamily { get; init; } = "Manrope";
    public double RefFontSize { get; init; } = 34;
    public string RefColor { get; init; } = "#E9C77E";
    public bool RefUppercase { get; init; } = true;
    public bool RefShowTranslation { get; init; } = true;

    // Verse numbers
    public VerseNumberStyle VerseNumbers { get; init; } = VerseNumberStyle.Superscript;
    public string VerseNumberColor { get; init; } = "#E9C77E";

    // Transitions
    public TransitionKind Transition { get; init; } = TransitionKind.Fade;
    public int TransitionMs { get; init; } = 300;

    /// <summary>When a camera is live, show this theme as a lower third so the speaker stays visible.</summary>
    public bool LowerThirdWithCamera { get; init; } = true;

    public static ScriptureTheme Classic => new();

    public static IReadOnlyList<ScriptureTheme> BuiltIn =>
    [
        Classic,
        new()
        {
            Id = "sunday-blue", Name = "Sunday Blue", Background = BackgroundKind.Gradient, BackgroundColor = "#0B1426",
            GradientTo = "#1E3A66", GradientAngle = 160, Darken = 0, FontFamily = "Sora", FontWeight = 400, FontSize = 70,
            RefColor = "#9CC3FF", VerseNumberColor = "#9CC3FF",
        },
        new()
        {
            Id = "lower-third", Name = "Lower third", Layout = ScriptureLayout.LowerThird, Background = BackgroundKind.Color,
            BackgroundColor = "#000000", Darken = 0, FontSize = 50, MinFontSize = 38, Align = TextAlign.Left, BoxEnabled = true,
            BoxOpacity = 0.8, BoxColor = "#121316", MarginX = 110, MarginY = 70, RefPosition = RefPosition.Above, RefFontSize = 28,
            ShadowBlur = 0, ShadowDistance = 0, LineSpacing = 1.25,
        },
        new()
        {
            Id = "youth-night", Name = "Youth Night", Background = BackgroundKind.Gradient, BackgroundColor = "#1A0F2E",
            GradientTo = "#4B1D52", GradientAngle = 120, Darken = 0, FontFamily = "Sora", FontWeight = 700, FontSize = 78,
            Align = TextAlign.Left, RefPosition = RefPosition.Above, RefColor = "#FFB86B", VerseNumbers = VerseNumberStyle.Hidden,
            Transition = TransitionKind.Slide, MarginX = 160,
        },
    ];
}

/// <summary>Church-wide Scripture choices, set by the admin.</summary>
public sealed record ScriptureSettings(string DefaultTranslation, string DefaultThemeId)
{
    public static ScriptureSettings Default => new("KJV", "classic");
}

/// <summary>One verse, or the part of a long verse that fits on this slide.</summary>
/// <param name="ThroughVerse">Set when the translation prints several verses as one (NLT Numbers 1:20-21).</param>
public sealed record VerseText(int Chapter, int Verse, string Text, bool ShowNumber = true, int? ThroughVerse = null)
{
    public string Number => ThroughVerse is { } t && t != Verse ? $"{Verse}-{t}" : Verse.ToString();
}

/// <summary>The verses of one translation on a slide. Side-by-side slides have two columns.</summary>
public sealed record ScriptureColumn(string Translation, IReadOnlyList<VerseText> Verses);

/// <summary>A Scripture slide as drawn by the Live Output PC: the words, the reference and the theme.</summary>
public sealed record ScriptureSlide(
    string Reference,
    IReadOnlyList<ScriptureColumn> Columns,
    ScriptureTheme Theme,
    int Part = 1,
    int Parts = 1)
{
    /// <summary>"John 3:16 (KJV)" or "John 3:16 · KJV | NIV", as the theme asks.</summary>
    public string ReferenceLine
    {
        get
        {
            var tr = string.Join(" | ", Columns.Select(c => c.Translation));
            var text = Theme.RefShowTranslation && tr.Length > 0 ? $"{Reference} ({tr})" : Reference;
            return Theme.RefUppercase ? text.ToUpperInvariant() : text;
        }
    }

    public string PlainText => string.Join(" ", Columns.FirstOrDefault()?.Verses.Select(v => v.Text) ?? []);
}

/// <summary>A file the Live Output PC holds and every PC keeps a copy of.</summary>
public sealed record LibraryFile(string Name, string Hash, long Size, string Title);
