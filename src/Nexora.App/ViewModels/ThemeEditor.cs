using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.App.Services;
using Nexora.App.Controls;
using Nexora.Core.Model;

namespace Nexora.App.ViewModels;

/// <summary>Every styling control of a Scripture theme, bound to the Style panel. Raises Changed on any edit.</summary>
public sealed partial class ThemeEditor : ObservableObject
{
    private bool _loading;

    public event Action? Changed;

    public string Id { get; private set; } = "classic";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private BackgroundKind _background;
    [ObservableProperty] private string _backgroundColor = "#0C0C0E";
    [ObservableProperty] private string _gradientTo = "#1B2B4A";
    [ObservableProperty] private double _gradientAngle = 135;
    [ObservableProperty] private string? _imageId;
    [ObservableProperty] private string? _videoId;
    [ObservableProperty] private double _darken;
    [ObservableProperty] private double _blur;
    [ObservableProperty] private string _tintColor = "#1B2B4A";
    [ObservableProperty] private double _tint;

    [ObservableProperty] private string _fontFamily = "Manrope";
    [ObservableProperty] private int _fontWeight = 600;
    [ObservableProperty] private bool _italic;
    [ObservableProperty] private double _letterSpacing;
    [ObservableProperty] private double _lineSpacing = 1.3;
    [ObservableProperty] private double _fontSize = 72;
    [ObservableProperty] private bool _autoFit = true;
    [ObservableProperty] private double _minFontSize = 48;

    [ObservableProperty] private string _textColor = "#FFFFFF";
    [ObservableProperty] private string _outlineColor = "#000000";
    [ObservableProperty] private double _outlineWidth;
    [ObservableProperty] private string _shadowColor = "#000000";
    [ObservableProperty] private double _shadowBlur;
    [ObservableProperty] private double _shadowDistance;
    [ObservableProperty] private double _shadowOpacity = 0.6;
    [ObservableProperty] private bool _boxEnabled;
    [ObservableProperty] private string _boxColor = "#000000";
    [ObservableProperty] private double _boxOpacity = 0.55;
    [ObservableProperty] private double _boxRadius = 18;

    [ObservableProperty] private ScriptureLayout _layout;
    [ObservableProperty] private double _marginX = 140;
    [ObservableProperty] private double _marginY = 110;
    [ObservableProperty] private double _lowerThirdHeight = 330;
    [ObservableProperty] private TextAlign _align = TextAlign.Center;
    [ObservableProperty] private double _safeArea = 3;

    [ObservableProperty] private RefPosition _refPosition = RefPosition.Below;
    [ObservableProperty] private string _refFontFamily = "Manrope";
    [ObservableProperty] private double _refFontSize = 34;
    [ObservableProperty] private string _refColor = "#E9C77E";
    [ObservableProperty] private bool _refUppercase = true;
    [ObservableProperty] private bool _refShowTranslation = true;

    [ObservableProperty] private VerseNumberStyle _verseNumbers = VerseNumberStyle.Superscript;
    [ObservableProperty] private string _verseNumberColor = "#E9C77E";

    [ObservableProperty] private TransitionKind _transition = TransitionKind.Fade;
    [ObservableProperty] private double _transitionMs = 300;
    [ObservableProperty] private bool _lowerThirdWithCamera = true;

    /// <summary>Fonts to choose from: NEXORA's own, then every font installed on this PC.</summary>
    public static IReadOnlyList<string> Fonts { get; } = LoadFonts();

    public static IReadOnlyList<WeightChoice> Weights { get; } =
    [
        new(300, "Light"), new(400, "Regular"), new(500, "Medium"), new(600, "Semibold"), new(700, "Bold"), new(800, "Extra bold"), new(900, "Black"),
    ];

    public static IReadOnlyList<string> Swatches { get; } =
        ["#FFFFFF", "#F2EBDD", "#E9C77E", "#FFB86B", "#9CC3FF", "#7FD3B0", "#0C0C0E", "#121316", "#1B2B4A", "#0B1426", "#2A1A3F", "#4B1D52", "#3A2414", "#000000"];

