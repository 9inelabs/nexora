using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nexora.App.Controls;

/// <summary>
/// The levels for a row of channels, smoothed for the eye: a meter jumps up at once and falls back
/// slowly, and a thin line holds the recent peak for a moment.
/// </summary>
public sealed class MeterSet
{
    private const float FallPerFrame = 0.88f; // about 20 dB a second at 30 frames a second
    private const int HoldFrames = 45;        // a peak line stays 1.5 seconds

    public MeterSet(int count)
    {
        Resize(count);
    }

    public int Count { get; private set; }
    public float[] Levels { get; private set; } = [];
    public float[] Peaks { get; private set; } = [];
    public bool[] Clips { get; private set; } = [];
    public string[] Labels { get; private set; } = [];
    public IBrush?[] Colours { get; private set; } = [];
    private int[] _hold = [];

    public event Action? Changed;

    public void Resize(int count)
    {
        if (count == Count) return;
        Count = count;
        Levels = new float[count];
        Peaks = new float[count];
        Clips = new bool[count];
        _hold = new int[count];
        var labels = new string[count];
        Array.Copy(Labels, labels, Math.Min(Labels.Length, count));
        Labels = labels;
        Colours = new IBrush?[count];
        Changed?.Invoke();
    }

    public void SetLabel(int i, string label, IBrush? colour)
    {
        if (i < 0 || i >= Count) return;
        Labels[i] = label;
        Colours[i] = colour;
    }

    /// <summary>New peaks from the engine (0 to 1).</summary>
    public void Push(float[] peaks, bool[] clips)
    {
        if (peaks.Length != Count) Resize(peaks.Length);
        for (var i = 0; i < Count; i++)
        {
            var p = peaks[i];
            Levels[i] = p >= Levels[i] ? p : Math.Max(p, Levels[i] * FallPerFrame);
            if (p >= Peaks[i])
            {
                Peaks[i] = p;
                _hold[i] = HoldFrames;
            }
            else if (--_hold[i] <= 0) Peaks[i] = Math.Max(Levels[i], Peaks[i] * FallPerFrame);
            Clips[i] = i < clips.Length && clips[i];
        }
        Changed?.Invoke();
    }

    /// <summary>Everything falls to silence (the engine stopped).</summary>
    public void Silence()
    {
        Array.Clear(Levels);
        Array.Clear(Peaks);
        Array.Clear(Clips);
        Changed?.Invoke();
    }
}

/// <summary>A row of channel meters with the channel number and the X32's name under each.</summary>
public sealed class MeterBank : Control
{
    public static readonly StyledProperty<MeterSet?> MetersProperty = AvaloniaProperty.Register<MeterBank, MeterSet?>(nameof(Meters));

    private static readonly IBrush Well = new SolidColorBrush(Color.Parse("#0E0F12"));
    private static readonly IBrush Low = new SolidColorBrush(Color.Parse("#3DD68C"));
    private static readonly IBrush Mid = new SolidColorBrush(Color.Parse("#E2BE76"));
    private static readonly IBrush High = new SolidColorBrush(Color.Parse("#FF5A5F"));
    private static readonly IBrush ClipOff = new SolidColorBrush(Color.Parse("#2A2C31"));
    private static readonly IBrush Number = new SolidColorBrush(Color.Parse("#6C6F77"));
    private static readonly IBrush Name = new SolidColorBrush(Color.Parse("#A4A7AE"));
    private static readonly IBrush PeakLine = new SolidColorBrush(Color.Parse("#F2F2F0"));
    private static readonly IPen Tick = new Pen(new SolidColorBrush(Color.Parse("#1FFFFFFF")), 1);
    private static readonly Typeface Face = new(new FontFamily("avares://Nexora/Assets/Fonts#Manrope"), FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface Mono = new(new FontFamily("avares://Nexora/Assets/Fonts#JetBrains Mono"));

    private const double LabelHeight = 34;
    private const double ClipHeight = 5;
    private const double Floor = -60;

    public MeterSet? Meters
    {
        get => GetValue(MetersProperty);
        set => SetValue(MetersProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MetersProperty)
        {
            if (change.OldValue is MeterSet old) old.Changed -= Redraw;
            if (change.NewValue is MeterSet now) now.Changed += Redraw;
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Meters is { } m) m.Changed -= Redraw;
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

    private void Redraw() => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual, Avalonia.Threading.DispatcherPriority.Render);

    /// <summary>Where a level (0 to 1) sits on the meter, 0 at the bottom (-60 dB) to 1 at the top (0 dB).</summary>
    public static double Position(float level)
    {
        if (level <= 0) return 0;
        var db = 20 * Math.Log10(level);
        return Math.Clamp((db - Floor) / -Floor, 0, 1);
    }

    public override void Render(DrawingContext ctx)
    {
        var m = Meters;
        if (m is null || m.Count == 0) return;
        var b = Bounds;
        var slot = b.Width / m.Count;
        var barW = Math.Max(3, Math.Min(18, slot - 6));
        var top = ClipHeight + 3;
        var meterH = Math.Max(20, b.Height - LabelHeight - top);

        // Scale lines at 0, -6, -18 and -40 dB.
        foreach (var db in new[] { 0.0, -6, -18, -40 })
        {
            var y = top + meterH * (1 - (db - Floor) / -Floor);
            ctx.DrawLine(Tick, new Point(0, y), new Point(b.Width, y));
        }

        var y6 = top + meterH * (1 - (-6 - Floor) / -Floor);
        var y18 = top + meterH * (1 - (-18 - Floor) / -Floor);
        var nameSize = slot >= 44 ? 11 : 10;

        for (var i = 0; i < m.Count; i++)
        {
            var cx = slot * i + slot / 2;
            var x = cx - barW / 2;
            ctx.FillRectangle(m.Clips[i] ? High : ClipOff, new Rect(x, 0, barW, ClipHeight), 1);
            ctx.FillRectangle(Well, new Rect(x, top, barW, meterH), 2);

            var level = Position(m.Levels[i]);
            if (level > 0)
            {
                var yLevel = top + meterH * (1 - level);
                using (ctx.PushClip(new Rect(x, yLevel, barW, top + meterH - yLevel)))
                {
                    ctx.FillRectangle(Low, new Rect(x, y18, barW, top + meterH - y18));
                    ctx.FillRectangle(Mid, new Rect(x, y6, barW, y18 - y6));
                    ctx.FillRectangle(High, new Rect(x, top, barW, y6 - top));
                }
            }
            var peak = Position(m.Peaks[i]);
            if (peak > 0.02)
            {
                var yp = top + meterH * (1 - peak);
                ctx.FillRectangle(PeakLine, new Rect(x, yp, barW, 1.5));
            }

            var num = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10, Number);
            ctx.DrawText(num, new Point(cx - num.Width / 2, top + meterH + 4));

            if (m.Colours[i] is { } colour)
                ctx.FillRectangle(colour, new Rect(cx - Math.Min(slot - 4, 22) / 2, top + meterH + 17, Math.Min(slot - 4, 22), 2), 1);

            var label = m.Labels[i];
            if (!string.IsNullOrEmpty(label))
            {
                var name = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, nameSize, Name)
                {
                    MaxTextWidth = Math.Max(10, slot - 2),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                ctx.DrawText(name, new Point(cx - Math.Min(name.Width, slot - 2) / 2, top + meterH + 20));
            }
        }
    }
}
