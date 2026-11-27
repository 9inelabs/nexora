using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace Nexora.App.Controls;

/// <summary>Colours and text shared by the NEX-Audio graphs.</summary>
internal static class GraphKit
{
    public static readonly IBrush Back = new SolidColorBrush(Color.Parse("#0E0F12"));
    public static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#16FFFFFF")), 1);
    public static readonly IPen GridStrong = new Pen(new SolidColorBrush(Color.Parse("#2CFFFFFF")), 1);
    public static readonly IPen Guide = new Pen(new SolidColorBrush(Color.Parse("#40FFFFFF")), 1) { DashStyle = new DashStyle([3, 3], 0) };
    public static readonly IBrush Label = new SolidColorBrush(Color.Parse("#6C6F77"));
    public static readonly IBrush Bright = new SolidColorBrush(Color.Parse("#F2F2F0"));
    public static readonly IBrush Off = new SolidColorBrush(Color.Parse("#55595F"));
    public static readonly IBrush LevelFill = new SolidColorBrush(Color.Parse("#2A3D44"));
    public static readonly IPen LevelLine = new Pen(new SolidColorBrush(Color.Parse("#5E8C99")), 1.2);
    public static readonly IBrush Fallback = new SolidColorBrush(Color.Parse("#E2BE76"));
    public static readonly Typeface Mono = new(new FontFamily("avares://Nexora/Assets/Fonts#JetBrains Mono"));

    public static IBrush Accent(Control c) => c.TryFindResource("Accent", c.ActualThemeVariant, out var a) && a is IBrush b ? b : Fallback;

    public static IBrush Tint(IBrush accent, byte alpha) =>
        accent is ISolidColorBrush s ? new SolidColorBrush(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B)) : accent;

    public static FormattedText Text(string s, double size = 9, IBrush? brush = null) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush ?? Label);

    public static string Hz(double f) => f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";
}

/// <summary>
/// What a gate or compressor does to the level: sound coming in along the bottom, sound going out up the side.
/// The bright dot is what is playing now.
/// </summary>
public sealed class DynamicsGraph : Control
{
    public static readonly StyledProperty<bool> IsGateProperty = AvaloniaProperty.Register<DynamicsGraph, bool>(nameof(IsGate));
    public static readonly StyledProperty<bool> IsOnProperty = AvaloniaProperty.Register<DynamicsGraph, bool>(nameof(IsOn), true);
    public static readonly StyledProperty<double> ThresholdProperty = AvaloniaProperty.Register<DynamicsGraph, double>(nameof(Threshold), -20);
    public static readonly StyledProperty<double> RatioProperty = AvaloniaProperty.Register<DynamicsGraph, double>(nameof(Ratio), 3);
    public static readonly StyledProperty<double> KneeProperty = AvaloniaProperty.Register<DynamicsGraph, double>(nameof(Knee));
    public static readonly StyledProperty<double> RangeProperty = AvaloniaProperty.Register<DynamicsGraph, double>(nameof(Range), 40);
    public static readonly StyledProperty<double> MakeupProperty = AvaloniaProperty.Register<DynamicsGraph, double>(nameof(Makeup));
    public static readonly StyledProperty<double> InputDbProperty = AvaloniaProperty.Register<DynamicsGraph, double>(nameof(InputDb), -90);
    public static readonly StyledProperty<bool> ShowScaleProperty = AvaloniaProperty.Register<DynamicsGraph, bool>(nameof(ShowScale), true);

    static DynamicsGraph() => AffectsRender<DynamicsGraph>(IsGateProperty, IsOnProperty, ThresholdProperty, RatioProperty, KneeProperty,
        RangeProperty, MakeupProperty, InputDbProperty, ShowScaleProperty);