    public WeightChoice? SelectedWeight
    {
        get => Weights.FirstOrDefault(w => w.Weight == FontWeight) ?? Weights[3];
        set { if (value is not null) FontWeight = value.Weight; }
    }

    // Segmented choices as booleans for the toggle buttons.
    public bool BgColor { get => Background == BackgroundKind.Color; set { if (value) Background = BackgroundKind.Color; } }
    public bool BgGradient { get => Background == BackgroundKind.Gradient; set { if (value) Background = BackgroundKind.Gradient; } }
    public bool BgImage { get => Background == BackgroundKind.Image; set { if (value) Background = BackgroundKind.Image; } }
    public bool BgVideo { get => Background == BackgroundKind.Video; set { if (value) Background = BackgroundKind.Video; } }
    public bool IsVideo => Background == BackgroundKind.Video;
    public bool IsGradient => Background == BackgroundKind.Gradient;
    public bool IsImage => Background == BackgroundKind.Image;
    public bool HasImage => !string.IsNullOrEmpty(ImageId);

    public bool LayoutFull { get => Layout == ScriptureLayout.FullScreen; set { if (value) Layout = ScriptureLayout.FullScreen; } }
    public bool LayoutLower { get => Layout == ScriptureLayout.LowerThird; set { if (value) Layout = ScriptureLayout.LowerThird; } }
    public bool IsLowerThird => Layout == ScriptureLayout.LowerThird;

    public bool AlignLeft { get => Align == TextAlign.Left; set { if (value) Align = TextAlign.Left; } }
    public bool AlignCenter { get => Align == TextAlign.Center; set { if (value) Align = TextAlign.Center; } }
    public bool AlignRight { get => Align == TextAlign.Right; set { if (value) Align = TextAlign.Right; } }
    public bool AlignJustify { get => Align == TextAlign.Justify; set { if (value) Align = TextAlign.Justify; } }

    public bool RefAbove { get => RefPosition == RefPosition.Above; set { if (value) RefPosition = RefPosition.Above; } }
    public bool RefBelow { get => RefPosition == RefPosition.Below; set { if (value) RefPosition = RefPosition.Below; } }
    public bool RefHidden { get => RefPosition == RefPosition.Hidden; set { if (value) RefPosition = RefPosition.Hidden; } }

    public bool NumHidden { get => VerseNumbers == VerseNumberStyle.Hidden; set { if (value) VerseNumbers = VerseNumberStyle.Hidden; } }
    public bool NumInline { get => VerseNumbers == VerseNumberStyle.Inline; set { if (value) VerseNumbers = VerseNumberStyle.Inline; } }
    public bool NumSuper { get => VerseNumbers == VerseNumberStyle.Superscript; set { if (value) VerseNumbers = VerseNumberStyle.Superscript; } }

    public bool TrCut { get => Transition == TransitionKind.Cut; set { if (value) Transition = TransitionKind.Cut; } }
    public bool TrFade { get => Transition == TransitionKind.Fade; set { if (value) Transition = TransitionKind.Fade; } }
    public bool TrSlide { get => Transition == TransitionKind.Slide; set { if (value) Transition = TransitionKind.Slide; } }

    public IBrush TextBrush => ScriptureRenderer.Brush(TextColor);
    public IBrush BackgroundBrush => ScriptureRenderer.Brush(BackgroundColor);
    public IBrush GradientBrush => ScriptureRenderer.Brush(GradientTo);
    public IBrush RefBrush => ScriptureRenderer.Brush(RefColor);
    public IBrush OutlineBrush => ScriptureRenderer.Brush(OutlineColor);
    public IBrush ShadowBrush => ScriptureRenderer.Brush(ShadowColor);
    public IBrush BoxBrush => ScriptureRenderer.Brush(BoxColor);
    public IBrush NumberBrush => ScriptureRenderer.Brush(VerseNumberColor);
    public IBrush TintBrush => ScriptureRenderer.Brush(TintColor);

    /// <summary>The pictures and video loops that come with NEXORA.</summary>
    public static IReadOnlyList<BuiltInBackground> BuiltInPictures => BuiltInBackgrounds.Pictures;
    public static IReadOnlyList<BuiltInBackground> BuiltInLoops => BuiltInBackgrounds.Loops;
    public bool HasBuiltInPictures => BuiltInPictures.Count > 0;
    public bool HasBuiltInLoops => BuiltInLoops.Count > 0;

