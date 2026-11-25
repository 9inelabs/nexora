using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nexora.Core.Model;

namespace Nexora.App.Controls;

/// <summary>
/// Draws a lyric slide with its theme on the 1920×1080 canvas, and plays one of the six text animations.
/// Lines are kept whole where they fit, so the words break where the songwriter broke them.
/// </summary>
public static class LyricRenderer
{
    private const double W = ScreenView.W, H = ScreenView.H;
    private const double BoxPadX = 48, BoxPadY = 34;

    /// <summary>The look of a lyric theme as a lower third (for the camera and the stream).</summary>
    public static ScriptureTheme AsLowerThird(ScriptureTheme t) => t.Layout == ScriptureLayout.LowerThird ? t : t with
    {
        Layout = ScriptureLayout.LowerThird,
        FontSize = Math.Min(t.FontSize, 54),
        MinFontSize = Math.Min(t.MinFontSize, 36),
        BoxEnabled = true,
        BoxOpacity = Math.Max(t.BoxOpacity, 0.75),
        LowerThirdHeight = Math.Min(t.LowerThirdHeight, 280),
        Align = TextAlign.Center,
    };

    public static Control Build(LyricSlide slide, bool overCamera, bool forceLowerThird, bool animate)
    {
        var theme = forceLowerThird ? AsLowerThird(slide.Theme) : ScriptureRenderer.Effective(slide.Theme, overCamera);
        var root = new Panel { Width = W, Height = H, ClipToBounds = true };
        if (theme.Layout == ScriptureLayout.FullScreen && !overCamera && !forceLowerThird) root.Children.Add(ScriptureRenderer.Background(theme));
        if (slide.Lines.Count == 0) return root; // blank: background only

        var area = ScriptureRenderer.TextArea(theme);
        var footer = !string.IsNullOrWhiteSpace(slide.Footer) && theme.RefPosition != RefPosition.Hidden ? FooterText(slide.Footer!, theme) : null;
        var footerH = footer is null ? 0 : Measure(footer, area.Width).Height + 24;
        var size = FitSize(slide.Lines, theme, area.Width, area.Height - footerH, out var wrap);

        var lines = new StackPanel { Spacing = 0, HorizontalAlignment = Horizontal(theme.Align) };
        var animated = new List<Control>();
        foreach (var line in slide.Lines)
        {
            Control lc;
            if (animate && slide.Animation == LyricAnimation.WordByWord && !wrap)
            {
                var words = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = Horizontal(theme.Align) };
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length; i++)
                {
                    var w = Styled(parts[i] + (i < parts.Length - 1 ? " " : ""), theme, size, wrap: false);
                    words.Children.Add(w);
                    animated.Add(w);
                }
                lc = words;
            }
            else
            {
                lc = Styled(line, theme, size, wrap);
                animated.Add(lc);
            }
            lines.Children.Add(lc);
        }

        var stack = new StackPanel { Spacing = 24, VerticalAlignment = VerticalAlignment.Center };
        if (footer is not null && theme.RefPosition == RefPosition.Above) stack.Children.Add(footer);
        stack.Children.Add(lines);
        if (footer is not null && theme.RefPosition == RefPosition.Below) stack.Children.Add(footer);

        Control placed = stack;
        if (theme.BoxEnabled)
            placed = new Border
            {
                Background = ScriptureRenderer.Brush(theme.BoxColor, theme.BoxOpacity),
                CornerRadius = new CornerRadius(theme.BoxRadius),
                Padding = new Thickness(BoxPadX, BoxPadY),
                Child = stack,
                HorizontalAlignment = theme.Layout == ScriptureLayout.LowerThird ? HorizontalAlignment.Stretch : Horizontal(theme.Align),
                VerticalAlignment = VerticalAlignment.Center,
            };
        var holder = new Panel
        {
            Width = area.Width + (theme.BoxEnabled ? 2 * BoxPadX : 0),
            Height = area.Height + (theme.BoxEnabled ? 2 * BoxPadY : 0),
            Children = { placed },
        };
        Canvas.SetLeft(holder, area.X - (theme.BoxEnabled ? BoxPadX : 0));
        Canvas.SetTop(holder, area.Y - (theme.BoxEnabled ? BoxPadY : 0));
        root.Children.Add(new Canvas { Width = W, Height = H, Children = { holder } });

        if (animate) Animate(slide.Animation, Math.Clamp(slide.AnimationMs, 80, 3000), stack, animated);
        return root;
    }

    private static double FitSize(IReadOnlyList<string> lines, ScriptureTheme t, double width, double height, out bool wrap)
    {
        var min = Math.Min(t.MinFontSize, t.FontSize);
        var size = t.FontSize;
        if (!t.AutoFit)
        {
            wrap = true;
            return size;
        }
        for (; size >= min; size -= 2)
        {
            double total = 0, widest = 0;
            foreach (var l in lines)
            {
                var m = Measure(Plain(l, t, size, false), double.PositiveInfinity);
                total += m.Height;
                widest = Math.Max(widest, m.Width);
            }
            if (widest <= width && total <= height) { wrap = false; return size; }
        }
        wrap = true;
        return min;
    }

    private static Size Measure(Control c, double width)
    {
        c.Measure(new Size(width, double.PositiveInfinity));
        return c.DesiredSize;
    }

    private static TextBlock Plain(string text, ScriptureTheme t, double size, bool wrap, string? color = null) => new()
    {
        Text = text,
        FontFamily = ScriptureRenderer.Font(t.FontFamily),
        FontWeight = (FontWeight)Math.Clamp(t.FontWeight, 100, 950),
        FontStyle = t.Italic ? FontStyle.Italic : FontStyle.Normal,
        FontSize = size,
        LineHeight = size * Math.Clamp(t.LineSpacing, 0.8, 3),
        LetterSpacing = t.LetterSpacing,
        Foreground = ScriptureRenderer.Brush(color ?? t.TextColor),
        TextAlignment = ScriptureRenderer.Alignment(t.Align == TextAlign.Justify ? TextAlign.Left : t.Align),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>Text with the theme's outline and shadow.</summary>
    public static Control Styled(string text, ScriptureTheme t, double size, bool wrap)
    {
        var main = Plain(text, t, size, wrap);
        var shadow = t.ShadowBlur > 0 || t.ShadowDistance > 0
            ? new DropShadowEffect
            {
                Color = ScriptureRenderer.Color(t.ShadowColor),
                BlurRadius = t.ShadowBlur,
                OffsetX = t.ShadowDistance * 0.7,
                OffsetY = t.ShadowDistance * 0.7,
                Opacity = Math.Clamp(t.ShadowOpacity, 0, 1),
            }
            : null;
        if (t.OutlineWidth <= 0.1)
        {
            main.Effect = shadow;
            return main;
        }
        var panel = new Panel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var r = Math.Min(t.OutlineWidth, 12);
        const int steps = 12;
        for (var i = 0; i < steps; i++)
        {
            var a = Math.PI * 2 * i / steps;
            var copy = Plain(text, t, size, wrap, t.OutlineColor);
            copy.RenderTransform = new TranslateTransform(Math.Cos(a) * r, Math.Sin(a) * r);
            panel.Children.Add(copy);
        }
        panel.Children.Add(main);
        panel.Effect = shadow;
        return panel;
    }

    private static TextBlock FooterText(string text, ScriptureTheme t) => new()
    {
        Text = text,
        FontFamily = ScriptureRenderer.Font(t.RefFontFamily),
        FontWeight = FontWeight.Medium,
        FontSize = Math.Min(t.RefFontSize, 30),
        Foreground = ScriptureRenderer.Brush(t.RefColor),
        TextAlignment = ScriptureRenderer.Alignment(t.Align == TextAlign.Justify ? TextAlign.Left : t.Align),
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.85,
    };

    private static HorizontalAlignment Horizontal(TextAlign a) => a switch
    {
        TextAlign.Left or TextAlign.Justify => HorizontalAlignment.Left,
        TextAlign.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center,
    };

    // ---------- The six animations ----------

    private static void Animate(LyricAnimation kind, int ms, Control block, List<Control> parts)
    {
        if (kind == LyricAnimation.Cut) return;
        var d = TimeSpan.FromMilliseconds(ms);
        var ease = new CubicEaseOut();
        switch (kind)
        {
            case LyricAnimation.Fade:
                FadeIn(block, d, TimeSpan.Zero, ease);
                break;
            case LyricAnimation.ZoomIn:
            {
                var scale = new ScaleTransform(0.9, 0.9)
                {
                    Transitions =
                    [
                        new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = d, Easing = ease },
                        new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = d, Easing = ease },
                    ],
                };
                block.RenderTransformOrigin = RelativePoint.Center;
                block.RenderTransform = scale;
                FadeIn(block, d, TimeSpan.Zero, ease, () => { scale.ScaleX = 1; scale.ScaleY = 1; });
                break;
            }
            case LyricAnimation.SlideUp:
                for (var i = 0; i < parts.Count; i++) RiseIn(parts[i], d, TimeSpan.FromMilliseconds(i * 60), ease);
                break;
            case LyricAnimation.LineByLine:
                for (var i = 0; i < parts.Count; i++) FadeIn(parts[i], d, TimeSpan.FromMilliseconds(i * ms * 0.7), ease);
                break;
            case LyricAnimation.WordByWord:
            {
                // Spread the words over a little more than the chosen time, whatever their number.
                var step = Math.Clamp(ms * 3.0 / Math.Max(1, parts.Count), 40, 260);
                for (var i = 0; i < parts.Count; i++) FadeIn(parts[i], TimeSpan.FromMilliseconds(Math.Max(120, ms * 0.5)), TimeSpan.FromMilliseconds(i * step), ease);
                break;
            }
        }
    }

    private static void FadeIn(Control c, TimeSpan d, TimeSpan delay, Easing ease, Action? alsoAtStart = null)
    {
        c.Opacity = 0;
        c.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = d, Easing = ease }];
        Start(c, delay, () =>
        {
            c.Opacity = 1;
            alsoAtStart?.Invoke();
        });
    }

    private static void RiseIn(Control c, TimeSpan d, TimeSpan delay, Easing ease)
    {
        var shift = new TranslateTransform(0, 46)
        {
            Transitions = [new DoubleTransition { Property = TranslateTransform.YProperty, Duration = d, Easing = ease }],
        };
        c.RenderTransform = shift;
        FadeIn(c, d, delay, ease, () => shift.Y = 0);
    }

    /// <summary>Runs the change once the control is on screen (so the transition plays), after a delay.</summary>
    private static void Start(Control c, TimeSpan delay, Action change)
    {
        void Go()
        {
            if (delay <= TimeSpan.Zero) Dispatcher.UIThread.Post(change, DispatcherPriority.Render);
            else DispatcherTimer.RunOnce(change, delay);
        }
        if (c.IsAttachedToVisualTree()) Go();
        else c.AttachedToVisualTree += (_, _) => Go();
    }
}

internal static class VisualExtensions
{
    public static bool IsAttachedToVisualTree(this Visual v) => Avalonia.VisualTree.VisualExtensions.GetVisualRoot(v) is not null;
}