    public bool IsGate { get => GetValue(IsGateProperty); set => SetValue(IsGateProperty, value); }
    public bool IsOn { get => GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    public double Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public double Ratio { get => GetValue(RatioProperty); set => SetValue(RatioProperty, value); }
    public double Knee { get => GetValue(KneeProperty); set => SetValue(KneeProperty, value); }
    public double Range { get => GetValue(RangeProperty); set => SetValue(RangeProperty, value); }
    public double Makeup { get => GetValue(MakeupProperty); set => SetValue(MakeupProperty, value); }
    public double InputDb { get => GetValue(InputDbProperty); set => SetValue(InputDbProperty, value); }
    public bool ShowScale { get => GetValue(ShowScaleProperty); set => SetValue(ShowScaleProperty, value); }

    /// <summary>A compressor's output level for an input level (soft knee).</summary>
    public static double CompOut(double x, double t, double ratio, double knee)
    {
        var r = Math.Max(1, ratio);
        var over = x - t;
        if (knee > 0 && Math.Abs(over) <= knee / 2) return x + (1 / r - 1) * Math.Pow(over + knee / 2, 2) / (2 * knee);
        return over <= 0 ? x : t + over / r;
    }

    public static double GateOut(double x, double t, double range) => x >= t ? x : x - Math.Max(0, range);

    /// <summary>Draws the curve into <paramref name="r"/>. Shared with the effect graphs.</summary>
    internal static void Draw(DrawingContext ctx, Rect r, Control owner, bool gate, bool on, double t, double ratio, double knee, double range,
        double makeup, double inputDb, bool scale)
    {
        var lo = gate ? -80.0 : -60.0;
        double X(double db) => r.X + (Math.Clamp(db, lo, 0) - lo) / -lo * r.Width;
        double Y(double db) => r.Bottom - (Math.Clamp(db, lo, 0) - lo) / -lo * r.Height;

        for (var db = lo; db <= 0; db += 10)
        {
            ctx.DrawLine(GraphKit.Grid, new Point(X(db), r.Y), new Point(X(db), r.Bottom));
            ctx.DrawLine(GraphKit.Grid, new Point(r.X, Y(db)), new Point(r.Right, Y(db)));
            if (scale && db > lo && db < 0)
            {
                var ft = GraphKit.Text($"{db:0}");
                ctx.DrawText(ft, new Point(X(db) - ft.Width / 2, r.Bottom - ft.Height - 2));
                ctx.DrawText(ft, new Point(r.X + 3, Y(db) - ft.Height / 2));
            }
        }
        ctx.DrawLine(GraphKit.Guide, new Point(X(lo), Y(lo)), new Point(X(0), Y(0)));
        var accent = GraphKit.Accent(owner);
        ctx.DrawLine(new Pen(GraphKit.Tint(accent, 110), 1) { DashStyle = new DashStyle([2, 3], 0) }, new Point(X(t), r.Y), new Point(X(t), r.Bottom));

        double Out(double x) => gate ? GateOut(x, t, range) : CompOut(x, t, ratio, knee) + makeup;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            var first = true;
            for (var x = lo; x <= 0.001; x += 0.25)
            {
                var p = new Point(X(x), Y(Out(x)));
                if (first) g.BeginFigure(p, false);
                else g.LineTo(p);
                first = false;
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(on ? accent : GraphKit.Off, 2.2), geo);

        if (on && inputDb > lo)
        {
            var p = new Point(X(inputDb), Y(Out(inputDb)));
            ctx.DrawLine(new Pen(GraphKit.Tint(accent, 70), 1), new Point(p.X, r.Bottom), p);
            ctx.DrawEllipse(GraphKit.Tint(accent, 60), null, p, 9, 9);
            ctx.DrawEllipse(GraphKit.Bright, null, p, 4.5, 4.5);
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(GraphKit.Back, b, 8);
        using var clip = ctx.PushClip(b);
        Draw(ctx, b.Deflate(1), this, IsGate, IsOn, Threshold, Ratio, Knee, Range, Makeup, InputDb, ShowScale);
    }
}

/// <summary>The last few seconds of one strip's level and gain reduction, filled by the screen 30 times a second.</summary>
public sealed class LevelHistory
{
    public const int Length = 180; // 6 seconds at 30 a second

    public float[] Level { get; } = new float[Length];
    public float[] Reduction { get; } = new float[Length];
    public int Head { get; private set; }
    public event Action? Changed;

    public LevelHistory() => Array.Fill(Level, -90f);

    public void Push(double levelDb, double reductionDb)
    {
        Level[Head] = (float)levelDb;
        Reduction[Head] = (float)Math.Max(0, reductionDb);
        Head = (Head + 1) % Length;
        Changed?.Invoke();
    }

    public void Clear()
    {
        Array.Fill(Level, -90f);
        Array.Clear(Reduction);
        Changed?.Invoke();
    }

    /// <summary>The i-th value, oldest first.</summary>
    public float LevelAt(int i) => Level[(Head + i) % Length];
    public float ReductionAt(int i) => Reduction[(Head + i) % Length];
}

/// <summary>A scrolling graph of the level coming in (shaded) and how far the gate or compressor turned it down (gold line from the top).</summary>
public sealed class HistoryGraph : Control
{
    public static readonly StyledProperty<LevelHistory?> HistoryProperty = AvaloniaProperty.Register<HistoryGraph, LevelHistory?>(nameof(History));
    public static readonly StyledProperty<double> ThresholdProperty = AvaloniaProperty.Register<HistoryGraph, double>(nameof(Threshold), double.NaN);
    public static readonly StyledProperty<double> ReductionRangeProperty = AvaloniaProperty.Register<HistoryGraph, double>(nameof(ReductionRange), 24);
    public static readonly StyledProperty<bool> ShowReductionProperty = AvaloniaProperty.Register<HistoryGraph, bool>(nameof(ShowReduction), true);

    static HistoryGraph() => AffectsRender<HistoryGraph>(ThresholdProperty, ReductionRangeProperty, ShowReductionProperty);

    public LevelHistory? History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public double Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public double ReductionRange { get => GetValue(ReductionRangeProperty); set => SetValue(ReductionRangeProperty, value); }
    public bool ShowReduction { get => GetValue(ShowReductionProperty); set => SetValue(ShowReductionProperty, value); }

    private bool _queued;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != HistoryProperty) return;
        if (change.OldValue is LevelHistory o) o.Changed -= Queue;
        if (change.NewValue is LevelHistory n && VisualRoot is not null) n.Changed += Queue;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (History is { } h)
        {
            h.Changed -= Queue;
            h.Changed += Queue;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (History is { } h) h.Changed -= Queue;
    }

    private void Queue()
    {
        if (_queued || !IsEffectivelyVisible) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(GraphKit.Back, b, 8);
        using var clip = ctx.PushClip(b);
        const double lo = -60;
        var w = b.Width;
        var h = b.Height;
        double Y(double db) => h - (Math.Clamp(db, lo, 0) - lo) / -lo * (h - 8);

        for (var db = -50.0; db < 0; db += 10)
        {
            ctx.DrawLine(GraphKit.Grid, new Point(0, Y(db)), new Point(w, Y(db)));
            ctx.DrawText(GraphKit.Text($"{db:0}"), new Point(4, Y(db) - 12));
        }
        for (var s = 1; s < 6; s++) ctx.DrawLine(GraphKit.Grid, new Point(w * s / 6, 0), new Point(w * s / 6, h));
        var secs = GraphKit.Text("6 s ago");
        ctx.DrawText(secs, new Point(4, h - secs.Height - 2));
        var now = GraphKit.Text("now");
        ctx.DrawText(now, new Point(w - now.Width - 4, h - now.Height - 2));

        var accent = GraphKit.Accent(this);
        if (double.IsFinite(Threshold))
        {
            var ty = Y(Threshold);
            ctx.DrawLine(new Pen(GraphKit.Tint(accent, 130), 1) { DashStyle = new DashStyle([4, 3], 0) }, new Point(0, ty), new Point(w, ty));
            var ft = GraphKit.Text($"threshold {Threshold:0} dB", 9, GraphKit.Tint(accent, 200));
            ctx.DrawText(ft, new Point(w - ft.Width - 6, ty - ft.Height - 1));
        }

        if (History is not { } hist) return;
        var n = LevelHistory.Length;
        var step = w / (n - 1);
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(0, h), true);
            for (var i = 0; i < n; i++) g.LineTo(new Point(i * step, Y(hist.LevelAt(i))));
            g.LineTo(new Point(w, h));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(GraphKit.LevelFill, null, area);
        var top = new StreamGeometry();
        using (var g = top.Open())
        {
            g.BeginFigure(new Point(0, Y(hist.LevelAt(0))), false);
            for (var i = 1; i < n; i++) g.LineTo(new Point(i * step, Y(hist.LevelAt(i))));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, GraphKit.LevelLine, top);

        if (!ShowReduction) return;
        var range = Math.Max(1, ReductionRange);
        var red = new StreamGeometry();
        using (var g = red.Open())
        {
            g.BeginFigure(new Point(0, 0), true);
            for (var i = 0; i < n; i++) g.LineTo(new Point(i * step, Math.Min(hist.ReductionAt(i), range) / range * h * 0.5));
            g.LineTo(new Point(w, 0));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(GraphKit.Tint(accent, 50), new Pen(accent, 1.6), red);
        var last = hist.ReductionAt(n - 1);
        var lbl = GraphKit.Text(last > 0.05 ? $"-{last:0.0} dB" : "0 dB", 10, accent);
        ctx.DrawText(lbl, new Point(w - lbl.Width - 6, 4));
    }
}

/// <summary>Where the graphic EQ sends its changes.</summary>
public interface IGraphicEqEditor
{
    void SetBand(int band, double gainDb);
}

/// <summary>
/// A 31-band graphic EQ: the overall curve on top and one fader per band below. Drag across the faders to draw a shape;
/// double-click a fader to put it back to 0 dB.
/// </summary>
public sealed class GraphicEqControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty = AvaloniaProperty.Register<GraphicEqControl, IReadOnlyList<double>?>(nameof(Values));
    public static readonly StyledProperty<IGraphicEqEditor?> EditorProperty = AvaloniaProperty.Register<GraphicEqControl, IGraphicEqEditor?>(nameof(Editor));
    public static readonly StyledProperty<bool> IsOnProperty = AvaloniaProperty.Register<GraphicEqControl, bool>(nameof(IsOn), true);
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<GraphicEqControl, bool>(nameof(Compact));

