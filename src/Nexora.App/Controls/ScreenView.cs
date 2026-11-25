using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nexora.Core.Model;

namespace Nexora.App.Controls;

/// <summary>
/// Draws one output screen. The same control is used for the audience screen, the
/// Stage Monitor and every preview, so a preview always looks exactly like the screen.
/// Everything is laid out on a fixed 1920×1080 canvas and scaled.
/// </summary>
public sealed class ScreenView : UserControl
{
    public static readonly StyledProperty<ScreenFrame?> FrameProperty =
        AvaloniaProperty.Register<ScreenView, ScreenFrame?>(nameof(Frame));

    public static readonly StyledProperty<ScreenKind> KindProperty =
        AvaloniaProperty.Register<ScreenView, ScreenKind>(nameof(Kind));

    public static readonly StyledProperty<Branding?> BrandingProperty =
        AvaloniaProperty.Register<ScreenView, Branding?>(nameof(Branding));

    public static readonly StyledProperty<bool> AnimateProperty =
        AvaloniaProperty.Register<ScreenView, bool>(nameof(Animate), true);

    public static readonly StyledProperty<string?> ScreenNameProperty =
        AvaloniaProperty.Register<ScreenView, string?>(nameof(ScreenName));

    public ScreenFrame? Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public ScreenKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Branding? Branding { get => GetValue(BrandingProperty); set => SetValue(BrandingProperty, value); }
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
    public string? ScreenName { get => GetValue(ScreenNameProperty); set => SetValue(ScreenNameProperty, value); }

