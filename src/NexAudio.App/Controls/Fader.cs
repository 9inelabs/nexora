using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Nexora.Audio.X32;

namespace NexAudio.App.Controls;

/// <summary>
/// A mixing desk fader with its level meter beside it. It moves like the X32's faders (0 dB about
/// three quarters up), double-click puts it at 0 dB, the mouse wheel moves it 1 dB, and holding
/// Shift moves it finely.
/// </summary>
public sealed class Fader : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Fader, double>(nameof(Value), -90, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<MeterSet?> MetersProperty = AvaloniaProperty.Register<Fader, MeterSet?>(nameof(Meters));
    public static readonly StyledProperty<int> MeterIndexProperty = AvaloniaProperty.Register<Fader, int>(nameof(MeterIndex), -1);
    public static readonly StyledProperty<int> MeterIndexRProperty = AvaloniaProperty.Register<Fader, int>(nameof(MeterIndexR), -1);
    public static readonly StyledProperty<IBrush?> CapBrushProperty = AvaloniaProperty.Register<Fader, IBrush?>(nameof(CapBrush));
    /// <summary>A bigger cap and meter, for the channel workspace.</summary>
    public static readonly StyledProperty<bool> WideProperty = AvaloniaProperty.Register<Fader, bool>(nameof(Wide));

    private static readonly IBrush Groove = new SolidColorBrush(Color.Parse("#0E0F12"));
    private static readonly IBrush CapBody = new SolidColorBrush(Color.Parse("#383B42"));
    private static readonly IBrush CapEdge = new SolidColorBrush(Color.Parse("#4A4E56"));
    private static readonly IPen Grip = new Pen(new SolidColorBrush(Color.Parse("#1E2024")), 1.2);
    private static readonly IBrush Bubble = new SolidColorBrush(Color.Parse("#F2F2F0"));
    private static readonly IBrush BubbleText = new SolidColorBrush(Color.Parse("#121316"));
    private static readonly IBrush CapLine = new SolidColorBrush(Color.Parse("#F2F2F0"));
    private static readonly IBrush Scale = new SolidColorBrush(Color.Parse("#6C6F77"));
    private static readonly IBrush Low = new SolidColorBrush(Color.Parse("#3DD68C"));
    private static readonly IBrush Mid = new SolidColorBrush(Color.Parse("#E2BE76"));
    private static readonly IBrush High = new SolidColorBrush(Color.Parse("#FF5A5F"));
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.Parse("#33FFFFFF")), 1);
    private static readonly Typeface Mono = new(new FontFamily("avares://Nexora.UI/Assets/Fonts#JetBrains Mono"));
    private static readonly (double Db, string Text)[] Marks = [(10, "10"), (5, "5"), (0, "0"), (-5, "5"), (-10, "10"), (-20, "20"), (-30, "30"), (-50, "50"), (-90, "∞")];

    private double Pad => CapH / 2 + 2;
    private bool _dragging;
    private double _startY, _startPos;

    static Fader()
    {
        AffectsRender<Fader>(ValueProperty, CapBrushProperty, MeterIndexProperty, MeterIndexRProperty, WideProperty);
        FocusableProperty.OverrideDefaultValue<Fader>(true);
        CursorProperty.OverrideDefaultValue<Fader>(new Cursor(StandardCursorType.SizeNorthSouth));
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public MeterSet? Meters { get => GetValue(MetersProperty); set => SetValue(MetersProperty, value); }
    public int MeterIndex { get => GetValue(MeterIndexProperty); set => SetValue(MeterIndexProperty, value); }
    public int MeterIndexR { get => GetValue(MeterIndexRProperty); set => SetValue(MeterIndexRProperty, value); }
    public IBrush? CapBrush { get => GetValue(CapBrushProperty); set => SetValue(CapBrushProperty, value); }
    public bool Wide { get => GetValue(WideProperty); set => SetValue(WideProperty, value); }

    private double CapW => Wide ? 44 : 30;
    private double CapH => Wide ? 58 : 44;

    /// <summary>Raised when the person lets go of the fader.</summary>
    public event EventHandler? Released;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MetersProperty)
        {
            if (change.OldValue is MeterSet old) old.Changed -= Redraw;
            if (change.NewValue is MeterSet now && VisualRoot is not null) now.Changed += Redraw;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Meters is { } m)
        {
            m.Changed -= Redraw;
            m.Changed += Redraw;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Meters is { } m) m.Changed -= Redraw;
    }

    private void Redraw() => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual, Avalonia.Threading.DispatcherPriority.Render);

    private static double Pos(double db) => db <= -90 ? 0 : X32Client.DbToFader(db);
    private static double DbAt(double pos) => pos <= 0.0005 ? -90 : Math.Round(X32Client.FaderToDb((float)pos) * 10) / 10;

    private double TrackTop => Pad;
    private double TrackHeight => Math.Max(20, Bounds.Height - 2 * Pad);
    private double YOf(double pos) => TrackTop + TrackHeight * (1 - pos);

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        var grooveX = Math.Round(b.Width * (Wide ? 0.46 : 0.44));
        var labelX = grooveX - CapW / 2 - 3;
        // Scale: a tick on each side of the groove, numbers to the left of the cap.
        foreach (var (db, text) in Marks)
        {
            var y = YOf(Pos(db));
            ctx.DrawLine(db == 0 ? new Pen(CapLine, 1) : TickPen, new Point(labelX + 1, y), new Point(grooveX - 4, y));
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, Wide ? 10 : 9, db == 0 ? CapLine : Scale);
            ctx.DrawText(ft, new Point(labelX - 1 - ft.Width, y - ft.Height / 2));
        }
        var gw = Wide ? 6 : 4;
        ctx.FillRectangle(Groove, new Rect(grooveX - gw / 2.0, TrackTop, gw, TrackHeight), 3);

        // Meter, to the right of the cap.
        if (Meters is { } m && MeterIndex >= 0 && MeterIndex < m.Count)
        {
            var mx = grooveX + CapW / 2 + 4;
            var right = MeterIndexR >= 0 && MeterIndexR < m.Count;
            var w = Wide ? (right ? 6 : 10) : (right ? 4 : 6);
            DrawMeter(ctx, new Rect(mx, TrackTop, w, TrackHeight), m.Levels[MeterIndex], m.Peaks[MeterIndex]);
            if (right) DrawMeter(ctx, new Rect(mx + w + 2, TrackTop, w, TrackHeight), m.Levels[MeterIndexR], m.Peaks[MeterIndexR]);
        }

        // Cap: a solid body with grip lines, and a bright line across its middle at the level.
        var cy = YOf(Pos(Value));
        var cap = new Rect(grooveX - CapW / 2, cy - CapH / 2, CapW, CapH);
        ctx.FillRectangle(CapEdge, cap, 6);
        ctx.FillRectangle(CapBrush ?? CapBody, cap.Deflate(1.5), 5);
        for (var i = 1; i <= 3; i++)
        {
            var dy = i * CapH / 9;
            ctx.DrawLine(Grip, new Point(cap.X + 5, cy - dy), new Point(cap.Right - 5, cy - dy));
            ctx.DrawLine(Grip, new Point(cap.X + 5, cy + dy), new Point(cap.Right - 5, cy + dy));
        }
        var accent = this.TryFindResource("Accent", ActualThemeVariant, out var a) && a is IBrush ab ? ab : CapLine;
        ctx.FillRectangle(_dragging || IsFocused ? accent : CapLine, new Rect(cap.X + 3, cy - 1, cap.Width - 6, 2));

        // While it moves, the level shows beside the cap.
        if (_dragging)
        {
            var txt = new FormattedText(Value <= -90 ? "-∞" : $"{Value:+0.0;-0.0;0.0}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 11, BubbleText);
            var bw = txt.Width + 12;
            var bx = cap.Right + 4 + bw > b.Width ? cap.X - bw - 4 : cap.Right + 4;
            var bubble = new Rect(bx, cy - 10, bw, 20);
            ctx.FillRectangle(Bubble, bubble, 5);
            ctx.DrawText(txt, new Point(bubble.X + 6, cy - txt.Height / 2));
        }
    }

    private static void DrawMeter(DrawingContext ctx, Rect r, float level, float peak)
    {
        ctx.FillRectangle(Groove, r, 1);
        var p = MeterBank.Position(level);
        if (p <= 0) return;
        var top = r.Y + r.Height * (1 - p);
        var y6 = r.Y + r.Height * (1 - MeterBank.Position(0.501f));
        var y18 = r.Y + r.Height * (1 - MeterBank.Position(0.126f));
        using (ctx.PushClip(new Rect(r.X, top, r.Width, r.Bottom - top)))
        {
            ctx.FillRectangle(Low, new Rect(r.X, y18, r.Width, r.Bottom - y18));
            ctx.FillRectangle(Mid, new Rect(r.X, y6, r.Width, y18 - y6));
            ctx.FillRectangle(High, new Rect(r.X, r.Y, r.Width, y6 - r.Y));
        }
        var pp = MeterBank.Position(peak);
        if (pp > 0.02) ctx.FillRectangle(CapLine, new Rect(r.X, r.Y + r.Height * (1 - pp), r.Width, 1));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled) return;
        if (e.ClickCount == 2)
        {
            Value = 0;
            Released?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        var y = e.GetPosition(this).Y;
        var capY = YOf(Pos(Value));
        // Clicking away from the cap jumps it there; grabbing the cap keeps it where it is.
        if (Math.Abs(y - capY) > CapH / 2) Value = DbAt(Math.Clamp(1 - (y - TrackTop) / TrackHeight, 0, 1));
        _dragging = true;
        _startY = y;
        _startPos = Pos(Value);
        Focus();
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var y = e.GetPosition(this).Y;
        var fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1.0;
        var pos = Math.Clamp(_startPos - (y - _startY) / TrackHeight * fine, 0, 1);
        Value = DbAt(pos);
        if (fine < 1)
        {
            _startY = y;
            _startPos = pos;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        Released?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!IsEnabled) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 1.0;
        var now = Value <= -90 ? -60 : Value;
        Value = Math.Clamp(Math.Round((now + Math.Sign(e.Delta.Y) * step) * 10) / 10, -90, 10);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsEnabled) return;
        if (e.Key is Key.Up or Key.Down)
        {
            var now = Value <= -90 ? -60 : Value;
            Value = Math.Clamp(now + (e.Key == Key.Up ? 1 : -1), -90, 10);
            e.Handled = true;
        }
    }
}