    private const double Range = 15;
    private readonly double[] _local = new double[31];
    private int _drag = -1, _hover = -1;

    static GraphicEqControl() => AffectsRender<GraphicEqControl>(ValuesProperty, IsOnProperty, CompactProperty);

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IGraphicEqEditor? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public bool IsOn { get => GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    public bool Compact { get => GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValuesProperty && _drag < 0)
        {
            Array.Clear(_local);
            if (Values is { } v) for (var i = 0; i < Math.Min(31, v.Count); i++) _local[i] = v[i];
        }
    }

    private Rect CurveRect => Compact ? new Rect(Bounds.Size) : new Rect(0, 0, Bounds.Width, Bounds.Height * 0.36);
    private Rect FaderRect => new(0, Bounds.Height * 0.36 + 10, Bounds.Width, Bounds.Height * 0.64 - 10);

    public static double Response(IReadOnlyList<double> gains, double f)
    {
        var db = 0.0;
        for (var i = 0; i < Math.Min(gains.Count, 31); i++)
        {
            if (Math.Abs(gains[i]) < 0.01) continue;
            db += Biquad.Make(FilterShape.Peak, GraphicEq.Centres[i], GraphicEq.BandQ, gains[i], 48000).MagnitudeDb(f, 48000);
        }
        return db;
    }

