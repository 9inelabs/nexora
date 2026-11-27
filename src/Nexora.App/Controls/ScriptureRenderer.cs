using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Nexora.Core.Config;
using Nexora.Core.Model;

namespace Nexora.App.Controls;

/// <summary>
/// Draws a Scripture slide on the 1920×1080 canvas with its theme. Used by the audience screen,
/// every preview and the slide splitter, so what the operator sees is what the screen shows.
/// </summary>
public static class ScriptureRenderer
{
    public static double W => CanvasSize.W;
    public static double H => CanvasSize.H;
    private const double ColumnGap = 90;
    private const double BoxPadX = 48, BoxPadY = 34;
    private const double RefGap = 28;

    // ---------- Building ----------

    /// <summary>The whole slide: background and text. <paramref name="overCamera"/> switches to the lower third when the theme asks.</summary>
    public static Control Build(ScriptureSlide slide, bool overCamera, bool showGuides = false)
    {
        var theme = Effective(slide.Theme, overCamera);
        var root = new Panel { Width = W, Height = H, ClipToBounds = true };
        if (theme.Layout == ScriptureLayout.FullScreen && !overCamera) root.Children.Add(Background(theme));

        var area = TextArea(theme);
        var fontSize = FitFontSize(slide, theme, area);
        var content = Content(slide, theme, fontSize, area.Width);

        if (theme.Layout == ScriptureLayout.LowerThird)
        {
            var box = new Border
            {
                Width = area.Width + (theme.BoxEnabled ? 2 * BoxPadX : 0),
                Height = area.Height + (theme.BoxEnabled ? 2 * BoxPadY : 0),
                Padding = theme.BoxEnabled ? new Thickness(BoxPadX, BoxPadY) : default,
                Background = theme.BoxEnabled ? Brush(theme.BoxColor, theme.BoxOpacity) : null,
                CornerRadius = new CornerRadius(theme.BoxRadius),
                Child = new Panel { Children = { content } },
            };
            content.VerticalAlignment = VerticalAlignment.Center;
            Canvas.SetLeft(box, area.X - (theme.BoxEnabled ? BoxPadX : 0));
            Canvas.SetTop(box, area.Y - (theme.BoxEnabled ? BoxPadY : 0));
            root.Children.Add(new Canvas { Width = W, Height = H, Children = { box } });
        }
        else
        {
            Control placed = content;
            if (theme.BoxEnabled)
            {
                placed = new Border
                {
                    Background = Brush(theme.BoxColor, theme.BoxOpacity),
                    CornerRadius = new CornerRadius(theme.BoxRadius),
                    Padding = new Thickness(BoxPadX, BoxPadY),
                    Child = content,
                    HorizontalAlignment = Horizontal(theme.Align),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else
            {
                content.VerticalAlignment = VerticalAlignment.Center;
            }
            var holder = new Panel { Width = area.Width + (theme.BoxEnabled ? 2 * BoxPadX : 0), Height = area.Height + (theme.BoxEnabled ? 2 * BoxPadY : 0), Children = { placed } };
            Canvas.SetLeft(holder, area.X - (theme.BoxEnabled ? BoxPadX : 0));
            Canvas.SetTop(holder, area.Y - (theme.BoxEnabled ? BoxPadY : 0));
            root.Children.Add(new Canvas { Width = W, Height = H, Children = { holder } });
        }

        if (showGuides) root.Children.Add(Guides(theme));
        return root;
    }

    /// <summary>The theme as it is drawn: over a live camera a full-screen theme becomes a lower third.</summary>
    public static ScriptureTheme Effective(ScriptureTheme t, bool overCamera)
    {
        if (!overCamera || !t.LowerThirdWithCamera || t.Layout == ScriptureLayout.LowerThird) return t;
        return t with
        {
            Layout = ScriptureLayout.LowerThird,
            FontSize = Math.Min(t.FontSize, 54),
            MinFontSize = Math.Min(t.MinFontSize, 38),
            BoxEnabled = true,
            BoxOpacity = Math.Max(t.BoxOpacity, 0.75),
        };
    }

    /// <summary>The rectangle the words (and reference line) must fit in, inside the margins and the safe area.</summary>
    public static Rect TextArea(ScriptureTheme t)
    {
        var safeX = W * t.SafeArea / 100;
        var safeY = H * t.SafeArea / 100;
        var x = safeX + t.MarginX;
        var w = Math.Max(400, W - 2 * x);
        if (t.Layout == ScriptureLayout.LowerThird)
        {
            var padY = t.BoxEnabled ? BoxPadY : 0;
            var padX = t.BoxEnabled ? BoxPadX : 0;
            var h = Math.Clamp(t.LowerThirdHeight, 140, H * 0.7) - 2 * padY;
            var y = H - safeY - t.MarginY - h - padY;
            return new Rect(x + padX, y, Math.Max(300, w - 2 * padX), Math.Max(80, h));
        }
        var top = safeY + t.MarginY;
        var height = Math.Max(200, H - 2 * top);
        if (t.BoxEnabled)
            return new Rect(x + BoxPadX, top + BoxPadY, Math.Max(300, w - 2 * BoxPadX), Math.Max(150, height - 2 * BoxPadY));
        return new Rect(x, top, w, height);
    }

    /// <summary>The font size this slide is drawn at: the theme size, or with auto-fit the largest that fits (not below the minimum).</summary>
    public static double FitFontSize(ScriptureSlide slide, ScriptureTheme theme, Rect area)
    {
        if (!theme.AutoFit) return theme.FontSize;
        var size = theme.FontSize;
        var min = Math.Min(theme.MinFontSize, theme.FontSize);
        while (size > min && !Fits(slide.Columns, slide.ReferenceLine, theme, size, area)) size -= 2;
        return Math.Max(size, min);
    }

    /// <summary>True when these columns fit the text area at this font size.</summary>
    public static bool Fits(IReadOnlyList<ScriptureColumn> columns, string referenceLine, ScriptureTheme theme, double fontSize, Rect area)
    {
        var colWidth = ColumnWidth(area.Width, columns.Count);
        var refH = theme.RefPosition == RefPosition.Hidden ? 0 : MeasureHeight(ReferenceText(referenceLine, theme), area.Width) + RefGap;
        var avail = area.Height - refH;
        foreach (var col in columns)
        {
            var tb = VerseBlock(col, theme, fontSize, includeEffects: false);
            if (MeasureHeight(tb, colWidth) > avail + 0.5) return false;
        }
        return true;
    }

    private static double ColumnWidth(double width, int columns) =>
        columns <= 1 ? width : (width - ColumnGap * (columns - 1)) / columns;

    private static double MeasureHeight(Control c, double width)
    {
        c.Measure(new Size(width, double.PositiveInfinity));
        return c.DesiredSize.Height;
    }

    private static StackPanel Content(ScriptureSlide slide, ScriptureTheme theme, double fontSize, double width)
    {
        var stack = new StackPanel { Spacing = RefGap, Width = width };
        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", slide.Columns.Select(_ => "*"))) };
        for (var i = 0; i < slide.Columns.Count; i++)
        {
            var c = WithEffects(slide.Columns[i], theme, fontSize);
            c.Margin = new Thickness(i == 0 ? 0 : ColumnGap / 2, 0, i == slide.Columns.Count - 1 ? 0 : ColumnGap / 2, 0);
            Grid.SetColumn(c, i);
            cols.Children.Add(c);
        }
        var reference = theme.RefPosition == RefPosition.Hidden ? null : ReferenceText(slide.ReferenceLine, theme);
        if (reference is not null && theme.RefPosition == RefPosition.Above) stack.Children.Add(reference);
        stack.Children.Add(cols);
        if (reference is not null && theme.RefPosition == RefPosition.Below) stack.Children.Add(reference);
        return stack;
    }

    private static TextBlock ReferenceText(string text, ScriptureTheme t) => new()
    {
        Text = text,
        FontFamily = Font(t.RefFontFamily),
        FontWeight = FontWeight.Bold,
        FontSize = t.RefFontSize,
        LetterSpacing = t.RefUppercase ? t.RefFontSize * 0.14 : 0,
        Foreground = Brush(t.RefColor),
        TextAlignment = t.Align == TextAlign.Justify ? TextAlignment.Left : Alignment(t.Align),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>The verses with outline and shadow.</summary>
    private static Control WithEffects(ScriptureColumn col, ScriptureTheme t, double fontSize)
    {
        var main = VerseBlock(col, t, fontSize, includeEffects: true);
        if (t.OutlineWidth <= 0.1) return main;

        // An outline drawn as copies of the words around the text, behind it.
        var panel = new Panel();
        var r = Math.Min(t.OutlineWidth, 12);
        const int steps = 16;
        for (var i = 0; i < steps; i++)
        {
            var a = Math.PI * 2 * i / steps;
            var copy = VerseBlock(col, t, fontSize, includeEffects: false, overrideColor: t.OutlineColor);
            copy.RenderTransform = new TranslateTransform(Math.Cos(a) * r, Math.Sin(a) * r);
            panel.Children.Add(copy);
        }
        main.Effect = null;
        panel.Children.Add(main);
        if (t.ShadowBlur > 0 || t.ShadowDistance > 0) panel.Effect = Shadow(t);
        return panel;
    }

    public static TextBlock VerseBlock(ScriptureColumn col, ScriptureTheme t, double fontSize, bool includeEffects, string? overrideColor = null)
    {
        var tb = new TextBlock
        {
            FontFamily = Font(t.FontFamily),
            FontWeight = (FontWeight)Math.Clamp(t.FontWeight, 100, 950),
            FontStyle = t.Italic ? FontStyle.Italic : FontStyle.Normal,
            FontSize = fontSize,
            LineHeight = fontSize * Math.Clamp(t.LineSpacing, 0.8, 3),
            LetterSpacing = t.LetterSpacing,
            Foreground = Brush(overrideColor ?? t.TextColor),
            TextAlignment = Alignment(t.Align),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var numberBrush = Brush(overrideColor ?? t.VerseNumberColor);
        var inlines = new InlineCollection();
        var first = true;
        foreach (var v in col.Verses)
        {
            if (!first) inlines.Add(new Run(" "));
            first = false;
            if (v.ShowNumber && t.VerseNumbers != VerseNumberStyle.Hidden && col.Verses.Count > 0)
            {
                if (t.VerseNumbers == VerseNumberStyle.Superscript)
                    inlines.Add(new Run(v.Number + " ") { FontSize = fontSize * 0.55, BaselineAlignment = BaselineAlignment.Superscript, Foreground = numberBrush, FontWeight = FontWeight.Bold });
                else
                    inlines.Add(new Run(v.Number + " ") { Foreground = numberBrush, FontWeight = FontWeight.Bold });
            }
            inlines.Add(new Run(v.Text));
        }
        tb.Inlines = inlines;
        if (includeEffects && (t.ShadowBlur > 0 || t.ShadowDistance > 0) && t.OutlineWidth <= 0.1) tb.Effect = Shadow(t);
        return tb;
    }

    private static DropShadowEffect Shadow(ScriptureTheme t) => new()
    {
        Color = Color(t.ShadowColor),
        BlurRadius = t.ShadowBlur,
        OffsetX = t.ShadowDistance * 0.7,
        OffsetY = t.ShadowDistance * 0.7,
        Opacity = Math.Clamp(t.ShadowOpacity, 0, 1),
    };

    // ---------- Background ----------

    /// <summary>
    /// Set by an output window while it builds a screen: gives the playing loop for a video background.
    /// Left null, a video background shows its still picture.
    /// </summary>
    public static Func<string?, Control?>? VideoLoop { get; set; }

    public static Control Background(ScriptureTheme t)
    {
        var panel = new Panel { Width = W, Height = H, Background = Brush("#0C0C0E") };
        switch (t.Background)
        {
            case BackgroundKind.Color:
                panel.Background = Brush(t.BackgroundColor);
                break;
            case BackgroundKind.Gradient:
                panel.Background = Gradient(t.BackgroundColor, t.GradientTo, t.GradientAngle);
                break;
            case BackgroundKind.Image:
                if (MediaImages.Get(t.ImageId) is { } bmp)
                {
                    var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill, Width = W, Height = H };
                    if (t.Blur > 0.5)
                    {
                        img.Effect = new BlurEffect { Radius = Math.Min(t.Blur, 60) };
                        // Blur softens the edges: grow the picture a little so no dark rim shows.
                        img.RenderTransform = new ScaleTransform(1.06, 1.06);
                    }
                    panel.Children.Add(img);
                }
                else panel.Background = Brush(t.BackgroundColor);
                break;
            case BackgroundKind.Video:
                // Output windows play the loop; previews show its still, so they stay light.
                if (VideoLoop?.Invoke(t.VideoId) is { } loop) panel.Children.Add(loop);
                else if (t.VideoId is { } v && MediaImages.Get(Path.ChangeExtension(v, ".jpg")) is { } still)
                    panel.Children.Add(new Image { Source = still, Stretch = Stretch.UniformToFill, Width = W, Height = H });
                else panel.Background = Brush(t.BackgroundColor);
                break;
        }
        if (t.Tint > 0.01) panel.Children.Add(new Border { Background = Brush(t.TintColor, Math.Clamp(t.Tint, 0, 1)) });
        if (t.Darken > 0.01) panel.Children.Add(new Border { Background = Brush("#000000", Math.Clamp(t.Darken, 0, 0.95)) });
        return panel;
    }

    private static Control Guides(ScriptureTheme t)
    {
        var safeX = W * t.SafeArea / 100;
        var safeY = H * t.SafeArea / 100;
        var dash = new Avalonia.Collections.AvaloniaList<double> { 6, 6 };
        var canvas = new Canvas { Width = W, Height = H, IsHitTestVisible = false };
        var safe = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = W - 2 * safeX, Height = H - 2 * safeY, Stroke = Brush("#3DD68C", 0.7), StrokeThickness = 3, StrokeDashArray = dash,
        };
        Canvas.SetLeft(safe, safeX);
        Canvas.SetTop(safe, safeY);
        canvas.Children.Add(safe);
        var a = TextArea(t);
        var text = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = a.Width, Height = a.Height, Stroke = Brush("#FFFFFF", 0.25), StrokeThickness = 2, StrokeDashArray = dash,
        };
        Canvas.SetLeft(text, a.X);
        Canvas.SetTop(text, a.Y);
        canvas.Children.Add(text);
        return canvas;
    }

    // ---------- Helpers ----------

    public static readonly string[] BundledFonts = ["Manrope", "Sora", "JetBrains Mono"];

    public static FontFamily Font(string name)
    {
        if (BundledFonts.Contains(name, StringComparer.OrdinalIgnoreCase)) return new FontFamily($"avares://Nexora.UI/Assets/Fonts#{name}");
        return string.IsNullOrWhiteSpace(name) ? new FontFamily("avares://Nexora.UI/Assets/Fonts#Manrope") : new FontFamily(name);
    }

    public static TextAlignment Alignment(TextAlign a) => a switch
    {
        TextAlign.Left => TextAlignment.Left,
        TextAlign.Right => TextAlignment.Right,
        TextAlign.Justify => TextAlignment.Justify,
        _ => TextAlignment.Center,
    };

    private static HorizontalAlignment Horizontal(TextAlign a) => a switch
    {
        TextAlign.Left or TextAlign.Justify => HorizontalAlignment.Left,
        TextAlign.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center,
    };

    public static Color Color(string hex)
    {
        try { return Avalonia.Media.Color.Parse(hex); }
        catch { return Colors.White; }
    }

    public static IBrush Brush(string hex, double opacity = 1)
    {
        var c = Color(hex);
        if (opacity < 1) c = new Color((byte)Math.Round(c.A * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B);
        return new SolidColorBrush(c);
    }

    public static IBrush Gradient(string from, string to, int angle)
    {
        var rad = (angle - 90) * Math.PI / 180;
        var dx = Math.Cos(rad) / 2;
        var dy = Math.Sin(rad) / 2;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5 - dx, 0.5 - dy, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5 + dx, 0.5 + dy, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color(from), 0), new GradientStop(Color(to), 1) },
        };
    }
}

/// <summary>Uploaded pictures, loaded once and kept for every screen and preview.</summary>
public static class MediaImages
{
    private static readonly Dictionary<string, Bitmap?> Cache = new();

    public static string? PathOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains("..")) return null;
        foreach (var dir in new[] { AppPaths.MediaDir, AppPaths.LibraryMediaDir, AppPaths.BuiltInBackgroundsDir })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static Bitmap? Get(string? name)
    {
        if (name is null) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var b) && b is not null) return b;
            var path = PathOf(name);
            if (path is null) return null; // not copied yet: try again next time
            try
            {
                using var fs = File.OpenRead(path);
                b = Bitmap.DecodeToWidth(fs, 1920, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception)
            {
                b = null;
            }
            Cache[name] = b;
            return b;
        }
    }
}
