using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace NexAudio.App.Controls;

/// <summary>What one crossing in the routing grid shows.</summary>
public enum RouteState
{
    /// <summary>Can be routed, and is not.</summary>
    Free,
    /// <summary>Routed and sounding.</summary>
    On,
    /// <summary>There is a send, but it is switched off or all the way down.</summary>
    Off,
    /// <summary>This kind of strip cannot feed that kind of bus.</summary>
    Never,
    /// <summary>It would make a feedback loop.</summary>
    Loop,
}

public sealed record RouteEnd(string Id, string Label, string Group, Color Colour);

/// <summary>Everything the routing grid draws: sources down the side, destinations across the top, and each crossing.</summary>
public sealed class RouteMap
{
    public RouteMap(IReadOnlyList<RouteEnd> rows, IReadOnlyList<RouteEnd> cols)
    {
        Rows = rows;
        Cols = cols;
        States = new RouteState[rows.Count, cols.Count];
        Labels = new string?[rows.Count, cols.Count];
        Tips = new string[rows.Count, cols.Count];
    }

    public IReadOnlyList<RouteEnd> Rows { get; }
    public IReadOnlyList<RouteEnd> Cols { get; }
    public RouteState[,] States { get; }
    /// <summary>A few characters inside an "on" crossing (the send level, or PRE).</summary>
    public string?[,] Labels { get; }
    public string[,] Tips { get; }
    /// <summary>The strip being traced, and everything its sound passes through or comes from.</summary>
    public string? Traced { get; set; }
    public HashSet<string> TraceDown { get; set; } = [];
    public HashSet<string> TraceUp { get; set; } = [];
}

/// <summary>
/// The routing grid: every channel and bus down the left, every bus they can feed across the top. A tap on a crossing
/// makes or removes the route; a tap on a name traces that strip's whole path.
/// </summary>
public sealed class RouteGrid : Control
{
    public const double RowH = 24, ColW = 44, LeftW = 150, TopH = 104;

    public static readonly StyledProperty<RouteMap?> MapProperty = AvaloniaProperty.Register<RouteGrid, RouteMap?>(nameof(Map));

    public RouteMap? Map
    {
        get => GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    /// <summary>A crossing was tapped (row, column).</summary>
    public event Action<int, int>? CellClicked;
    /// <summary>A name was tapped: trace it (row or column index, and whether it was a column).</summary>
    public event Action<string>? Trace;
    /// <summary>The pointer is over a crossing or name: what it means, for the line under the grid.</summary>
    public event Action<string>? Hover;

    private (int R, int C) _hot = (-1, -1);

    static RouteGrid()
    {
        AffectsRender<RouteGrid>(MapProperty);
        AffectsMeasure<RouteGrid>(MapProperty);
    }

    public RouteGrid()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var m = Map;
        return m is null ? default : new Size(LeftW + m.Cols.Count * ColW + 8, TopH + m.Rows.Count * RowH + 4);
    }

    private (int R, int C) Hit(Point p)
    {
        var m = Map;
        if (m is null) return (-1, -1);
        var c = p.X < LeftW ? -1 : (int)((p.X - LeftW) / ColW);
        var r = p.Y < TopH ? -1 : (int)((p.Y - TopH) / RowH);
        if (c >= m.Cols.Count) c = -2;
        if (r >= m.Rows.Count) r = -2;
        return (r, c);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var m = Map;
        var h = Hit(e.GetPosition(this));
        if (h == _hot || m is null) return;
        _hot = h;
        InvalidateVisual();
        if (h.R >= 0 && h.C >= 0) Hover?.Invoke(m.Tips[h.R, h.C]);
        else if (h.R >= 0 && h.C == -1) Hover?.Invoke($"{m.Rows[h.R].Label}: tap to trace where its sound goes.");
        else if (h.C >= 0 && h.R == -1) Hover?.Invoke($"{m.Cols[h.C].Label}: tap to trace what feeds it.");
        else Hover?.Invoke("");
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hot = (-1, -1);
        InvalidateVisual();
        Hover?.Invoke("");
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var m = Map;
        if (m is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var (r, c) = Hit(e.GetPosition(this));
        if (r >= 0 && c >= 0) CellClicked?.Invoke(r, c);
        else if (r >= 0 && c == -1) Trace?.Invoke(m.Rows[r].Id);
        else if (c >= 0 && r == -1) Trace?.Invoke(m.Cols[c].Id);
        e.Handled = true;
    }

    private IBrush R(string key, string fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : new SolidColorBrush(Color.Parse(fallback));

    private static IBrush Solid(Color c, byte alpha = 255) => new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, weight), size, brush);