    public override void Render(DrawingContext ctx)
    {
        var accent = GraphKit.Accent(this);
        var cr = CurveRect;
        ctx.FillRectangle(GraphKit.Back, cr, 8);
        using (ctx.PushClip(cr))
        {
            double XOf(double f) => cr.Width * Math.Log(f / 20) / Math.Log(1000);
            double YOf(double db) => cr.Y + cr.Height / 2 - db / Range * (cr.Height / 2 - 6);
            foreach (var f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
            {
                ctx.DrawLine(GraphKit.Grid, new Point(XOf(f), cr.Y), new Point(XOf(f), cr.Bottom));
                if (!Compact) ctx.DrawText(GraphKit.Text(GraphKit.Hz(f)), new Point(XOf(f) + 3, cr.Bottom - 13));
            }
            foreach (var db in new[] { -12.0, -6, 6, 12 })
            {
                ctx.DrawLine(GraphKit.Grid, new Point(0, YOf(db)), new Point(cr.Width, YOf(db)));
                if (!Compact) ctx.DrawText(GraphKit.Text($"{db:+0;-0}"), new Point(4, YOf(db) - 12));
            }
            ctx.DrawLine(GraphKit.GridStrong, new Point(0, YOf(0)), new Point(cr.Width, YOf(0)));
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(0, YOf(0)), true);
                for (var x = 0.0; x <= cr.Width; x += 2)
                    g.LineTo(new Point(x, YOf(Math.Clamp(Response(_local, 20 * Math.Pow(1000, x / cr.Width)), -Range - 3, Range + 3))));
                g.LineTo(new Point(cr.Width, YOf(0)));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(GraphKit.Tint(IsOn ? accent : GraphKit.Off, 34), new Pen(IsOn ? accent : GraphKit.Off, 2), geo);
        }
        if (Compact) return;

        var fr = FaderRect;
        var col = fr.Width / 31;
        double FY(double db) => fr.Y + 8 + (Range - db) / (2 * Range) * (fr.Height - 34);
        ctx.FillRectangle(GraphKit.Back, fr, 8);
        foreach (var db in new[] { 12.0, 6, 0, -6, -12 })
            ctx.DrawLine(db == 0 ? GraphKit.GridStrong : GraphKit.Grid, new Point(fr.X, FY(db)), new Point(fr.Right, FY(db)));
        for (var i = 0; i < 31; i++)
        {
            var cx = fr.X + col * (i + 0.5);
            var hot = i == _drag || i == _hover;
            ctx.FillRectangle(hot ? new SolidColorBrush(Color.Parse("#0DFFFFFF")) : Brushes.Transparent, new Rect(cx - col / 2 + 1, fr.Y + 2, col - 2, fr.Height - 4), 4);
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#22252A")), new Rect(cx - 1.5, FY(Range), 3, FY(-Range) - FY(Range)), 1.5f);
            var v = _local[i];
            var y0 = FY(0);
            var y = FY(v);
            if (Math.Abs(v) > 0.01) ctx.FillRectangle(IsOn ? accent : GraphKit.Off, new Rect(cx - 2, Math.Min(y, y0), 4, Math.Abs(y - y0)), 1);
            var capW = Math.Min(col - 6, 18);
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#3A3D44")), new Rect(cx - capW / 2, y - 6, capW, 12), 3);
            ctx.FillRectangle(hot ? accent : GraphKit.Bright, new Rect(cx - capW / 2 + 2, y - 0.75, capW - 4, 1.5));
            var lbl = GraphKit.Text(GraphKit.Hz(GraphicEq.Centres[i]), 8.5);
            ctx.DrawText(lbl, new Point(cx - lbl.Width / 2, fr.Bottom - 20));
        }
        var focus = _drag >= 0 ? _drag : _hover;
        if (focus >= 0)
        {
            var t = GraphKit.Text($"{GraphKit.Hz(GraphicEq.Centres[focus])} Hz  {_local[focus]:+0.0;-0.0;0.0} dB", 11, accent);
            ctx.DrawText(t, new Point(cr.Right - t.Width - 8, cr.Y + 6));
        }
    }

