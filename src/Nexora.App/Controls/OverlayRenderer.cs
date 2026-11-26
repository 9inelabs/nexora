using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nexora.Core.Model;

namespace Nexora.App.Controls;

/// <summary>Announcement slides, lower thirds and countdowns on the 1920×1080 canvas.</summary>
public static class OverlayRenderer
{
    private static double W => CanvasSize.W;
    private static double H => CanvasSize.H;
    private static readonly FontFamily Ui = new("avares://Nexora/Assets/Fonts#Manrope");
    private static readonly FontFamily Display = new("avares://Nexora/Assets/Fonts#Sora");
    public static readonly FontFamily Mono = new("avares://Nexora/Assets/Fonts#JetBrains Mono");

    public static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F0B44C"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF5A5F"));

    public static IBrush StateBrush(CountdownState s, IBrush normal) => s switch
    {
        CountdownState.Warning => Warn,
        CountdownState.Zero or CountdownState.Overtime => Red,
        _ => normal,
    };

    // ---------- Announcements ----------

    public static Control Announcement(AnnouncementSlide slide, ScriptureTheme theme)
    {
        var root = new Panel { Width = W, Height = H, ClipToBounds = true };
        var picture = slide.ImageId is { } id ? MediaImages.Get(id) : null;
        if (slide.ImageOnly && picture is not null)
        {
            root.Background = Brushes.Black;
            root.Children.Add(new Image { Source = picture, Stretch = Stretch.Uniform, Width = W, Height = H });
            return root;
        }
        // The slide's own picture replaces the theme's background.
        var bgTheme = picture is not null ? theme with { Background = BackgroundKind.Image, ImageId = slide.ImageId } : theme;
        root.Children.Add(ScriptureRenderer.Background(bgTheme));

        var area = ScriptureRenderer.TextArea(theme with { Layout = ScriptureLayout.FullScreen });
        var stack = new StackPanel { Spacing = 26, VerticalAlignment = VerticalAlignment.Center };
        var titleSize = theme.FontSize;
        var title = LyricRenderer.Styled(slide.Title, theme, titleSize, wrap: true);
        stack.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(slide.DateText))
            stack.Children.Add(new TextBlock
            {
                Text = slide.DateText,
                FontFamily = ScriptureRenderer.Font(theme.RefFontFamily),
                FontWeight = FontWeight.Bold,
                FontSize = Math.Max(28, theme.RefFontSize),
                Foreground = ScriptureRenderer.Brush(theme.RefColor),
                TextAlignment = ScriptureRenderer.Alignment(theme.Align),
                TextWrapping = TextWrapping.Wrap,
            });
        if (!string.IsNullOrWhiteSpace(slide.Details))
            stack.Children.Add(LyricRenderer.Styled(slide.Details, theme with { FontWeight = Math.Min(theme.FontWeight, 500) }, Math.Max(34, titleSize * 0.42), wrap: true));

        // Shrink the whole block when the words do not fit.
        var box = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = new Border { Width = area.Width, Child = stack }, VerticalAlignment = VerticalAlignment.Center };
        Control placed = box;
        if (theme.BoxEnabled)
            placed = new Border
            {
                Background = ScriptureRenderer.Brush(theme.BoxColor, theme.BoxOpacity),
                CornerRadius = new CornerRadius(theme.BoxRadius),
                Padding = new Thickness(48, 40),
                Child = box,
                VerticalAlignment = VerticalAlignment.Center,
            };
        var holder = new Panel { Width = area.Width, Height = area.Height, Children = { placed } };
        Canvas.SetLeft(holder, area.X);
        Canvas.SetTop(holder, area.Y);
        root.Children.Add(new Canvas { Width = W, Height = H, Children = { holder } });
        return root;
    }

    // ---------- Lower thirds ----------

    public static Control LowerThird(LowerThirdShow lt, bool animate)
    {
        var accent = ScriptureRenderer.Brush(lt.Accent);
        Control body = lt.Style switch
        {
            LowerThirdStyle.Card => Card(lt, accent),
            LowerThirdStyle.Minimal => Minimal(lt, accent),
            _ => Bar(lt, accent),
        };
        var holder = new Panel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(110, 0, 0, 96),
            Children = { body },
        };
        if (animate) AnimateIn(body, lt.Animation, Math.Clamp(lt.AnimationMs, 100, 2000));
        return holder;
    }

    private static Control NameBlock(LowerThirdShow lt, IBrush accent, IBrush nameBrush, double nameSize)
    {
        var sp = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = lt.Name, FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = nameSize, Foreground = nameBrush });
        if (!string.IsNullOrWhiteSpace(lt.Title))
            sp.Children.Add(new TextBlock { Text = lt.Title, FontFamily = Ui, FontWeight = FontWeight.Medium, FontSize = nameSize * 0.64, Foreground = accent });
        return sp;
    }

    private static Control? Photo(LowerThirdShow lt, double size)
    {
        if (lt.ImageId is not { } id || MediaImages.Get(id) is not { } bmp) return null;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), ClipToBounds = true,
            Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill },
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static Control Bar(LowerThirdShow lt, IBrush accent)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 30 };
        row.Children.Add(new Border { Width = 8, Background = accent, CornerRadius = new CornerRadius(0, 4, 4, 0) });
        if (Photo(lt, 92) is { } p) row.Children.Add(p);
        row.Children.Add(NameBlock(lt, accent, Brushes.White, 48));
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E6121316")),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(0, 22, 46, 22),
            Child = row,
        };
    }

    private static Control Card(LowerThirdShow lt, IBrush accent)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 28 };
        if (Photo(lt, 120) is { } p) row.Children.Add(p);
        row.Children.Add(NameBlock(lt, accent, new SolidColorBrush(Color.Parse("#111113")), 50));
        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(26),
            Padding = new Thickness(36, 26, 52, 26),
            BorderBrush = accent,
            BorderThickness = new Thickness(0, 0, 0, 8),
            Child = row,
        };
    }

    private static Control Minimal(LowerThirdShow lt, IBrush accent)
    {
        var sp = new StackPanel { Spacing = 10 };
        var name = new TextBlock
        {
            Text = lt.Name, FontFamily = Display, FontWeight = FontWeight.SemiBold, FontSize = 58, Foreground = Brushes.White,
            Effect = new DropShadowEffect { BlurRadius = 18, OffsetX = 2, OffsetY = 2, Opacity = 0.8, Color = Colors.Black },
        };
        sp.Children.Add(name);
        sp.Children.Add(new Border { Height = 5, Width = 120, Background = accent, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3) });
        if (!string.IsNullOrWhiteSpace(lt.Title))
            sp.Children.Add(new TextBlock
            {
                Text = lt.Title, FontFamily = Ui, FontWeight = FontWeight.SemiBold, FontSize = 34, Foreground = Brushes.White,
                Effect = new DropShadowEffect { BlurRadius = 14, OffsetX = 1, OffsetY = 1, Opacity = 0.8, Color = Colors.Black },
            });
        return sp;
    }

    private static void AnimateIn(Control c, OverlayAnimation kind, int ms)
    {
        var d = TimeSpan.FromMilliseconds(ms);
        var ease = new CubicEaseOut();
        c.Opacity = 0;
        c.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(ms * 0.7), Easing = ease }];
        Action final;
        switch (kind)
        {
            case OverlayAnimation.Slide:
            {
                var t = new TranslateTransform(-180, 0) { Transitions = [new DoubleTransition { Property = TranslateTransform.XProperty, Duration = d, Easing = ease }] };
                c.RenderTransform = t;
                final = () => t.X = 0;
                break;
            }
            case OverlayAnimation.Wipe:
            {
                var s = new ScaleTransform(0.02, 1) { Transitions = [new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = d, Easing = ease }] };
                c.RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative);
                c.RenderTransform = s;
                final = () => s.ScaleX = 1;
                break;
            }
            default:
                final = () => { };
                break;
        }
        void Go() => Dispatcher.UIThread.Post(() => { c.Opacity = 1; final(); }, DispatcherPriority.Render);
        if (c.IsAttachedToVisualTree()) Go();
        else c.AttachedToVisualTree += (_, _) => Go();
    }

    // ---------- Countdown ----------

    /// <summary>A countdown and the text block that the screen updates every tick.</summary>
    public sealed record CountdownParts(Control Root, TextBlock Time, CountdownShow Show, IBrush Normal);

    public static CountdownParts CountdownBadge(CountdownShow c, DateTime now)
    {
        var time = new TextBlock { Text = c.Text(now), FontFamily = Mono, FontWeight = FontWeight.Medium, FontSize = 64, Foreground = Brushes.White };
        var root = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E6121316")),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(34, 18),
            Margin = new Thickness(0, 70, 90, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = c.Label.ToUpperInvariant(), FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 22, LetterSpacing = 3, Foreground = new SolidColorBrush(Color.Parse("#E9C77E")) },
                    time,
                },
            },
        };
        time.Foreground = StateBrush(c.State(now), Brushes.White);
        return new CountdownParts(root, time, c, Brushes.White);
    }

    public static CountdownParts CountdownFull(CountdownShow c, DateTime now, Branding branding)
    {
        var time = new TextBlock
        {
            Text = c.Text(now), FontFamily = Mono, FontWeight = FontWeight.Medium, FontSize = 260, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var sp = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock
        {
            Text = c.Label.ToUpperInvariant(), FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 44, LetterSpacing = 10,
            Foreground = new SolidColorBrush(Color.Parse("#E9C77E")), HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp.Children.Add(time);
        sp.Children.Add(new TextBlock
        {
            Text = branding.ChurchName, FontFamily = Display, FontSize = 40, Foreground = new SolidColorBrush(Color.Parse("#8A8D94")),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var root = new Panel
        {
            Width = W, Height = H,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#0C0C0E"), 0), new GradientStop(Color.Parse("#1A1B20"), 1) },
            },
            Children = { sp },
        };
        time.Foreground = StateBrush(c.State(now), Brushes.White);
        return new CountdownParts(root, time, c, Brushes.White);
    }

    public static void Update(CountdownParts p, DateTime now)
    {
        p.Time.Text = p.Show.Text(now);
        p.Time.Foreground = StateBrush(p.Show.State(now), p.Normal);
    }
}