    public string PictureName => ImageId is null ? "No picture chosen" : BuiltInBackgrounds.Find(ImageId)?.Title ?? ImageId;
    public string LoopName => VideoId is null ? "Choose a loop below" : BuiltInBackgrounds.Find(VideoId)?.Title ?? VideoId;

    /// <summary>Uses a built-in picture or loop. Turns up Darken a little if it is off, so the words stay readable.</summary>
    [RelayCommand]
    private void UseBuiltIn(BuiltInBackground? b)
    {
        if (b is null) return;
        if (b.IsVideo) { VideoId = b.File; Background = BackgroundKind.Video; }
        else { ImageId = b.File; Background = BackgroundKind.Image; }
        if (Darken < 0.2) Darken = 0.35;
    }

    public void Load(ScriptureTheme t)
    {
        _loading = true;
        Id = t.Id;
        Name = t.Name;
        Background = t.Background; BackgroundColor = t.BackgroundColor; GradientTo = t.GradientTo; GradientAngle = t.GradientAngle;
        ImageId = t.ImageId; VideoId = t.VideoId; Darken = t.Darken; Blur = t.Blur; TintColor = t.TintColor; Tint = t.Tint;
        FontFamily = t.FontFamily; FontWeight = t.FontWeight; Italic = t.Italic; LetterSpacing = t.LetterSpacing; LineSpacing = t.LineSpacing;
        FontSize = t.FontSize; AutoFit = t.AutoFit; MinFontSize = t.MinFontSize;
        TextColor = t.TextColor; OutlineColor = t.OutlineColor; OutlineWidth = t.OutlineWidth;
        ShadowColor = t.ShadowColor; ShadowBlur = t.ShadowBlur; ShadowDistance = t.ShadowDistance; ShadowOpacity = t.ShadowOpacity;
        BoxEnabled = t.BoxEnabled; BoxColor = t.BoxColor; BoxOpacity = t.BoxOpacity; BoxRadius = t.BoxRadius;
        Layout = t.Layout; MarginX = t.MarginX; MarginY = t.MarginY; LowerThirdHeight = t.LowerThirdHeight; Align = t.Align; SafeArea = t.SafeArea;
        RefPosition = t.RefPosition; RefFontFamily = t.RefFontFamily; RefFontSize = t.RefFontSize; RefColor = t.RefColor;
        RefUppercase = t.RefUppercase; RefShowTranslation = t.RefShowTranslation;
        VerseNumbers = t.VerseNumbers; VerseNumberColor = t.VerseNumberColor;
        Transition = t.Transition; TransitionMs = t.TransitionMs; LowerThirdWithCamera = t.LowerThirdWithCamera;
        _loading = false;
        OnPropertyChanged(string.Empty);
    }

    public ScriptureTheme ToTheme() => new()
    {
        Id = Id, Name = string.IsNullOrWhiteSpace(Name) ? "Theme" : Name.Trim(),
        Background = Background, BackgroundColor = Hex(BackgroundColor), GradientTo = Hex(GradientTo), GradientAngle = (int)GradientAngle,
        ImageId = ImageId, VideoId = VideoId, Darken = Darken, Blur = Blur, TintColor = Hex(TintColor), Tint = Tint,
        FontFamily = FontFamily, FontWeight = FontWeight, Italic = Italic, LetterSpacing = LetterSpacing, LineSpacing = LineSpacing,
        FontSize = FontSize, AutoFit = AutoFit, MinFontSize = Math.Min(MinFontSize, FontSize),
        TextColor = Hex(TextColor), OutlineColor = Hex(OutlineColor), OutlineWidth = OutlineWidth,
        ShadowColor = Hex(ShadowColor), ShadowBlur = ShadowBlur, ShadowDistance = ShadowDistance, ShadowOpacity = ShadowOpacity,
        BoxEnabled = BoxEnabled, BoxColor = Hex(BoxColor), BoxOpacity = BoxOpacity, BoxRadius = BoxRadius,
        Layout = Layout, MarginX = MarginX, MarginY = MarginY, LowerThirdHeight = LowerThirdHeight, Align = Align, SafeArea = SafeArea,
        RefPosition = RefPosition, RefFontFamily = RefFontFamily, RefFontSize = RefFontSize, RefColor = Hex(RefColor),
        RefUppercase = RefUppercase, RefShowTranslation = RefShowTranslation,
        VerseNumbers = VerseNumbers, VerseNumberColor = Hex(VerseNumberColor),
        Transition = Transition, TransitionMs = (int)TransitionMs, LowerThirdWithCamera = LowerThirdWithCamera,
    };