    public override void Render(DrawingContext ctx)
    {
        var m = Map;
        if (m is null) return;
        var text1 = R("Text1", "#F2F2F0");
        var text3 = R("Text3", "#6C6F77");
        var surface2 = R("Surface2", "#1F2125");
        var surface3 = R("Surface3", "#272A2F");
        var line = new Pen(R("Line2", "#1FFFFFFF"), 1);
        var loopPen = new Pen(Solid(Color.Parse("#FF5A5F"), 110), 1.2);
        var traced = m.Traced;
        bool InTrace(string id) => id == traced || m.TraceDown.Contains(id) || m.TraceUp.Contains(id);
        var tracing = traced is not null;
        var w = LeftW + m.Cols.Count * ColW;
        var h = TopH + m.Rows.Count * RowH;

        // Row bands: kind groups alternate, the traced path lights up.
        string? lastGroup = null;
        var shade = false;
        for (var r = 0; r < m.Rows.Count; r++)
        {
            var row = m.Rows[r];
            if (row.Group != lastGroup)
            {
                shade = !shade;
                if (lastGroup is not null) ctx.DrawLine(line, new Point(0, TopH + r * RowH), new Point(w, TopH + r * RowH));
                lastGroup = row.Group;
            }
            var y = TopH + r * RowH;
            var rect = new Rect(0, y, w, RowH);
            if (tracing && InTrace(row.Id)) ctx.FillRectangle(Solid(row.Colour, row.Id == traced ? (byte)60 : (byte)28), rect);
            else if (shade) ctx.FillRectangle(surface2, rect);
            if (r == _hot.R) ctx.FillRectangle(Solid(Colors.White, 10), rect);
            // Name: a colour chip, then the label.
            ctx.FillRectangle(Solid(row.Colour), new Rect(4, y + 6, 4, RowH - 12), 2);
            var label = Text(row.Label, 12, tracing && !InTrace(row.Id) ? text3 : text1, row.Id == traced ? FontWeight.Bold : FontWeight.Normal);
            label.MaxTextWidth = LeftW - 18;
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            ctx.DrawText(label, new Point(14, y + (RowH - label.Height) / 2));
        }

        // Column heads: the name written upwards, with a colour bar.
        lastGroup = null;
        for (var c = 0; c < m.Cols.Count; c++)
        {
            var col = m.Cols[c];
            var x = LeftW + c * ColW;
            if (col.Group != lastGroup)
            {
                if (lastGroup is not null) ctx.DrawLine(line, new Point(x, 0), new Point(x, h));
                lastGroup = col.Group;
            }
            if (tracing && InTrace(col.Id)) ctx.FillRectangle(Solid(col.Colour, col.Id == traced ? (byte)60 : (byte)24), new Rect(x, 0, ColW, h));
            if (c == _hot.C) ctx.FillRectangle(Solid(Colors.White, 10), new Rect(x, 0, ColW, h));
            ctx.FillRectangle(Solid(col.Colour), new Rect(x + 6, TopH - 8, ColW - 12, 3), 1.5f);
            var t = Text(col.Label, 11.5, tracing && !InTrace(col.Id) ? text3 : text1, col.Id == traced ? FontWeight.Bold : FontWeight.SemiBold);
            t.MaxTextWidth = TopH - 16;
            t.MaxLineCount = 1;
            t.Trimming = TextTrimming.CharacterEllipsis;
            using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(x + (ColW - t.Height) / 2, TopH - 14)))
                ctx.DrawText(t, new Point(0, 0));
        }
        ctx.DrawLine(line, new Point(LeftW, TopH - 2), new Point(w, TopH - 2));

        // The crossings.
        for (var r = 0; r < m.Rows.Count; r++)
            for (var c = 0; c < m.Cols.Count; c++)
            {
                var x = LeftW + c * ColW;
                var y = TopH + r * RowH;
                var cell = new Rect(x + 4, y + 3, ColW - 8, RowH - 6);
                var colour = m.Cols[c].Colour;
                var hot = r == _hot.R && c == _hot.C;
                switch (m.States[r, c])
                {
                    case RouteState.On:
                        ctx.FillRectangle(Solid(colour, hot ? (byte)255 : (byte)225), cell, 4);
                        if (m.Labels[r, c] is { Length: > 0 } l)
                        {
                            var ft = Text(l, 10, new SolidColorBrush(Color.Parse("#15161A")), FontWeight.Bold);
                            ctx.DrawText(ft, new Point(cell.Center.X - ft.Width / 2, cell.Center.Y - ft.Height / 2));
                        }
                        break;
                    case RouteState.Off:
                        ctx.DrawRectangle(null, new Pen(Solid(colour, 170), 1.3), cell.Deflate(1), 4, 4);
                        if (hot) ctx.FillRectangle(Solid(colour, 40), cell, 4);
                        break;
                    case RouteState.Free:
                        if (hot) ctx.DrawRectangle(Solid(colour, 50), new Pen(Solid(colour, 160), 1), cell.Deflate(1), 4, 4);
                        else ctx.FillRectangle(surface3, new Rect(cell.Center.X - 2, cell.Center.Y - 2, 4, 4), 2);
                        break;
                    case RouteState.Loop:
                        var cx = cell.Center.X;
                        var cy = cell.Center.Y;
                        ctx.DrawLine(loopPen, new Point(cx - 3, cy - 3), new Point(cx + 3, cy + 3));
                        ctx.DrawLine(loopPen, new Point(cx - 3, cy + 3), new Point(cx + 3, cy - 3));
                        break;
                }
            }
    }
}
