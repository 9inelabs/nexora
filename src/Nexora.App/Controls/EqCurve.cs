using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Nexora.Audio.Dsp;

namespace Nexora.App.Controls;

/// <summary>
/// The shape of a strip's filters and EQ, from 20 Hz to 20 kHz, with the spectrum of what is playing
/// behind it when there is one. Dots mark each EQ band.
/// </summary>
public sealed class EqCurve : Control
{
    public static readonly StyledProperty<FilterSettings?> SettingsProperty = AvaloniaProperty.Register<EqCurve, FilterSettings?>(nameof(Settings));
    public static readonly StyledProperty<float[]?> SpectrumProperty = AvaloniaProperty.Register<EqCurve, float[]?>(nameof(Spectrum));
    public static readonly StyledProperty<double> RangeProperty = AvaloniaProperty.Register<EqCurve, double>(nameof(Range), 18);

    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#0E0F12"));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#1AFFFFFF")), 1);
    private static readonly IPen Zero = new Pen(new SolidColorBrush(Color.Parse("#33FFFFFF")), 1);
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#6C6F77"));
    private static readonly IBrush SpectrumFill = new SolidColorBrush(Color.Parse("#2A3D44"));
    private static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#F2F2F0"));
    private static readonly IBrush DotOff = new SolidColorBrush(Color.Parse("#55595F"));
    private static readonly Typeface Mono = new(new FontFamily("avares://Nexora/Assets/Fonts#JetBrains Mono"));

    static EqCurve() => AffectsRender<EqCurve>(SettingsProperty, SpectrumProperty, RangeProperty);

    public FilterSettings? Settings { get => GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
    public float[]? Spectrum { get => GetValue(SpectrumProperty); set => SetValue(SpectrumProperty, value); }
    public double Range { get => GetValue(RangeProperty); set => SetValue(RangeProperty, value); }

    private static double XOf(double f, double w) => w * Math.Log(f / 20) / Math.Log(1000);
    private static double FreqAt(double x, double w) => 20 * Math.Pow(1000, x / w);

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(Back, b, 8);
        using var clip = ctx.PushClip(b);
        var w = b.Width;
        var h = b.Height;
        double YOf(double db) => h / 2 - db / Range * (h / 2 - 10);

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
            ctx.DrawLine(Grid, new Point(x, 0), new Point(x, h));
            var t = new FormattedText(f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 9, Label);
            ctx.DrawText(t, new Point(x + 3, h - t.Height - 2));
        }
        foreach (var db in new[] { -12.0, -6, 6, 12 })
            ctx.DrawLine(Grid, new Point(0, YOf(db)), new Point(w, YOf(db)));
        ctx.DrawLine(Zero, new Point(0, YOf(0)), new Point(w, YOf(0)));

        if (Settings is not { } s) return;
        var accent = this.TryFindResource("Accent", ActualThemeVariant, out var a) && a is IBrush ab ? ab : Dot;
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
        ctx.DrawGeometry(null, new Pen(accent, 2), curve);

        if (!s.Eq.On) return;
        for (var i = 0; i < s.Eq.Bands.Count; i++)
        {
            var band = s.Eq.Bands[i];
            var x = XOf(Math.Clamp(band.Freq, 20, 20000), w);
            var y = YOf(band.Shape is FilterShape.Peak or FilterShape.LowShelf or FilterShape.HighShelf ? Math.Clamp(band.Gain, -Range, Range) : 0);
            ctx.DrawEllipse(band.On ? Dot : DotOff, null, new Point(x, y), 5, 5);
            var t = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 9, Back);
            ctx.DrawText(t, new Point(x - t.Width / 2, y - t.Height / 2));
        }
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

    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#0E0F12"));
    private static readonly IBrush TargetBrush = new SolidColorBrush(Color.Parse("#F2F2F0"));

    static BarMeter() => AffectsRender<BarMeter>(ValueProperty, MinimumProperty, MaximumProperty, TargetProperty, FillProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(Back, b, 3);
        var span = Maximum - Minimum;
        if (span <= 0) return;
        var v = double.IsFinite(Value) ? Math.Clamp((Value - Minimum) / span, 0, 1) : 0;
        var fill = Fill ?? (this.TryFindResource("Accent", ActualThemeVariant, out var a) && a is IBrush ab ? ab : TargetBrush);
        ctx.FillRectangle(fill, new Rect(0, 0, b.Width * v, b.Height), 3);
        if (double.IsFinite(Target))
        {
            var t = Math.Clamp((Target - Minimum) / span, 0, 1) * b.Width;
            ctx.FillRectangle(TargetBrush, new Rect(t - 1, 0, 2, b.Height));
        }
    }
}