    public void SetId(string id) => Id = id;

    private static string Hex(string s)
    {
        var t = s.Trim();
        if (!t.StartsWith('#')) t = "#" + t;
        return Color.TryParse(t, out _) ? t.ToUpperInvariant() : "#FFFFFF";
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (string.IsNullOrEmpty(e.PropertyName) || _loading) return;
        switch (e.PropertyName)
        {
            case nameof(Background):
                OnPropertyChanged(nameof(BgColor)); OnPropertyChanged(nameof(BgGradient)); OnPropertyChanged(nameof(BgImage)); OnPropertyChanged(nameof(BgVideo));
                OnPropertyChanged(nameof(IsGradient)); OnPropertyChanged(nameof(IsImage)); OnPropertyChanged(nameof(IsVideo));
                break;
            case nameof(Layout):
                OnPropertyChanged(nameof(LayoutFull)); OnPropertyChanged(nameof(LayoutLower)); OnPropertyChanged(nameof(IsLowerThird));
                break;
            case nameof(Align):
                OnPropertyChanged(nameof(AlignLeft)); OnPropertyChanged(nameof(AlignCenter)); OnPropertyChanged(nameof(AlignRight)); OnPropertyChanged(nameof(AlignJustify));
                break;
            case nameof(RefPosition):
                OnPropertyChanged(nameof(RefAbove)); OnPropertyChanged(nameof(RefBelow)); OnPropertyChanged(nameof(RefHidden));
                break;
            case nameof(VerseNumbers):
                OnPropertyChanged(nameof(NumHidden)); OnPropertyChanged(nameof(NumInline)); OnPropertyChanged(nameof(NumSuper));
                break;
            case nameof(Transition):
                OnPropertyChanged(nameof(TrCut)); OnPropertyChanged(nameof(TrFade)); OnPropertyChanged(nameof(TrSlide));
                break;
            case nameof(FontWeight): OnPropertyChanged(nameof(SelectedWeight)); break;
            case nameof(ImageId): OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(PictureName)); break;
            case nameof(VideoId): OnPropertyChanged(nameof(LoopName)); break;
            case nameof(TextColor): OnPropertyChanged(nameof(TextBrush)); break;
            case nameof(BackgroundColor): OnPropertyChanged(nameof(BackgroundBrush)); break;
            case nameof(GradientTo): OnPropertyChanged(nameof(GradientBrush)); break;
            case nameof(RefColor): OnPropertyChanged(nameof(RefBrush)); break;
            case nameof(OutlineColor): OnPropertyChanged(nameof(OutlineBrush)); break;
            case nameof(ShadowColor): OnPropertyChanged(nameof(ShadowBrush)); break;
            case nameof(BoxColor): OnPropertyChanged(nameof(BoxBrush)); break;
            case nameof(VerseNumberColor): OnPropertyChanged(nameof(NumberBrush)); break;
            case nameof(TintColor): OnPropertyChanged(nameof(TintBrush)); break;
        }
        if (e.PropertyName is nameof(Name) or nameof(PictureName) or nameof(LoopName) || e.PropertyName.EndsWith("Brush") || e.PropertyName.StartsWith("Is") || e.PropertyName == nameof(HasImage)) return;
        Changed?.Invoke();
    }

    private static IReadOnlyList<string> LoadFonts()
    {
        var list = new List<string>(ScriptureRenderer.BundledFonts);
        try
        {
            list.AddRange(FontManager.Current.SystemFonts.Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n) && !list.Contains(n, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }
        catch { }
        return list;
    }
}

public sealed record WeightChoice(int Weight, string Name)
{
    public override string ToString() => Name;
}
