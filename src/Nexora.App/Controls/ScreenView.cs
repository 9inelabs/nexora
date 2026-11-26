using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nexora.Core.Config;
using Nexora.Core.Model;

namespace Nexora.App.Controls;

/// <summary>
/// Draws one output screen. The same control is used for the audience screen, the Stage Monitor, the
/// Broadcast picture and every preview, so a preview always looks exactly like the screen.
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

    /// <summary>Show the safe-area and text-box guides (theme editing previews only).</summary>
    public static readonly StyledProperty<bool> ShowGuidesProperty =
        AvaloniaProperty.Register<ScreenView, bool>(nameof(ShowGuides));

    /// <summary>Play videos and cameras for real. Only the output windows do; previews show a still.</summary>
    public static readonly StyledProperty<bool> LiveVideoProperty =
        AvaloniaProperty.Register<ScreenView, bool>(nameof(LiveVideo));

    /// <summary>A real output window (not a preview): in Rehearsal mode the audience sees only the logo.</summary>
    public static readonly StyledProperty<bool> IsOutputProperty =
        AvaloniaProperty.Register<ScreenView, bool>(nameof(IsOutput));

    public static readonly StyledProperty<StageLayout?> StageLayoutProperty =
        AvaloniaProperty.Register<ScreenView, StageLayout?>(nameof(StageLayout));

    /// <summary>Camera only, without words (the "clean" recording).</summary>
    public static readonly StyledProperty<bool> CleanProperty =
        AvaloniaProperty.Register<ScreenView, bool>(nameof(Clean));

    public ScreenFrame? Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public ScreenKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Branding? Branding { get => GetValue(BrandingProperty); set => SetValue(BrandingProperty, value); }
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
    public string? ScreenName { get => GetValue(ScreenNameProperty); set => SetValue(ScreenNameProperty, value); }
    public bool ShowGuides { get => GetValue(ShowGuidesProperty); set => SetValue(ShowGuidesProperty, value); }
    public bool LiveVideo { get => GetValue(LiveVideoProperty); set => SetValue(LiveVideoProperty, value); }
    public bool IsOutput { get => GetValue(IsOutputProperty); set => SetValue(IsOutputProperty, value); }
    public StageLayout? StageLayout { get => GetValue(StageLayoutProperty); set => SetValue(StageLayoutProperty, value); }
    public bool Clean { get => GetValue(CleanProperty); set => SetValue(CleanProperty, value); }

    public const double W = 1920, H = 1080;
    private static readonly IBrush OutputBlack = new SolidColorBrush(Color.Parse("#0C0C0E"));
    private static readonly IBrush OutputAccent = new SolidColorBrush(Color.Parse("#E9C77E"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8A8D94"));
    private static readonly IBrush Panel2 = new SolidColorBrush(Color.Parse("#17181C"));
    private static readonly FontFamily Ui = new("avares://Nexora/Assets/Fonts#Manrope");
    private static readonly FontFamily Display = new("avares://Nexora/Assets/Fonts#Sora");
    private static readonly FontFamily Mono = new("avares://Nexora/Assets/Fonts#JetBrains Mono");

    private readonly Panel _canvas;
    private string _signature = "";
    private ScreenFrame? _shown;
    private TextBlock? _clock;
    private DispatcherTimer? _ticker;
    private readonly List<OverlayRenderer.CountdownParts> _countdowns = [];
    private readonly List<MinisterRenderer.Parts> _ministers = [];
    private readonly List<Action> _releases = [];
    private int _announcementIndex = -1;
    private readonly List<(TextBlock Text, MediaPlay Play)> _mediaLeft = [];

    private static string? _logoKey;
    private static Bitmap? _logo;

    public ScreenView()
    {
        _canvas = new Panel { Width = W, Height = H, Background = OutputBlack, ClipToBounds = true };
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };
        ClipToBounds = true;
    }

    /// <summary>The 1920×1080 canvas itself (the recorder draws it into frames).</summary>
    public Control Canvas => _canvas;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FrameProperty || change.Property == KindProperty || change.Property == BrandingProperty ||
            change.Property == ScreenNameProperty || change.Property == StageLayoutProperty || change.Property == CleanProperty ||
            change.Property == IsOutputProperty)
            Rebuild();
        else if (change.Property == ShowGuidesProperty || change.Property == LiveVideoProperty)
        {
            _signature = "";
            Rebuild();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _ticker.Start();
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _ticker?.Stop();
        _ticker = null;
        ReleaseVideo();
        _signature = "";
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        if (_clock is not null) _clock.Text = ClockText(DateTime.Now);
        foreach (var c in _countdowns) OverlayRenderer.Update(c, now);
        foreach (var m in _ministers) m.Update(now);
        foreach (var (text, play) in _mediaLeft) text.Text = MediaTimeText(play, now);
        // An announcement loop moves on by itself.
        if (_announcementIndex >= 0 && CurrentAnnouncement(Frame) is { } a && a.Current(now).Index != _announcementIndex) Rebuild();
    }

    private string ClockText(DateTime local) => (StageLayout ?? StageLayout.Default).Use24Hour ? local.ToString("HH:mm") : local.ToString("h:mm tt");

    private void ReleaseVideo()
    {
        foreach (var r in _releases) r();
        _releases.Clear();
    }

    // ---------- Building ----------

    private void Rebuild()
    {
        if (!this.IsAttachedToVisualTree() && _canvas.Children.Count > 0) return;
        var frame = Frame;
        var branding = Branding ?? Core.Model.Branding.Default;
        var sig = Signature(frame, branding);
        if (sig == _signature && _canvas.Children.Count > 0) return;
        _signature = sig;
        _clock = null;
        _countdowns.Clear();
        _ministers.Clear();
        _mediaLeft.Clear();
        _announcementIndex = -1;
        var previous = _shown;
        _shown = frame;

        // Hold on to the old pictures until the new ones are acquired, so a shared decoder is never closed and reopened.
        var oldReleases = _releases.ToList();
        _releases.Clear();

        var next = new Panel { Width = W, Height = H };
        // Output windows play video backgrounds; one decoder per loop is shared, so a new verse does not restart it.
        ScriptureRenderer.VideoLoop = LiveVideo ? LoopLayer : null;
        try
        {
            if (Kind == ScreenKind.Stage) BuildStage(next, frame, branding);
            else if (IsOutput && frame?.Rehearsal == true && Kind == ScreenKind.Audience) next.Children.Add(LogoScreen(branding));
            else BuildAudience(next, frame, branding, Kind == ScreenKind.Broadcast);
        }
        finally { ScriptureRenderer.VideoLoop = null; }

        // The church logo in the corner of the recording and stream.
        if (Kind == ScreenKind.Broadcast && branding.CornerWatermark && !Clean && frame?.Master != MasterMode.Black && LogoBitmap(branding) is { } mark)
            next.Children.Add(new Image
            {
                Source = mark, Width = 150, Height = 84, Opacity = 0.7, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 48, 56, 0),
            });

        foreach (var r in oldReleases) r();

        var old = _canvas.Children.ToList();
        var (kind, ms, fadeOldOnly) = TransitionFor(previous, frame);
        if (!Animate || old.Count == 0 || kind == TransitionKind.Cut || ms < 20)
        {
            _canvas.Children.Clear();
            _canvas.Children.Add(next);
            return;
        }

        var duration = TimeSpan.FromMilliseconds(ms);
        var ease = new CubicEaseOut();
        if (fadeOldOnly)
        {
            // Lyrics animate their own words in: the new slide sits underneath and the old one fades away.
            _canvas.Children.Insert(0, next);
            foreach (var o in old)
            {
                o.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = ease }];
                Dispatcher.UIThread.Post(() => o.Opacity = 0, DispatcherPriority.Render);
            }
        }
        else
        {
            next.Opacity = 0;
            next.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = ease }];
            if (kind == TransitionKind.Slide)
            {
                var shift = new TranslateTransform(90, 0)
                {
                    Transitions = [new DoubleTransition { Property = TranslateTransform.XProperty, Duration = duration, Easing = ease }],
                };
                next.RenderTransform = shift;
                Dispatcher.UIThread.Post(() => shift.X = 0, DispatcherPriority.Render);
            }
            _canvas.Children.Add(next);
            Dispatcher.UIThread.Post(() => next.Opacity = 1, DispatcherPriority.Render);
        }
        DispatcherTimer.RunOnce(() =>
        {
            foreach (var o in old) _canvas.Children.Remove(o);
        }, duration + TimeSpan.FromMilliseconds(60));
    }

    /// <summary>How to change from what was shown to what is coming: the newest change decides.</summary>
    private (TransitionKind Kind, int Ms, bool FadeOldOnly) TransitionFor(ScreenFrame? before, ScreenFrame? after)
    {
        var oldItems = before?.Items ?? [];
        var newItems = after?.Items ?? [];
        var oldMedia = oldItems.Select(i => i.Content.Media).FirstOrDefault(m => m is not null);
        var newMedia = newItems.Select(i => i.Content.Media).FirstOrDefault(m => m is not null);
        if (newMedia is not null && newMedia.Id != oldMedia?.Id) return (TransitionKind.Fade, newMedia.FadeInMs, false);
        if (oldMedia is not null && newMedia is null) return (TransitionKind.Fade, oldMedia.FadeOutMs, false);

        var oldCam = oldItems.Select(i => i.Content.Camera).FirstOrDefault(c => c is not null);
        var newCam = newItems.Select(i => i.Content.Camera).FirstOrDefault(c => c is not null);
        if (newCam is not null && oldCam is not null && CameraFeeds.Key(newCam) != CameraFeeds.Key(oldCam))
            return (newCam.Transition, newCam.TransitionMs, false);

        var changed = newItems.Where(i => !oldItems.Any(o => o.Sequence == i.Sequence && o.SourceKey == i.SourceKey)).OrderByDescending(i => i.Sequence).FirstOrDefault();
        if (Kind != ScreenKind.Stage && changed?.Content.Lyrics is { } ly)
            return ly.Animation == LyricAnimation.Cut ? (TransitionKind.Cut, 0, false) : (TransitionKind.Fade, Math.Clamp(ly.AnimationMs / 2, 120, 400), true);
        if (Kind != ScreenKind.Stage && changed?.Content.Announcement is { } an) return (an.Transition, 500, false);
        if (Kind != ScreenKind.Stage && changed?.Content.LowerThird is not null) return (TransitionKind.Cut, 0, false); // it animates itself
        var theme = Kind == ScreenKind.Audience ? newItems.LastOrDefault(i => i.Content.Scripture is not null)?.Content.Scripture?.Theme : null;
        return (theme?.Transition ?? TransitionKind.Fade, Math.Clamp(theme?.TransitionMs ?? 220, 0, 3000), false);
    }

    private string Signature(ScreenFrame? f, Branding b)
    {
        var items = f is null ? "" : string.Join("|", f.Items.Select(i => $"{i.Sequence}:{i.Layer}:{i.SourceKey}:{Hash(i.Content)}"));
        var ann = CurrentAnnouncement(f)?.Current(DateTime.UtcNow).Index ?? -1;
        var stage = Kind == ScreenKind.Stage ? $"{StageLayout}/{f?.Booth}" : "";
        return $"{Kind}/{ScreenName}/{f?.Master}/{f?.Rehearsal}/{IsOutput}/{Clean}/{items}/{ann}/{stage}/{b.ChurchName}/{b.LogoLine}/{b.LogoWhenIdle}/{b.CornerWatermark}/{b.LogoPngBase64?.Length}/{b.LogoPngBase64?.GetHashCode()}";
    }

    private static string Hash(SlideContent c) =>
        System.Text.Json.JsonSerializer.Serialize(c, Nexora.Core.Protocol.Wire.Options).GetHashCode().ToString();

    private static AnnouncementShow? CurrentAnnouncement(ScreenFrame? f) =>
        f?.Items.LastOrDefault(i => i.Content.Announcement is not null)?.Content.Announcement;

    // ---------- Audience and Broadcast ----------

    private void BuildAudience(Panel root, ScreenFrame? frame, Branding branding, bool broadcast)
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
            if (branding.LogoWhenIdle && !broadcast) root.Children.Add(LogoScreen(branding));
            return;
        }
        var now = DateTime.UtcNow;

        // Camera, then media, then words, then overlays.
        var cam = items.LastOrDefault(i => i.Layer == Layer.Camera);
        if (cam is not null) root.Children.Add(CameraLayer(cam.Content));

        var media = items.LastOrDefault(i => i.Content.Media is not null)?.Content.Media;
        if (media is not null) root.Children.Add(MediaLayer(media, now));
        if (Clean) return;

        var hasPicture = cam is not null || media is not null;
        var holdText = media is { Media.Kind: MediaKind.Video, ShowText: false };

        foreach (var i in items.Where(i => i.Layer is Layer.Scripture or Layer.Lyrics))
        {
            if (holdText) break;
            var c = i.Content;
            if (c.Logo) root.Children.Add(LogoScreen(branding));
            else if (c.Scripture is { } sc)
            {
                var slide = broadcast ? sc with { Theme = LyricRenderer.AsLowerThird(sc.Theme) } : sc;
                root.Children.Add(ScriptureRenderer.Build(slide, hasPicture || broadcast, ShowGuides));
            }
            else if (c.Lyrics is { } ly) root.Children.Add(LyricRenderer.Build(ly, hasPicture, broadcast, Animate));
            else if (!c.IsBlank && c.Text.Length > 0) root.Children.Add(PlainText(i, hasPicture || broadcast));
        }

        // Overlays: announcements (full screen), then countdowns, then lower thirds on top.
        var overlays = items.Where(i => i.Layer == Layer.Overlays).ToList();
        var ann = overlays.LastOrDefault(i => i.Content.Announcement is not null)?.Content.Announcement;
        if (ann is not null && ann.Current(now) is ({ } index, { } annSlide))
        {
            _announcementIndex = index;
            root.Children.Add(OverlayRenderer.Announcement(annSlide, ann.Theme));
        }
        foreach (var o in overlays)
        {
            if (o.Content.Countdown is { } cd)
            {
                switch (cd.AudienceLook)
                {
                    case CountdownLook.FullScreen when !broadcast:
                    {
                        var p = OverlayRenderer.CountdownFull(cd, now, branding);
                        _countdowns.Add(p);
                        root.Children.Add(p.Root);
                        break;
                    }
                    case CountdownLook.Badge:
                    case CountdownLook.FullScreen:
                    {
                        var p = OverlayRenderer.CountdownBadge(cd, now);
                        _countdowns.Add(p);
                        root.Children.Add(p.Root);
                        break;
                    }
                }
            }
            else if (o.Content.Countdown is null && o.Section == SectionKind.Countdown && o.Content.Points is null && o.Content.Text.Length > 0)
                root.Children.Add(OverlayRenderer.CountdownBadge(new CountdownShow(o.Content.Caption ?? "Countdown", CountdownMode.Duration, false, 0, null, 0, false, CountdownLook.Badge, ZeroAction.None), now).Root);
        }
        var lowers = overlays.Where(i => i.Content.LowerThird is not null && !i.Content.LowerThird.Expired(now)).ToList();
        if (lowers.Count > 0)
        {
            var lt = lowers[^1].Content.LowerThird!;
            root.Children.Add(OverlayRenderer.LowerThird(lt, Animate));
        }
        else
        {
            // A caption sent without a style (older terminals): the house bar.
            var plain = overlays.LastOrDefault(i => i.Section == SectionKind.LowerThirds && i.Content.LowerThird is null && i.Content.Text.Length > 0);
            if (plain is not null)
                root.Children.Add(OverlayRenderer.LowerThird(new LowerThirdShow(plain.Content.Text, plain.Content.Caption ?? "", null,
                    LowerThirdStyle.Bar, "#E9C77E", OverlayAnimation.Fade, 0, now), false));
        }
    }

    private Control? LoopLayer(string? file)
    {
        if (BackgroundLoops.PathOf(file) is null) return null;
        var feed = BackgroundLoops.Acquire(file!);
        _releases.Add(() => BackgroundLoops.Release(file!));
        var panel = new Panel { Width = W, Height = H };
        // The still sits underneath until the first frame arrives.
        if (MediaImages.Get(Path.ChangeExtension(file!, ".jpg")) is { } still)
            panel.Children.Add(new Image { Source = still, Stretch = Stretch.UniformToFill, Width = W, Height = H });
        panel.Children.Add(new VideoView { Feed = feed, Fill = true, Width = W, Height = H });
        return panel;
    }

    private Control CameraLayer(SlideContent c)
    {
        if (c.Camera is { } feed && LiveVideo)
        {
            var f = CameraFeeds.Acquire(feed);
            _releases.Add(() => CameraFeeds.Release(feed));
            return new Panel
            {
                Width = W, Height = H, Background = Brushes.Black,
                Children = { new VideoView { Feed = f, Fill = true, Width = W, Height = H } },
            };
        }
        return CameraStill(c.Camera?.Label ?? c.Text, c.Camera?.Kind);
    }

    private Control MediaLayer(MediaPlay p, DateTime now)
    {
        var root = new Panel { Width = W, Height = H, Background = Brushes.Black };
        if (p.Media.Kind == MediaKind.Image)
        {
            if (PictureOf(p.Media.File) is { } bmp) root.Children.Add(new Image { Source = bmp, Stretch = Stretch.Uniform, Width = W, Height = H });
            return root;
        }
        if (LiveVideo)
        {
            var f = MediaSync.Acquire(p);
            _releases.Add(() => MediaSync.Release(p));
            root.Children.Add(new VideoView { Feed = f, Fill = false, Width = W, Height = H, HoldOnLoss = TimeSpan.Zero });
            if (f.Problem is { } problem) root.Children.Add(Caption(problem));
            return root;
        }
        // Previews: the thumbnail and the time left.
        if (p.Media.Thumbnail is { } th && PictureOf(th) is { } thumb)
            root.Children.Add(new Image { Source = thumb, Stretch = Stretch.Uniform, Width = W, Height = H, Opacity = 0.85 });
        var left = new TextBlock { Text = MediaTimeText(p, now), FontFamily = Mono, FontSize = 54, Foreground = Brushes.White };
        _mediaLeft.Add((left, p));
        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#D90C0C0E")),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(36, 22),
            Margin = new Thickness(0, 0, 0, 80),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "▶  " + p.Media.Title, FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 40, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                    new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = left },
                },
            },
        });
        return root;
    }

    private static string MediaTimeText(MediaPlay p, DateTime now)
    {
        if (p.Media.Kind == MediaKind.Image) return "Picture";
        var state = p.Playing ? "" : "Paused  ";
        return p.Loop ? state + "Looping" : state + "-" + CountdownShow.Format((int)Math.Ceiling(p.Left(now)));
    }

    private static Bitmap? PictureOf(string file)
    {
        if (file.Contains('/') || file.Contains('\\') || file.Contains("..")) return null;
        var path = Path.Combine(AppPaths.VideosDir, file);
        if (!File.Exists(path)) return MediaImages.Get(file);
        return Thumbs.Get(path);
    }

    private static Control Caption(string text) => new Border
    {
        Background = new SolidColorBrush(Color.Parse("#E60C0C0E")),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(36, 20),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontFamily = Ui, FontSize = 36, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 1400 },
    };

    private static Control PlainText(LayerItem item, bool compact)
    {
        var sp = new StackPanel
        {
            Spacing = 26, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = compact ? VerticalAlignment.Bottom : VerticalAlignment.Center,
            Margin = compact ? new Thickness(0, 0, 0, 90) : new Thickness(0), MaxWidth = 1640,
        };
        sp.Children.Add(new TextBlock
        {
            Text = item.Content.Text, FontFamily = Ui, FontWeight = FontWeight.SemiBold,
            FontSize = compact ? 58 : 80, LineHeight = compact ? 74 : 104, Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (!string.IsNullOrWhiteSpace(item.Content.Caption))
            sp.Children.Add(new TextBlock
            {
                Text = item.Content.Caption!.ToUpperInvariant(), FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 30,
                LetterSpacing = 5, Foreground = OutputAccent, HorizontalAlignment = HorizontalAlignment.Center,
            });
        if (!compact) return sp;
        return new Panel
        {
            Children =
            {
                new Border { Background = new SolidColorBrush(Color.Parse("#B30C0C0E")), VerticalAlignment = VerticalAlignment.Bottom, Height = 420 },
                sp,
            },
        };
    }

    /// <summary>What a preview shows in place of a live camera: colour bars with the camera's name.</summary>
    private static Control CameraStill(string label, CameraSourceKind? kind)
    {
        var grid = new Grid();
        string[] bars = ["#5A5A5A", "#5A5A10", "#105A5A", "#105A10", "#5A105A", "#5A1010", "#10105A"];
        foreach (var _ in bars) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var i = 0; i < bars.Length; i++)
        {
            var r = new Border { Background = new SolidColorBrush(Color.Parse(bars[i])) };
            Grid.SetColumn(r, i);
            grid.Children.Add(r);
        }
        var how = kind switch { CameraSourceKind.Ndi => "NDI", CameraSourceKind.Device => "Capture card", CameraSourceKind.File => "Test video", _ => "Camera" };
        var tag = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E60C0C0E")),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(40, 22),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 90, 0, 0),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = how.ToUpperInvariant(), FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 24, LetterSpacing = 4, Foreground = OutputAccent, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = label, FontFamily = Mono, FontSize = 44, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        Grid.SetColumnSpan(tag, bars.Length);
        grid.Children.Add(tag);
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
        return new Panel { Width = W, Height = H, Background = OutputBlack, Children = { sp } };
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

    // ---------- Stage Monitor: plain, high contrast, readable from the stage ----------

    private void BuildStage(Panel root, ScreenFrame? frame, Branding branding)
    {
        if (frame?.Master == MasterMode.Black) return;
        var layout = StageLayout ?? StageLayout.Default;
        var k = Math.Clamp(layout.TextScale, 0.6, 1.8);
        var now = DateTime.UtcNow;
        var items = frame?.Items ?? [];

        // The Minister Timer: the whole screen, or a band across the top with the words underneath.
        var minister = items.LastOrDefault(i => i.Content.Minister is not null)?.Content.Minister;
        if (minister is not null)
        {
            if (minister.Look == MinisterLook.FullScreen)
            {
                var full = MinisterRenderer.FullScreen(minister, layout.Use24Hour, now);
                _ministers.Add(full);
                root.Children.Add(full.Root);
                return;
            }
            var bar = MinisterRenderer.Bar(minister, layout.Use24Hour, now);
            _ministers.Add(bar);
            var outer = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Width = W, Height = H };
            outer.Children.Add(bar.Root);
            var host = new Panel();
            Grid.SetRow(host, 1);
            outer.Children.Add(host);
            root.Children.Add(outer);
            root = host;
        }

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(80, minister is null ? 54 : 30, 80, 60) };
        root.Children.Add(grid);

        // Top bar: clock, countdown, screen name.
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        if (layout.Clock)
        {
            _clock = new TextBlock
            {
                Text = ClockText(DateTime.Now), FontFamily = Mono, FontWeight = FontWeight.Medium, FontSize = 72 * k,
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            };
            top.Children.Add(_clock);
        }
        var countdown = items.LastOrDefault(i => i.Content.Countdown is not null)?.Content.Countdown;
        if (layout.Countdown && countdown is not null)
        {
            var time = new TextBlock { Text = countdown.Text(now), FontFamily = Mono, FontWeight = FontWeight.Bold, FontSize = 96 * k, Foreground = OutputAccent, HorizontalAlignment = HorizontalAlignment.Center };
            _countdowns.Add(new OverlayRenderer.CountdownParts(time, time, countdown, OutputAccent));
            OverlayRenderer.Update(_countdowns[^1], now);
            var cd = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = countdown.Label.ToUpperInvariant(), FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 24 * k, LetterSpacing = 3, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center },
                    time,
                },
            };
            Grid.SetColumn(cd, 1);
            top.Children.Add(cd);
        }
        var name = new TextBlock
        {
            Text = (ScreenName ?? "Stage Monitor").ToUpperInvariant(), FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 22,
            LetterSpacing = 3, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(name, 2);
        top.Children.Add(name);
        grid.Children.Add(top);

        // A message from the media desk ("5 minutes", "Wrap up").
        if (layout.Booth && !string.IsNullOrWhiteSpace(frame?.Booth))
        {
            var booth = new Border
            {
                Background = OutputAccent, CornerRadius = new CornerRadius(14), Padding = new Thickness(36, 16), Margin = new Thickness(0, 24, 0, 0),
                Child = new TextBlock
                {
                    Text = frame!.Booth, FontFamily = Ui, FontWeight = FontWeight.ExtraBold, FontSize = 54 * k,
                    Foreground = OutputBlack, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                },
            };
            Grid.SetRow(booth, 1);
            grid.Children.Add(booth);
        }

        var points = layout.SermonPoints ? items.LastOrDefault(i => i.Content.Points is not null)?.Content.Points : null;
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions(points is { Points.Count: > 0 } ? "*,40,0.55*" : "*"), Margin = new Thickness(0, 34, 0, 0) };
        Grid.SetRow(body, 2);
        grid.Children.Add(body);

        var main = new StackPanel { Spacing = 44, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(main);

        if (frame?.Master == MasterMode.Logo) main.Children.Add(StageLine("ON SCREEN", "Logo", null, k));
        else
        {
            var words = items.Where(i => i.Layer is Layer.Scripture or Layer.Lyrics or Layer.Media || i.Content.Announcement is not null || i.Content.LowerThird is not null)
                .Where(i => !i.Content.IsBlank).ToList();
            if (words.Count == 0)
                main.Children.Add(new TextBlock { Text = branding.ChurchName, FontFamily = Display, FontSize = 54, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center });
            foreach (var i in words.TakeLast(2))
            {
                var label = i.Content.Lyrics is { } ly ? $"{ly.SongTitle} · {ly.PartLabel}".ToUpperInvariant()
                    : i.Content.Media is { } md ? (md.Media.Kind == MediaKind.Video ? "VIDEO PLAYING" : "PICTURE")
                    : Sections.Get(i.Section).Title.ToUpperInvariant();
                var text = i.Content.Media is { } m ? m.Media.Title
                    : i.Content.Announcement?.Current(now).Slide?.Title ?? i.Content.LowerThird?.Name ?? i.Content.Text;
                if (i.Content.Logo) text = "Logo";
                main.Children.Add(StageLine(label, text, i.Content.Lyrics is null ? i.Content.Caption : null, k));
                if (layout.Next && !string.IsNullOrWhiteSpace(i.Content.Next))
                    main.Children.Add(StageNext(i.Content.Next!, k));
                if (i.Content.Media is { } mp && mp.Media.Kind == MediaKind.Video)
                {
                    var left = new TextBlock { Text = MediaTimeText(mp, now), FontFamily = Mono, FontSize = 64 * k, Foreground = OutputAccent };
                    _mediaLeft.Add((left, mp));
                    main.Children.Add(left);
                }
            }
        }

        if (points is { Points.Count: > 0 })
        {
            var list = new StackPanel { Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
            list.Children.Add(new TextBlock { Text = "SERMON POINTS", FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 22 * k, LetterSpacing = 3, Foreground = Dim });
            for (var n = 0; n < points.Points.Count; n++)
            {
                var current = n == points.Current;
                list.Children.Add(new Border
                {
                    Background = current ? Panel2 : Brushes.Transparent,
                    BorderBrush = current ? OutputAccent : Brushes.Transparent,
                    BorderThickness = new Thickness(6, 0, 0, 0),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(20, 10),
                    Child = new TextBlock
                    {
                        Text = $"{n + 1}. {points.Points[n]}", FontFamily = Ui, FontWeight = current ? FontWeight.Bold : FontWeight.Medium,
                        FontSize = (current ? 40 : 32) * k, Foreground = current ? Brushes.White : n < points.Current ? Dim : new SolidColorBrush(Color.Parse("#C8CAD0")),
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }
            Grid.SetColumn(list, 2);
            body.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = new Border { Width = 640, Child = list } }.Also(v => Grid.SetColumn(v, 2)));
        }
    }

    private static Control StageLine(string label, string text, string? caption, double k)
    {
        var sp = new StackPanel { Spacing = 12 };
        sp.Children.Add(new TextBlock { Text = label, FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 24 * k, LetterSpacing = 3, Foreground = OutputAccent });
        sp.Children.Add(new TextBlock
        {
            Text = text, FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 80 * k, LineHeight = 100 * k,
            Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxLines = 6, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(caption))
            sp.Children.Add(new TextBlock { Text = caption, FontFamily = Ui, FontWeight = FontWeight.SemiBold, FontSize = 36 * k, Foreground = Dim });
        return sp;
    }

    private static Control StageNext(string text, double k) => new StackPanel
    {
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = "NEXT", FontFamily = Ui, FontWeight = FontWeight.Bold, FontSize = 22 * k, LetterSpacing = 3, Foreground = Dim },
            new TextBlock
            {
                Text = text, FontFamily = Ui, FontWeight = FontWeight.SemiBold, FontSize = 46 * k, LineHeight = 58 * k,
                Foreground = new SolidColorBrush(Color.Parse("#A9ACB3")), TextWrapping = TextWrapping.Wrap, MaxLines = 4,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        },
    };
}

/// <summary>Small pictures from the media list (thumbnails and stills), loaded once.</summary>
public static class Thumbs
{
    private static readonly Dictionary<string, Bitmap?> Cache = new();

    public static Bitmap? Get(string path)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var b)) return b;
            try
            {
                using var fs = File.OpenRead(path);
                b = Bitmap.DecodeToWidth(fs, 1920, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception) { b = null; }
            Cache[path] = b;
            return b;
        }
    }

    public static void Forget(string path)
    {
        lock (Cache) Cache.Remove(path);
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T c, Action<T> a)
    {
        a(c);
        return c;
    }
}
