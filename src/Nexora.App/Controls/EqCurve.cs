using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace Nexora.App.Controls;

/// <summary>Where the EQ graph sends changes made by dragging its band dots.</summary>
public interface IEqEditor
{
    void MoveBand(int band, double freq, double gain);
    void ScaleQ(int band, double factor);
    void SelectBand(int band);
}

/// <summary>
/// The shape of a strip's filters and EQ, from 20 Hz to 20 kHz, with the spectrum of what is playing
/// behind it when there is one. Dots mark each EQ band: drag one to move the band, use the wheel over it
/// to make it wider or narrower.
/// </summary>
public sealed class EqCurve : Control
{
    public static readonly StyledProperty<FilterSettings?> SettingsProperty = AvaloniaProperty.Register<EqCurve, FilterSettings?>(nameof(Settings));
    public static readonly StyledProperty<float[]?> SpectrumProperty = AvaloniaProperty.Register<EqCurve, float[]?>(nameof(Spectrum));
    public static readonly StyledProperty<double> RangeProperty = AvaloniaProperty.Register<EqCurve, double>(nameof(Range), 18);
    public static readonly StyledProperty<IEqEditor?> EditorProperty = AvaloniaProperty.Register<EqCurve, IEqEditor?>(nameof(Editor));
    public static readonly StyledProperty<int> SelectedBandProperty = AvaloniaProperty.Register<EqCurve, int>(nameof(SelectedBand), -1);
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<EqCurve, bool>(nameof(Compact));
    public static readonly StyledProperty<bool> ShowBandsProperty = AvaloniaProperty.Register<EqCurve, bool>(nameof(ShowBands), true);

    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#0E0F12"));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#1AFFFFFF")), 1);
    private static readonly IPen Zero = new Pen(new SolidColorBrush(Color.Parse("#33FFFFFF")), 1);
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#6C6F77"));
    private static readonly IBrush SpectrumFill = new SolidColorBrush(Color.Parse("#2A3D44"));
    private static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#F2F2F0"));
    private static readonly IBrush DotOff = new SolidColorBrush(Color.Parse("#55595F"));
    private static readonly Typeface Mono = new(new FontFamily("avares://Nexora/Assets/Fonts#JetBrains Mono"));

    private int _drag = -1, _hover = -1;

    static EqCurve()
    {
        AffectsRender<EqCurve>(SettingsProperty, SpectrumProperty, RangeProperty, SelectedBandProperty, CompactProperty, ShowBandsProperty);
    }

    public FilterSettings? Settings { get => GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
    public float[]? Spectrum { get => GetValue(SpectrumProperty); set => SetValue(SpectrumProperty, value); }
    public double Range { get => GetValue(RangeProperty); set => SetValue(RangeProperty, value); }
    public IEqEditor? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public int SelectedBand { get => GetValue(SelectedBandProperty); set => SetValue(SelectedBandProperty, value); }
    /// <summary>A small picture for a tile: no labels and no dots.</summary>
    public bool Compact { get => GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public bool ShowBands { get => GetValue(ShowBandsProperty); set => SetValue(ShowBandsProperty, value); }

    private static double XOf(double f, double w) => w * Math.Log(f / 20) / Math.Log(1000);
    private static double FreqAt(double x, double w) => 20 * Math.Pow(1000, Math.Clamp(x / w, 0, 1));
    private double YOf(double db) => Bounds.Height / 2 - db / Range * (Bounds.Height / 2 - (Compact ? 4 : 12));
    private double DbAt(double y) => (Bounds.Height / 2 - y) / (Bounds.Height / 2 - (Compact ? 4 : 12)) * Range;

    private static bool HasGain(FilterShape s) => s is FilterShape.Peak or FilterShape.LowShelf or FilterShape.HighShelf;

    private Point DotOf(EqBand band) =>
        new(XOf(Math.Clamp(band.Freq, 20, 20000), Bounds.Width), YOf(HasGain(band.Shape) ? Math.Clamp(band.Gain, -Range, Range) : 0));

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(Back, b, 8);
        using var clip = ctx.PushClip(b);
        var w = b.Width;
        var h = b.Height;

        // Spectrum behind (dB from -90 to 0 mapped to the bottom half and up).
        if (Spectrum is { Length: > 0 } sp)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(0, h), true);
                for (var i = 0; i < sp.Length; i++)
                {
                    var f = Analyser.BandCentre(i, sp.Length);
                    var db = Math.Clamp(sp[i], -90, 0);
                    g.LineTo(new Point(XOf(f, w), h - (db + 90) / 90 * h * 0.9));
                }
                g.LineTo(new Point(w, h));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(SpectrumFill, null, geo);
        }

