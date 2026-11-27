using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Nexora.App.ViewModels;

namespace Nexora.App.Controls;

/// <summary>
/// A rotary knob with its name above and its value below. Drag up or down to turn it (Shift for fine),
/// use the mouse wheel, or double-click to put it back to its starting point. Position runs 0 to 1000.
/// </summary>
public sealed class Knob : Control
{
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<Knob, double>(nameof(Position), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<Knob, string?>(nameof(Label));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<Knob, string?>(nameof(Text));
    public static readonly StyledProperty<bool> BipolarProperty = AvaloniaProperty.Register<Knob, bool>(nameof(Bipolar));
    public static readonly StyledProperty<ICommand?> ResetCommandProperty = AvaloniaProperty.Register<Knob, ICommand?>(nameof(ResetCommand));
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<Knob, bool>(nameof(IsActive), true);
    /// <summary>A setting to show and change: the knob takes its name, value and reset from it.</summary>
    public static readonly StyledProperty<bool> ShowLabelProperty = AvaloniaProperty.Register<Knob, bool>(nameof(ShowLabel), true);
    public static readonly StyledProperty<SliderParam?> SourceProperty = AvaloniaProperty.Register<Knob, SliderParam?>(nameof(Source));

    private static readonly IBrush Body = new SolidColorBrush(Color.Parse("#26282D"));
    private static readonly IPen BodyRim = new Pen(new SolidColorBrush(Color.Parse("#3A3D44")), 1);
    private static readonly IPen Track = new Pen(new SolidColorBrush(Color.Parse("#2E3137")), 3.5, lineCap: PenLineCap.Round);
    private static readonly IPen TrackThin = new Pen(new SolidColorBrush(Color.Parse("#2E3137")), 2.5, lineCap: PenLineCap.Round);
    private static readonly IBrush Pointer = new SolidColorBrush(Color.Parse("#F2F2F0"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#8A8D95"));
    private static readonly IBrush ValueBrush = new SolidColorBrush(Color.Parse("#F2F2F0"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#6C6F77"));
    private static readonly IBrush Fallback = new SolidColorBrush(Color.Parse("#E2BE76"));
    private static readonly Typeface Ui = new(new FontFamily("avares://Nexora/Assets/Fonts#Manrope"), FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface Mono = new(new FontFamily("avares://Nexora/Assets/Fonts#JetBrains Mono"));

    private const double Start = 135, Sweep = 270;
    private bool _dragging;
    private double _lastY;

    static Knob()
    {
        AffectsRender<Knob>(PositionProperty, LabelProperty, TextProperty, BipolarProperty, IsActiveProperty);
        FocusableProperty.OverrideDefaultValue<Knob>(true);
        CursorProperty.OverrideDefaultValue<Knob>(new Cursor(StandardCursorType.SizeNorthSouth));
    }

    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>The ring grows out from 12 o'clock (gain, pan) instead of from the left.</summary>
    public bool Bipolar { get => GetValue(BipolarProperty); set => SetValue(BipolarProperty, value); }
    public ICommand? ResetCommand { get => GetValue(ResetCommandProperty); set => SetValue(ResetCommandProperty, value); }
    /// <summary>False draws the ring grey (the section is switched off).</summary>
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public SliderParam? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool ShowLabel { get => GetValue(ShowLabelProperty); set => SetValue(ShowLabelProperty, value); }

    private bool _syncing;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            if (change.OldValue is SliderParam o) o.PropertyChanged -= SourceChanged;
            if (change.NewValue is SliderParam n)
            {
                if (VisualRoot is not null) n.PropertyChanged += SourceChanged;
                Sync();
            }
        }
        else if (change.Property == PositionProperty && !_syncing && Source is { } src && Math.Abs(src.Position - Position) > 0.01)
        {
            src.Position = Position;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Source is { } s)
        {
            s.PropertyChanged -= SourceChanged;
            s.PropertyChanged += SourceChanged;
            Sync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Source is { } s) s.PropertyChanged -= SourceChanged;
    }

    private void SourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        if (Source is not { } s) return;
        _syncing = true;
        Position = s.Position;
        Text = s.Text;
        Label = ShowLabel ? s.Label : "";
        ResetCommand = s.ResetCommand;
        _syncing = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(availableSize.Width, 84), Math.Min(availableSize.Height, 104));

    private static Point At(Point c, double r, double deg)
    {
        var a = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
    }

    private static void Arc(DrawingContext ctx, IPen pen, Point c, double r, double from, double to)
    {
        if (Math.Abs(to - from) < 0.5) return;
        if (to < from) (from, to) = (to, from);
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(At(c, r, from), false);
            s.ArcTo(At(c, r, to), new Size(r, r), 0, to - from > 180, SweepDirection.Clockwise);
            s.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var label = new FormattedText((Label ?? "").ToUpperInvariant(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Ui, 9.5, LabelBrush)
        {
            MaxTextWidth = w, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
        };
        if (!string.IsNullOrEmpty(Label)) ctx.DrawText(label, new Point(0, 0));
        var value = new FormattedText(Text ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, w < 50 ? 9 : 11, IsActive ? ValueBrush : Dim)
        {
            MaxTextWidth = w, MaxLineCount = 1, TextAlignment = TextAlignment.Center,
        };

        var top = string.IsNullOrEmpty(Label) ? 0 : label.Height + 4;
        var bottom = h - value.Height - 3;
        var d = Math.Max(16, Math.Min(w - 6, bottom - top));
        var c = new Point(w / 2, top + d / 2);
        var ring = d / 2 - 2;
        var bodyR = ring - (d < 40 ? 4 : 6);

        var t = Math.Clamp(Position / 1000, 0, 1);
        var angle = Start + Sweep * t;
        var thin = d < 40;
        Arc(ctx, thin ? TrackThin : Track, c, ring, Start, Start + Sweep);
        var accent = Paint.Of(this);
        var pen = new Pen(IsActive ? accent : Dim, thin ? 2.5 : 3.5, lineCap: PenLineCap.Round);
        Arc(ctx, pen, c, ring, Bipolar ? Start + Sweep / 2 : Start, angle);

        ctx.DrawEllipse(Body, BodyRim, c, bodyR, bodyR);
        var p1 = At(c, bodyR * 0.25, angle);
        var p2 = At(c, bodyR * 0.86, angle);
        ctx.DrawLine(new Pen(IsActive ? Pointer : Dim, 2.2, lineCap: PenLineCap.Round), p1, p2);

        ctx.DrawText(value, new Point(0, c.Y + d / 2 + 2));
        if (IsFocused) ctx.DrawEllipse(null, new Pen(accent, 1) { DashStyle = DashStyle.Dot }, c, ring + 4, ring + 4);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled) return;
        Focus();
        if (e.ClickCount == 2)
        {
            if (ResetCommand?.CanExecute(null) == true) ResetCommand.Execute(null);
            e.Handled = true;
            return;
        }
        _dragging = true;
        _lastY = e.GetPosition(this).Y;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var y = e.GetPosition(this).Y;
        var fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.15 : 1.0;
        // A full turn is about 240 pixels of mouse travel.
        Position = Math.Clamp(Position - (y - _lastY) / 240 * 1000 * fine, 0, 1000);
        _lastY = y;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!IsEnabled) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 4 : 20;
        Position = Math.Clamp(Position + Math.Sign(e.Delta.Y) * step, 0, 1000);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsEnabled) return;
        if (e.Key is Key.Up or Key.Right) Position = Math.Min(1000, Position + 10);
        else if (e.Key is Key.Down or Key.Left) Position = Math.Max(0, Position - 10);
        else return;
        e.Handled = true;
    }
}