    private int BandAt(Point p)
    {
        var fr = FaderRect;
        if (p.Y < fr.Y - 4) return -1;
        return Math.Clamp((int)((p.X - fr.X) / (fr.Width / 31)), 0, 30);
    }

    private double GainAt(double y)
    {
        var fr = FaderRect;
        var t = (y - fr.Y - 8) / (fr.Height - 34);
        return Math.Clamp(Math.Round((Range - t * 2 * Range) * 2) / 2, -Range, Range);
    }

    private void Set(int band, double v)
    {
        if (Math.Abs(_local[band] - v) < 0.001) return;
        _local[band] = v;
        Editor?.SetBand(band, v);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Compact || !IsEnabled) return;
        var p = e.GetPosition(this);
        var band = BandAt(p);
        if (band < 0) return;
        if (e.ClickCount == 2)
        {
            Set(band, 0);
            e.Handled = true;
            return;
        }
        _drag = band;
        Set(band, GainAt(p.Y));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Compact) return;
        var p = e.GetPosition(this);
        if (_drag >= 0)
        {
            var band = BandAt(p);
            if (band < 0) band = _drag;
            _drag = band;
            Set(band, GainAt(p.Y));
        }
        else
        {
            var h = BandAt(p);
            if (h != _hover)
            {
                _hover = h;
                InvalidateVisual();
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag < 0) return;
        _drag = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }
}

/// <summary>A picture of what an effect in the rack does: the reverb's tail, the delay's echoes, a compressor's curve and so on.</summary>
public sealed class EffectGraph : Control
{
    public static readonly StyledProperty<RackItem?> ItemProperty = AvaloniaProperty.Register<EffectGraph, RackItem?>(nameof(Item));

    static EffectGraph() => AffectsRender<EffectGraph>(ItemProperty);