        foreach (var f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
        {
            var x = XOf(f, w);
            if (Compact && h < 50) continue;
            ctx.DrawLine(Grid, new Point(x, 0), new Point(x, h));
            if (Compact) continue;
            var t = new FormattedText(f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 9, Label);
            ctx.DrawText(t, new Point(x + 3, h - t.Height - 2));
        }
        foreach (var db in new[] { -12.0, -6, 6, 12 })
        {
            if (Compact && h < 50) continue;
            ctx.DrawLine(Grid, new Point(0, YOf(db)), new Point(w, YOf(db)));
            if (Compact) continue;
            var t = new FormattedText($"{db:+0;-0}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 9, Label);
            ctx.DrawText(t, new Point(4, YOf(db) - t.Height));
        }
        ctx.DrawLine(Zero, new Point(0, YOf(0)), new Point(w, YOf(0)));

        if (Settings is not { } s) return;
        var accent = this.TryFindResource("Accent", ActualThemeVariant, out var a) && a is IBrush ab ? ab : Dot;
        var tint = accent is ISolidColorBrush sc ? new SolidColorBrush(Color.FromArgb(40, sc.Color.R, sc.Color.G, sc.Color.B)) : accent;

        // The chosen band on its own, shaded, so it is clear what that band is doing.
        var sel = _drag >= 0 ? _drag : SelectedBand;
        if (!Compact && ShowBands && s.Eq.On && sel >= 0 && sel < s.Eq.Bands.Count && s.Eq.Bands[sel].On)
        {
            var only = new FilterSettings(new PassFilter(), new PassFilter(), new EqSettings { On = true, Bands = [s.Eq.Bands[sel].Clone()] });
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(0, YOf(0)), true);
                for (var x = 0.0; x <= w; x += 2)
                    g.LineTo(new Point(x, YOf(Math.Clamp(StripFilters.ResponseDb(only, FreqAt(x, w), 48000), -Range - 6, Range + 6))));
                g.LineTo(new Point(w, YOf(0)));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(tint, null, area);
        }

        var curve = new StreamGeometry();
        using (var g = curve.Open())
        {
            for (var x = 0.0; x <= w; x += 2)
            {
                var db = Math.Clamp(StripFilters.ResponseDb(s, FreqAt(x, w), 48000), -Range - 6, Range + 6);
                var p = new Point(x, YOf(db));
                if (x == 0) g.BeginFigure(p, false);
                else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(accent, Compact ? 1.8 : 2.4), curve);

        if (Compact || !ShowBands || !s.Eq.On) return;
        for (var i = 0; i < s.Eq.Bands.Count; i++)
        {
            var band = s.Eq.Bands[i];
            var p = DotOf(band);
            var chosen = i == sel;
            if (chosen || i == _hover) ctx.DrawEllipse(null, new Pen(accent, 1.5), p, 11, 11);
            ctx.DrawEllipse(chosen ? accent : band.On ? Dot : DotOff, null, p, 7.5, 7.5);
            var t = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10, Back);
            ctx.DrawText(t, new Point(p.X - t.Width / 2, p.Y - t.Height / 2));
        }
        if (sel >= 0 && sel < s.Eq.Bands.Count)
        {
            var band = s.Eq.Bands[sel];
            var f = band.Freq >= 1000 ? $"{band.Freq / 1000:0.##} kHz" : $"{band.Freq:0} Hz";
            var text = HasGain(band.Shape) ? $"Band {sel + 1}   {f}   {band.Gain:+0.0;-0.0;0.0} dB   Q {band.Q:0.0#}" : $"Band {sel + 1}   {f}   Q {band.Q:0.0#}";
            var t = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 11, accent);
            ctx.DrawText(t, new Point(w - t.Width - 10, 8));
        }
    }

    private int HitBand(Point p)
    {
        if (Compact || !ShowBands || Settings is not { } s || !s.Eq.On) return -1;
        var best = -1;
        var bestD = 14.0;
        for (var i = 0; i < s.Eq.Bands.Count; i++)
        {
            var d = Point.Distance(DotOf(s.Eq.Bands[i]), p);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || Editor is null) return;
        var band = HitBand(e.GetPosition(this));
        if (band < 0) return;
        Editor.SelectBand(band);
        _drag = band;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_drag >= 0 && Settings is { } s && _drag < s.Eq.Bands.Count)
        {
            var f = FreqAt(p.X, Bounds.Width);
            f = Math.Round(f, f < 100 ? 1 : 0);
            var gain = HasGain(s.Eq.Bands[_drag].Shape) ? Math.Round(Math.Clamp(DbAt(p.Y), -Range, Range) * 2) / 2 : s.Eq.Bands[_drag].Gain;
            Editor?.MoveBand(_drag, f, gain);
            return;
        }
        var h = HitBand(p);
        if (h == _hover) return;
        _hover = h;
        Cursor = h >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag < 0) return;
        _drag = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var band = HitBand(e.GetPosition(this));
        if (band < 0) band = SelectedBand;
        if (band < 0 || Editor is null) return;
        Editor.ScaleQ(band, e.Delta.Y > 0 ? 1.12 : 1 / 1.12);
        e.Handled = true;
    }
}

