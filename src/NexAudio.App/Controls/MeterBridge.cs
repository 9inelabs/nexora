using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NexAudio.App.Controls;

/// <summary>One strip on the meter bridge: where its levels are in the strip meters (Right is -1 for mono).</summary>
public sealed record BridgeEntry(string Section, string Number, string Name, IBrush Colour, int Left, int Right);

/// <summary>
/// Every channel, bus and output at once, in sections, drawn in one pass so a hundred meters stay light on the PC.
/// Stereo strips show two bars. A red light at the top stays lit after a clip until the peaks are cleared.
/// </summary>
public sealed class MeterBridge : Control
{
    public static readonly StyledProperty<MeterSet?> MetersProperty = AvaloniaProperty.Register<MeterBridge, MeterSet?>(nameof(Meters));
    public static readonly StyledProperty<IReadOnlyList<BridgeEntry>?> EntriesProperty =
        AvaloniaProperty.Register<MeterBridge, IReadOnlyList<BridgeEntry>?>(nameof(Entries));

    private static readonly IBrush Well = new SolidColorBrush(Color.Parse("#0E0F12"));
    private static readonly IBrush Low = new SolidColorBrush(Color.Parse("#3DD68C"));
    private static readonly IBrush Mid = new SolidColorBrush(Color.Parse("#E2BE76"));
    private static readonly IBrush High = new SolidColorBrush(Color.Parse("#FF5A5F"));
    private static readonly IBrush ClipOff = new SolidColorBrush(Color.Parse("#2A2C31"));
    private static readonly IBrush PeakLine = new SolidColorBrush(Color.Parse("#F2F2F0"));
    private static readonly IBrush SectionBack = new SolidColorBrush(Color.Parse("#0DFFFFFF"));
    private static readonly IPen Tick = new Pen(new SolidColorBrush(Color.Parse("#1FFFFFFF")), 1);
    private static readonly Typeface Face = new(new FontFamily("avares://Nexora.UI/Assets/Fonts#Manrope"), FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface Bold = new(new FontFamily("avares://Nexora.UI/Assets/Fonts#Manrope"), FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface Mono = new(new FontFamily("avares://Nexora.UI/Assets/Fonts#JetBrains Mono"));
    private static readonly double[] Scale = [0, -6, -12, -18, -30, -40, -50];

    /// <summary>Brighter writing for the high-contrast look.</summary>
    public static bool HighContrast { get; set; }

    private const double Floor = -60;
    private const double HeadH = 18, ClipH = 5, FootH = 32, ScaleW = 26, Gap = 10;
    private bool[] _clipped = [];
    private int _clearSeen;

    public MeterSet? Meters
    {
        get => GetValue(MetersProperty);
        set => SetValue(MetersProperty, value);
    }

    public IReadOnlyList<BridgeEntry>? Entries
    {
        get => GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MetersProperty)
        {
            if (change.OldValue is MeterSet old) old.Changed -= Redraw;
            if (change.NewValue is MeterSet now && VisualRoot is not null) now.Changed += Redraw;
            InvalidateVisual();
        }
        else if (change.Property == EntriesProperty)
        {
            _clipped = new bool[Entries?.Count ?? 0];
            InvalidateVisual();
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

    private static double Pos(float level) => MeterBank.Position(level);

    public override void Render(DrawingContext ctx)
    {
        var entries = Entries;
        var m = Meters;
        if (entries is null || entries.Count == 0) return;
        if (_clipped.Length != entries.Count) _clipped = new bool[entries.Count];
        if (_clearSeen != MeterSet.ClearStamp)
        {
            _clearSeen = MeterSet.ClearStamp;
            Array.Clear(_clipped);
        }
        var b = Bounds;
        var sections = entries.Select(e => e.Section).Distinct().ToList();
        var usable = b.Width - ScaleW - Gap * (sections.Count - 1);
        var slot = Math.Max(8, usable / entries.Count);
        var top = HeadH + ClipH + 3;
        var meterH = Math.Max(30, b.Height - top - FootH);
        double Y(double db) => top + meterH * (1 - (db - Floor) / -Floor);

        IBrush scaleText = HighContrast ? Brushes.White : new SolidColorBrush(Color.Parse("#6C6F77"));
        IBrush nameText = HighContrast ? Brushes.White : new SolidColorBrush(Color.Parse("#A4A7AE"));
        foreach (var db in Scale)
        {
            var y = Y(db);
            ctx.DrawLine(Tick, new Point(ScaleW - 4, y), new Point(b.Width, y));
            var t = new FormattedText(db == 0 ? "0" : db.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 9, scaleText);
            ctx.DrawText(t, new Point(ScaleW - 6 - t.Width, y - t.Height / 2));
        }
        var y6 = Y(-6);
        var y18 = Y(-18);
        var barSize = Math.Clamp(slot - 4, 3, 16);

        var x = ScaleW;
        string? section = null;
        double sectionStart = x;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Section != section)
            {
                if (section is not null)
                {
                    DrawSection(ctx, section, sectionStart, x, nameText);
                    x += Gap;
                }
                section = e.Section;
                sectionStart = x;
            }
            var cx = x + slot / 2;
            var stereo = e.Right >= 0;
            var w = stereo ? Math.Max(2, barSize / 2 - 0.5) : barSize;
            var bars = stereo ? new[] { (e.Left, cx - w - 0.5), (e.Right, cx + 0.5) } : new[] { (e.Left, cx - w / 2) };
            foreach (var (idx, bx) in bars)
            {
                var level = m is not null && idx >= 0 && idx < m.Count ? m.Levels[idx] : 0f;
                var peak = m is not null && idx >= 0 && idx < m.Count ? m.Peaks[idx] : 0f;
                if (level >= 0.989f) _clipped[i] = true;
                ctx.FillRectangle(Well, new Rect(bx, top, w, meterH), 1.5f);
                var lp = Pos(level);
                if (lp > 0)
                {
                    var yl = top + meterH * (1 - lp);
                    using (ctx.PushClip(new Rect(bx, yl, w, top + meterH - yl)))
                    {
                        ctx.FillRectangle(Low, new Rect(bx, y18, w, top + meterH - y18));
                        ctx.FillRectangle(Mid, new Rect(bx, y6, w, y18 - y6));
                        ctx.FillRectangle(High, new Rect(bx, top, w, y6 - top));
                    }
                }
                var pp = Pos(peak);
                if (pp > 0.02) ctx.FillRectangle(PeakLine, new Rect(bx, top + meterH * (1 - pp), w, 1.5));
            }
            ctx.FillRectangle(_clipped[i] ? High : ClipOff, new Rect(cx - barSize / 2, HeadH, barSize, ClipH), 1);
            ctx.FillRectangle(e.Colour, new Rect(cx - Math.Min(slot - 3, 20) / 2, top + meterH + 3, Math.Min(slot - 3, 20), 2), 1);
            var num = new FormattedText(e.Number, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 9, scaleText)
            {
                MaxTextWidth = Math.Max(8, slot), MaxLineCount = 1, Trimming = TextTrimming.None,
            };
            ctx.DrawText(num, new Point(cx - Math.Min(num.Width, slot) / 2, top + meterH + 7));
            if (slot >= 22)
            {
                var name = new FormattedText(e.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, slot >= 40 ? 10 : 9, nameText)
                {
                    MaxTextWidth = Math.Max(10, slot - 1), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
                };
                ctx.DrawText(name, new Point(cx - Math.Min(name.Width, slot - 1) / 2, top + meterH + 18));
            }
            x += slot;
        }
        if (section is not null) DrawSection(ctx, section, sectionStart, x, nameText);
    }

    private static void DrawSection(DrawingContext ctx, string name, double from, double to, IBrush text)
    {
        ctx.FillRectangle(SectionBack, new Rect(from, 0, Math.Max(0, to - from), HeadH - 3), 4);
        var t = new FormattedText(name.ToUpperInvariant(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 9, text)
        {
            MaxTextWidth = Math.Max(10, to - from - 6), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };
        ctx.DrawText(t, new Point(from + 5, (HeadH - 3 - t.Height) / 2));
    }
}

/// <summary>
/// The stereo correlation as a bar from -1 (left) to +1 (right): green when left and right agree, amber when they are
/// unrelated (wide), red when they fight (the sound thins out or vanishes in mono). Grey when it is too quiet to tell.
/// </summary>
public sealed class PhaseMeter : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<PhaseMeter, double>(nameof(Value), double.NaN);

    static PhaseMeter() => AffectsRender<PhaseMeter>(ValueProperty);

    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#0E0F12"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#3DD68C"));
    private static readonly IBrush Wide = new SolidColorBrush(Color.Parse("#E2BE76"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#FF5A5F"));
    private static readonly IBrush Quiet = new SolidColorBrush(Color.Parse("#4A4D54"));
    private static readonly IPen Centre = new Pen(new SolidColorBrush(Color.Parse("#33FFFFFF")), 1);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static IBrush BrushFor(double v) => double.IsNaN(v) ? Quiet : v < -0.1 ? Bad : v < 0.3 ? Wide : Good;

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        var h = Math.Min(b.Height, 10);
        var y = (b.Height - h) / 2;
        ctx.FillRectangle(Track, new Rect(0, y, b.Width, h), (float)(h / 2));
        var mid = b.Width / 2;
        ctx.DrawLine(Centre, new Point(mid, y - 2), new Point(mid, y + h + 2));
        foreach (var q in new[] { 0.25, 0.75 }) ctx.DrawLine(Centre, new Point(b.Width * q, y + 2), new Point(b.Width * q, y + h - 2));
        var v = Value;
        if (double.IsNaN(v)) return;
        var x = (Math.Clamp(v, -1, 1) + 1) / 2 * b.Width;
        var brush = BrushFor(v);
        ctx.FillRectangle(brush, new Rect(Math.Min(mid, x), y + 2, Math.Abs(x - mid), h - 4), 2);
        ctx.FillRectangle(brush, new Rect(Math.Clamp(x - 3, 0, b.Width - 6), y - 2, 6, h + 4), 3);
    }
}