    public RackItem? Item { get => GetValue(ItemProperty); set => SetValue(ItemProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(GraphKit.Back, b, 8);
        if (Item is not { } it) return;
        using var clip = ctx.PushClip(b);
        var r = b.Deflate(new Thickness(10, 10, 10, 18));
        var accent = GraphKit.Accent(this);
        if (it.Bypass) accent = GraphKit.Off;
        switch (it.Type)
        {
            case "reverb": Reverb(ctx, r, it, accent); break;
            case "delay": Delay(ctx, r, it, accent); break;
            case "chorus": Chorus(ctx, r, it, accent); break;
            case "pitch": Pitch(ctx, r, it, accent); break;
            case "multiband": Multiband(ctx, r, it, accent); break;
            case "eq31":
            {
                var gains = Enumerable.Range(0, 31).Select(i => it.P(GraphicEq.ParamName(i), 0)).ToList();
                Curve(ctx, r, f => GraphicEqControl.Response(gains, f), accent, 15);
                break;
            }
            case "deesser":
            {
                var f0 = it.P("freq", 6500);
                var depth = -it.P("range", 10);
                Curve(ctx, r, f => depth * Math.Exp(-Math.Pow(Math.Log(f / f0) / 0.5, 2)), accent, 24);
                Caption(ctx, b, $"listens and turns down around {GraphKit.Hz(f0)}Hz, up to {-depth:0} dB");
                break;
            }
            case "comp":
                DynamicsGraph.Draw(ctx, Square(r), this, false, !it.Bypass, it.P("threshold", -18), it.P("ratio", 3), it.P("knee", 6), 0, it.P("makeup", 0), -90, true);
                break;
            case "limiter":
                DynamicsGraph.Draw(ctx, Square(r), this, false, !it.Bypass, it.P("ceiling", -1), 100, 0, 0, 0, -90, true);
                break;
            default:
                Caption(ctx, b, it.Type == "plugin" ? "Open the plugin window to see its own controls." : "");
                break;
        }
    }

    private static Rect Square(Rect r)
    {
        var s = Math.Min(r.Width, r.Height + 8);
        return new Rect(r.X + (r.Width - s) / 2, r.Y - 4, s, s);
    }

    private static void Caption(DrawingContext ctx, Rect b, string text)
    {
        if (text.Length == 0) return;
        var t = GraphKit.Text(text, 10);
        ctx.DrawText(t, new Point(b.X + 10, b.Bottom - t.Height - 4));
    }

    private static void Curve(DrawingContext ctx, Rect r, Func<double, double> db, IBrush accent, double range)
    {
        double X(double f) => r.X + r.Width * Math.Log(f / 20) / Math.Log(1000);
        double Y(double d) => r.Y + r.Height / 2 - Math.Clamp(d, -range, range) / range * (r.Height / 2);
        foreach (var f in new double[] { 100, 1000, 10000 })
        {
            ctx.DrawLine(GraphKit.Grid, new Point(X(f), r.Y), new Point(X(f), r.Bottom));
            ctx.DrawText(GraphKit.Text(GraphKit.Hz(f)), new Point(X(f) + 3, r.Bottom + 2));
        }
        ctx.DrawLine(GraphKit.GridStrong, new Point(r.X, Y(0)), new Point(r.Right, Y(0)));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(r.X, Y(0)), true);
            for (var x = 0.0; x <= r.Width; x += 2) g.LineTo(new Point(r.X + x, Y(db(20 * Math.Pow(1000, x / r.Width)))));
            g.LineTo(new Point(r.Right, Y(0)));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(GraphKit.Tint(accent, 36), new Pen(accent, 2), geo);
    }