/// <summary>A small bar meter for gain reduction or loudness, drawn from a value and a range.</summary>
public sealed class BarMeter : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<BarMeter, double>(nameof(Value));
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<BarMeter, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<BarMeter, double>(nameof(Maximum), 20);
    public static readonly StyledProperty<double> TargetProperty = AvaloniaProperty.Register<BarMeter, double>(nameof(Target), double.NaN);
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<BarMeter, IBrush?>(nameof(Fill));
    /// <summary>Upright: fills from the bottom, or from the top when <see cref="FromTop"/> (gain reduction).</summary>
    public static readonly StyledProperty<bool> VerticalProperty = AvaloniaProperty.Register<BarMeter, bool>(nameof(Vertical));
    public static readonly StyledProperty<bool> FromTopProperty = AvaloniaProperty.Register<BarMeter, bool>(nameof(FromTop));

    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#0E0F12"));
    private static readonly IBrush TargetBrush = new SolidColorBrush(Color.Parse("#F2F2F0"));

    static BarMeter() => AffectsRender<BarMeter>(ValueProperty, MinimumProperty, MaximumProperty, TargetProperty, FillProperty, VerticalProperty, FromTopProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool Vertical { get => GetValue(VerticalProperty); set => SetValue(VerticalProperty, value); }
    public bool FromTop { get => GetValue(FromTopProperty); set => SetValue(FromTopProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(Back, b, 3);
        var span = Maximum - Minimum;
        if (span <= 0) return;
        var v = double.IsFinite(Value) ? Math.Clamp((Value - Minimum) / span, 0, 1) : 0;
        var fill = Fill ?? (this.TryFindResource("Accent", ActualThemeVariant, out var a) && a is IBrush ab ? ab : TargetBrush);
        if (Vertical)
        {
            var hgt = b.Height * v;
            ctx.FillRectangle(fill, FromTop ? new Rect(0, 0, b.Width, hgt) : new Rect(0, b.Height - hgt, b.Width, hgt), 3);
            return;
        }
        ctx.FillRectangle(fill, new Rect(0, 0, b.Width * v, b.Height), 3);
        if (double.IsFinite(Target))
        {
            var t = Math.Clamp((Target - Minimum) / span, 0, 1) * b.Width;
            ctx.FillRectangle(TargetBrush, new Rect(t - 1, 0, 2, b.Height));
        }
    }
}