    public const double W = 1920, H = 1080;
    private static readonly IBrush OutputBlack = new SolidColorBrush(Color.Parse("#0C0C0E"));
    private static readonly IBrush OutputAccent = new SolidColorBrush(Color.Parse("#E9C77E"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8A8D94"));
    private static readonly FontFamily Ui = new("avares://Nexora/Assets/Fonts#Manrope");
    private static readonly FontFamily Display = new("avares://Nexora/Assets/Fonts#Sora");
    private static readonly FontFamily Mono = new("avares://Nexora/Assets/Fonts#JetBrains Mono");

    private readonly Panel _canvas;
    private string _signature = "";
    private TextBlock? _clock;
    private DispatcherTimer? _clockTimer;

    private static string? _logoKey;
    private static Bitmap? _logo;

    public ScreenView()
    {
        _canvas = new Panel { Width = W, Height = H, Background = OutputBlack, ClipToBounds = true };
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FrameProperty || change.Property == KindProperty ||
            change.Property == BrandingProperty || change.Property == ScreenNameProperty)
            Rebuild();
        else if (change.Property == ShowGuidesProperty)
        {
            _signature = "";
            Rebuild();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_clock is not null) _clock.Text = DateTime.Now.ToString("h:mm tt");
        });
        _clockTimer.Start();
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _clockTimer?.Stop();
        _clockTimer = null;
    }

    private void Rebuild()
    {
        var frame = Frame;
        var branding = Branding ?? Core.Model.Branding.Default;
        var sig = Signature(frame, branding);
        if (sig == _signature && _canvas.Children.Count > 0) return;
        _signature = sig;
        _clock = null;

        var next = new Panel { Width = W, Height = H };
        if (Kind == ScreenKind.Stage) BuildStage(next, frame, branding);
        else BuildAudience(next, frame, branding);

        var old = _canvas.Children.ToList();
        // A Scripture theme chooses its own transition; everything else cross-fades.
        var theme = Kind == ScreenKind.Audience ? frame?.Items.LastOrDefault(i => i.Content.Scripture is not null)?.Content.Scripture?.Theme : null;
        var kind = theme?.Transition ?? TransitionKind.Fade;
        var ms = Math.Clamp(theme?.TransitionMs ?? 220, 0, 3000);
        if (!Animate || old.Count == 0 || kind == TransitionKind.Cut || ms < 20)
        {
            _canvas.Children.Clear();
            _canvas.Children.Add(next);
            return;
        }

        // New content fades (or slides) in over the old, then the old is removed.
        next.Opacity = 0;
        var duration = TimeSpan.FromMilliseconds(ms);
        next.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = new Avalonia.Animation.Easings.CubicEaseOut() }];
        if (kind == TransitionKind.Slide)
        {
            var shift = new TranslateTransform(90, 0)
            {
                Transitions = [new DoubleTransition { Property = TranslateTransform.XProperty, Duration = duration, Easing = new Avalonia.Animation.Easings.CubicEaseOut() }],
            };
            next.RenderTransform = shift;
            Dispatcher.UIThread.Post(() => shift.X = 0, DispatcherPriority.Render);
        }
        _canvas.Children.Add(next);
        Dispatcher.UIThread.Post(() => next.Opacity = 1, DispatcherPriority.Render);
        DispatcherTimer.RunOnce(() =>
        {
            foreach (var o in old) _canvas.Children.Remove(o);
        }, duration + TimeSpan.FromMilliseconds(60));
    }

    private string Signature(ScreenFrame? f, Branding b)
    {
        var items = f is null ? "" : string.Join("|", f.Items.Select(i => $"{i.Sequence}:{i.Layer}:{i.Content.Text}:{i.Content.Caption}:{ScriptureKey(i.Content.Scripture)}"));
        return $"{Kind}/{ScreenName}/{f?.Master}/{items}/{b.ChurchName}/{b.LogoLine}/{b.LogoWhenIdle}/{b.LogoPngBase64?.Length}/{b.LogoPngBase64?.GetHashCode()}";
    }

    private static string ScriptureKey(ScriptureSlide? s) =>
        s is null ? "" : System.Text.Json.JsonSerializer.Serialize(s, Nexora.Core.Protocol.Wire.Options).GetHashCode().ToString();

    /// <summary>Show the safe-area and text-box guides (theme editing previews only).</summary>
    public static readonly StyledProperty<bool> ShowGuidesProperty =
        AvaloniaProperty.Register<ScreenView, bool>(nameof(ShowGuides));

    public bool ShowGuides { get => GetValue(ShowGuidesProperty); set => SetValue(ShowGuidesProperty, value); }

    // ---------- Audience look ----------

    private void BuildAudience(Panel root, ScreenFrame? frame, Branding branding)
    {
        if (frame?.Master == MasterMode.Black) return;
        if (frame?.Master == MasterMode.Logo)
        {
            root.Children.Add(LogoScreen(branding));
            return;
        }

        var items = frame?.Items ?? [];
        if (items.Count == 0)
        {
            if (branding.LogoWhenIdle) root.Children.Add(LogoScreen(branding));
            return;
        }

        foreach (var cam in items.Where(i => i.Layer == Layer.Camera).TakeLast(1))
            root.Children.Add(TestPattern(cam.Content));

        var hasCam = items.Any(i => i.Layer == Layer.Camera);
        foreach (var sc in items.Where(i => i.Layer is Layer.Scripture or Layer.Lyrics && i.Content.Scripture is not null))
            root.Children.Add(ScriptureRenderer.Build(sc.Content.Scripture!, hasCam, ShowGuides));

        var text = items.Where(i => i.Layer is Layer.Scripture or Layer.Lyrics && i.Content.Scripture is null).ToList();
        if (text.Count > 0)
        {
            var hasCamera = items.Any(i => i.Layer == Layer.Camera);
            var stack = new StackPanel
            {
                Spacing = 70,
                VerticalAlignment = hasCamera ? VerticalAlignment.Bottom : VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = hasCamera ? new Thickness(0, 0, 0, 90) : new Thickness(0),
                MaxWidth = 1640,
            };
            foreach (var t in text) stack.Children.Add(TextBlockFor(t, hasCamera));
            if (hasCamera)
            {
                root.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#B30C0C0E")),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Height = 420,
                });
            }
            root.Children.Add(stack);
        }

        var overlays = items.Where(i => i.Layer == Layer.Overlays).ToList();
        var lowerStack = new StackPanel
        {
            Spacing = 18,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(110, 0, 0, 96),
        };
        foreach (var o in overlays)
        {
            if (o.Section == SectionKind.Countdown) root.Children.Add(CountdownBadge(o.Content));
            else lowerStack.Children.Add(LowerThird(o.Content));
        }
        if (lowerStack.Children.Count > 0) root.Children.Add(lowerStack);
    }

    private static Control TextBlockFor(LayerItem item, bool compact)
    {
        var sp = new StackPanel { Spacing = 26, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(new TextBlock
        {
            Text = item.Content.Text,
            FontFamily = Ui,
            FontWeight = FontWeight.SemiBold,
            FontSize = compact ? 58 : item.Layer == Layer.Lyrics ? 84 : 72,
            LineHeight = compact ? 74 : item.Layer == Layer.Lyrics ? 110 : 96,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (!string.IsNullOrWhiteSpace(item.Content.Caption))
        {
            sp.Children.Add(new TextBlock
            {
                Text = item.Content.Caption!.ToUpperInvariant(),
                FontFamily = Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 30,
                LetterSpacing = 5,
                Foreground = OutputAccent,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        return sp;
    }

    private static Control LowerThird(SlideContent c)
    {
        var sp = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = c.Text, FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 46, Foreground = Brushes.White });
        if (!string.IsNullOrWhiteSpace(c.Caption))
            sp.Children.Add(new TextBlock { Text = c.Caption, FontFamily = Ui, FontWeight = FontWeight.Medium, FontSize = 30, Foreground = OutputAccent });
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E6121316")),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(0, 22, 44, 22),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 30,
                Children =
                {
                    new Border { Width = 8, Background = OutputAccent, CornerRadius = new CornerRadius(0, 4, 4, 0) },
                    sp,
                },
            },
        };
    }

    private static Control CountdownBadge(SlideContent c) => new Border
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
                new TextBlock { Text = (c.Caption ?? "Countdown").ToUpperInvariant(), FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 22, LetterSpacing = 3, Foreground = OutputAccent },
                new TextBlock { Text = c.Text, FontFamily = Mono, FontWeight = FontWeight.Medium, FontSize = 64, Foreground = Brushes.White },
            },
        },
    };

    private static Control TestPattern(SlideContent c)
    {
        var grid = new Grid();
        string[] bars = ["#B4B4B4", "#B4B410", "#10B4B4", "#10B410", "#B410B4", "#B41010", "#1010B4"];
        foreach (var _ in bars) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var i = 0; i < bars.Length; i++)
        {
            var r = new Border { Background = new SolidColorBrush(Color.Parse(bars[i])) };
            Grid.SetColumn(r, i);
            grid.Children.Add(r);
        }
        var label = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E60C0C0E")),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(40, 22),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 90, 0, 0),
            Child = new TextBlock { Text = c.Text, FontFamily = Mono, FontSize = 44, Foreground = Brushes.White },
        };
        Grid.SetColumnSpan(label, bars.Length);
        grid.Children.Add(label);
        return grid;
    }

    private static Control LogoScreen(Branding b)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 28 };
        if (LogoBitmap(b) is { } bmp)
        {
            sp.Children.Add(new Image { Source = bmp, MaxHeight = 260, MaxWidth = 700, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center });
        }
        else
        {
            sp.Children.Add(new Border
            {
                Width = 200, Height = 200, CornerRadius = new CornerRadius(44), Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = b.Initials, FontFamily = Display, FontWeight = FontWeight.SemiBold, FontSize = 92,
                    Foreground = OutputBlack, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        sp.Children.Add(new TextBlock
        {
            Text = b.ChurchName, FontFamily = Display, FontWeight = FontWeight.Regular, FontSize = 76,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 1600,
        });
        if (!string.IsNullOrWhiteSpace(b.LogoLine))
            sp.Children.Add(new TextBlock
            {
                Text = b.LogoLine, FontFamily = Ui, FontWeight = FontWeight.Medium, FontSize = 34,
                Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center,
            });
        return sp;
    }

    public static Bitmap? LogoBitmap(Branding b)
    {
        if (string.IsNullOrEmpty(b.LogoPngBase64)) return null;
        if (_logoKey == b.LogoPngBase64) return _logo;
        try
        {
            using var ms = new MemoryStream(Convert.FromBase64String(b.LogoPngBase64));
            _logo = new Bitmap(ms);
        }
        catch (Exception)
        {
            _logo = null;
        }
        _logoKey = b.LogoPngBase64;
        return _logo;
    }

    // ---------- Stage Monitor look: plain, high contrast, readable from the stage ----------

    private void BuildStage(Panel root, ScreenFrame? frame, Branding branding)
    {
        if (frame?.Master == MasterMode.Black) return;

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(90, 60, 90, 70) };
        root.Children.Add(grid);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        _clock = new TextBlock
        {
            Text = DateTime.Now.ToString("h:mm tt"), FontFamily = Mono, FontWeight = FontWeight.Medium, FontSize = 76,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
        };
        top.Children.Add(_clock);
        var countdown = frame?.Items.LastOrDefault(i => i.Section == SectionKind.Countdown);
        if (countdown is not null)
        {
            var cd = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            cd.Children.Add(new TextBlock { Text = (countdown.Content.Caption ?? "Countdown").ToUpperInvariant(), FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 24, LetterSpacing = 3, Foreground = OutputAccent, HorizontalAlignment = HorizontalAlignment.Center });
            cd.Children.Add(new TextBlock { Text = countdown.Content.Text, FontFamily = Mono, FontWeight = FontWeight.Medium, FontSize = 76, Foreground = OutputAccent, HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetColumn(cd, 1);
            top.Children.Add(cd);
        }
        var name = new TextBlock
        {
            Text = (ScreenName ?? "Stage Monitor").ToUpperInvariant(), FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 22,
            LetterSpacing = 3, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 2);
        top.Children.Add(name);
        grid.Children.Add(top);

        var body = new StackPanel { Spacing = 50, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) };
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        if (frame?.Master == MasterMode.Logo)
        {
            body.Children.Add(StageLine("ON SCREEN", "Logo", null));
            return;
        }

        var items = (frame?.Items ?? []).Where(i => i.Section != SectionKind.Countdown && i.Layer != Layer.Camera).ToList();
        if (items.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = branding.ChurchName, FontFamily = Display, FontSize = 54, Foreground = Dim,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return;
        }
        foreach (var i in items)
            body.Children.Add(StageLine(Sections.Get(i.Section).Title.ToUpperInvariant(), i.Content.Text, i.Content.Caption));
    }

    private static Control StageLine(string label, string text, string? caption)
    {
        var sp = new StackPanel { Spacing = 14 };
        sp.Children.Add(new TextBlock { Text = label, FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 24, LetterSpacing = 3, Foreground = OutputAccent });
        sp.Children.Add(new TextBlock
        {
            Text = text, FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 88, LineHeight = 108,
            Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(caption))
            sp.Children.Add(new TextBlock { Text = caption, FontFamily = Ui, FontWeight = FontWeight.SemiBold, FontSize = 36, Foreground = Dim });
        return sp;
    }
}