    private static void Reverb(DrawingContext ctx, Rect r, RackItem it, IBrush accent)
    {
        var decay = it.P("decay", 2.3);
        var pre = it.P("predelay", 20) / 1000;
        var size = it.P("size", 100) / 100;
        var damp = it.P("damping", 50) / 100;
        var total = Math.Max(1, pre + decay * 1.25);
        double X(double s) => r.X + s / total * r.Width;
        double Y(double db) => r.Bottom - (Math.Clamp(db, -60, 0) + 60) / 60 * r.Height;

        for (var s = 0.5; s < total; s += total > 5 ? 1 : 0.5)
        {
            ctx.DrawLine(GraphKit.Grid, new Point(X(s), r.Y), new Point(X(s), r.Bottom));
            ctx.DrawText(GraphKit.Text($"{s:0.#} s"), new Point(X(s) + 3, r.Bottom + 2));
        }
        foreach (var db in new[] { -20.0, -40 }) ctx.DrawLine(GraphKit.Grid, new Point(r.X, Y(db)), new Point(r.Right, Y(db)));

        // The dry sound, then the gap before the reverb starts.
        ctx.FillRectangle(GraphKit.Bright, new Rect(X(0), Y(0), 2, r.Bottom - Y(0)));
        if (pre > 0.002)
        {
            ctx.DrawText(GraphKit.Text($"pre-delay {pre * 1000:0} ms", 10, accent), new Point(r.X, r.Y));
        }

        // Early reflections: a few bright taps, closer together in a small room.
        var rnd = new Random(7);
        var span = 0.02 + 0.06 * size;
        for (var i = 0; i < 9; i++)
        {
            var t = pre + span * (i + rnd.NextDouble()) / 9;
            var level = -4 - i * 1.6 - rnd.NextDouble() * 4;
            ctx.FillRectangle(GraphKit.Tint(accent, 200), new Rect(X(t), Y(level), 1.6, r.Bottom - Y(level)));
        }

        // The tail: lows last the full decay; highs die sooner the more damping there is.
        Tail(ctx, X, Y, pre + span * 0.5, decay, total, GraphKit.LevelFill, GraphKit.LevelLine);
        var highs = decay * (1 - 0.75 * damp);
        Tail(ctx, X, Y, pre + span * 0.5, highs, total, GraphKit.Tint(accent, 46), new Pen(accent, 1.8));
        var style = it.T("style", "hall");
        var cap = GraphKit.Text($"{char.ToUpperInvariant(style[0])}{style[1..]}  ·  decay {decay:0.0} s  ·  highs fade by {highs:0.0} s", 10, accent);
        ctx.DrawText(cap, new Point(r.Right - cap.Width, r.Y));
    }

    private static void Tail(DrawingContext ctx, Func<double, double> x, Func<double, double> y, double start, double rt60, double total, IBrush fill, IPen line)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x(start), y(-60)), true);
            for (var t = start; t <= total; t += total / 200)
            {
                var db = -6 - 60 * (t - start) / Math.Max(0.05, rt60);
                g.LineTo(new Point(x(t), y(db)));
            }
            g.LineTo(new Point(x(total), y(-60)));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(fill, line, geo);
    }

    private static void Delay(DrawingContext ctx, Rect r, RackItem it, IBrush accent)
    {
        var bpm = it.P("bpm", 0);
        var time = bpm >= 30 ? 60000 / bpm * it.P("beats", 0.75) : it.P("time", 375);
        var fb = it.P("feedback", 35) / 100;
        var ping = it.P("pingpong", 0) >= 0.5;
        var total = Math.Max(time * 8, 1000);
        double X(double ms) => r.X + ms / total * r.Width;
        var mid = ping ? r.Y + r.Height / 2 : r.Bottom;
        var half = ping ? r.Height / 2 - 4 : r.Height - 4;

        for (var s = 500.0; s < total; s += total > 4000 ? 1000 : 500)
        {
            ctx.DrawLine(GraphKit.Grid, new Point(X(s), r.Y), new Point(X(s), r.Bottom));
            ctx.DrawText(GraphKit.Text($"{s / 1000:0.#} s"), new Point(X(s) + 3, r.Bottom + 2));
        }
        if (ping)
        {
            ctx.DrawLine(GraphKit.GridStrong, new Point(r.X, mid), new Point(r.Right, mid));
            ctx.DrawText(GraphKit.Text("L"), new Point(r.Right - 10, r.Y));
            ctx.DrawText(GraphKit.Text("R"), new Point(r.Right - 10, r.Bottom - 12));
        }
        ctx.FillRectangle(GraphKit.Bright, new Rect(X(0), mid - half, 3, half), 1);
        var level = 0.85;
        for (var n = 1; n < 40 && level > 0.02; n++)
        {
            var x = X(n * time);
            if (x > r.Right) break;
            var hgt = half * level;
            var down = ping && n % 2 == 0;
            ctx.FillRectangle(accent, new Rect(x - 1.5, down ? mid : mid - hgt, 3, hgt), 1);
            level *= Math.Max(fb, 0.0001);
        }
        var cap = GraphKit.Text(bpm >= 30 ? $"{ParamInfo.BeatName(it.P("beats", 0.75))} at {bpm:0} bpm = {time:0} ms" : $"every {time:0} ms", 10, accent);
        ctx.DrawText(cap, new Point(r.Right - cap.Width - 14, r.Y));
    }

    private static void Chorus(DrawingContext ctx, Rect r, RackItem it, IBrush accent)
    {
        var rate = it.P("rate", 0.8);
        var depth = it.P("depth", 30) / 100;
        ctx.DrawLine(GraphKit.GridStrong, new Point(r.X, r.Y + r.Height / 2), new Point(r.Right, r.Y + r.Height / 2));
        for (var v = 0; v < 2; v++)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                for (var x = 0.0; x <= r.Width; x += 2)
                {
                    var t = x / r.Width * 4; // four seconds across
                    var y = r.Y + r.Height / 2 - Math.Sin(2 * Math.PI * rate * t + v * Math.PI / 2) * depth * (r.Height / 2 - 6);
                    if (x == 0) g.BeginFigure(new Point(r.X + x, y), false);
                    else g.LineTo(new Point(r.X + x, y));
                }
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(v == 0 ? accent : GraphKit.Tint(accent, 110), 2), geo);
        }
        Caption(ctx, r.Inflate(new Thickness(10, 10, 10, 18)), $"two voices drifting {rate:0.##} times a second, last 4 s shown");
    }

    private static void Pitch(DrawingContext ctx, Rect r, RackItem it, IBrush accent)
    {
        var key = it.T("key", "C");
        var scale = it.T("scale", "major");
        var root = Array.FindIndex(Scales.Keys, k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (root < 0) root = 0;
        int[] steps = scale switch
        {
            "minor" => [0, 2, 3, 5, 7, 8, 10],
            "chromatic" => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
            "pentatonic" => [0, 2, 4, 7, 9],
            _ => [0, 2, 4, 5, 7, 9, 11],
        };
        var on = steps.Select(s => (s + root) % 12).ToHashSet();
        var names = Scales.Keys;
        var kw = r.Width / 12;
        for (var i = 0; i < 12; i++)
        {
            var black = i is 1 or 3 or 6 or 8 or 10;
            var rect = new Rect(r.X + i * kw + 2, r.Y + (black ? 0 : 18), kw - 4, r.Height - (black ? 18 : 18));
            ctx.FillRectangle(on.Contains(i) ? accent : new SolidColorBrush(Color.Parse(black ? "#1A1C20" : "#2A2D33")), rect, 4);
            var t = GraphKit.Text(names[i], 11, on.Contains(i) ? new SolidColorBrush(Color.Parse("#121316")) : GraphKit.Label);
            ctx.DrawText(t, new Point(rect.X + (rect.Width - t.Width) / 2, rect.Bottom - t.Height - 6));
        }
        Caption(ctx, r.Inflate(new Thickness(10, 10, 10, 18)), $"notes in {key} {scale}: a voice is pulled to the nearest gold note");
    }

    private static void Multiband(DrawingContext ctx, Rect r, RackItem it, IBrush accent)
    {
        var lo = it.P("low", 200);
        var hi = it.P("high", 3000);
        double X(double f) => r.X + r.Width * Math.Log(f / 20) / Math.Log(1000);
        string[] names = ["Low", "Mid", "Top"];
        double[] edges = [20, lo, hi, 20000];
        string[] keys = ["l", "m", "h"];
        for (var i = 0; i < 3; i++)
        {
            var x0 = X(edges[i]);
            var x1 = X(edges[i + 1]);
            var thr = it.P(keys[i] + "thr", -20);
            var gain = it.P(keys[i] + "gain", 0);
            var y = r.Y + (-thr) / 60 * r.Height;
            ctx.FillRectangle(GraphKit.Tint(accent, (byte)(30 + i * 12)), new Rect(x0 + 1, y, x1 - x0 - 2, r.Bottom - y), 4);
            ctx.DrawLine(new Pen(accent, 2), new Point(x0 + 1, y), new Point(x1 - 1, y));
            var t = GraphKit.Text($"{names[i]}  {thr:0} dB  {it.P(keys[i] + "ratio", 2.5):0.#}:1  {gain:+0;-0;0} dB", 10, GraphKit.Bright);
            ctx.DrawText(t, new Point(x0 + 6, Math.Max(r.Y, y - t.Height - 3)));
        }
        foreach (var f in new[] { lo, hi })
        {
            ctx.DrawLine(GraphKit.Guide, new Point(X(f), r.Y), new Point(X(f), r.Bottom));
            ctx.DrawText(GraphKit.Text(GraphKit.Hz(f) + " Hz"), new Point(X(f) + 3, r.Bottom + 2));
        }
    }
}
