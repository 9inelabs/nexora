using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Nexora.Audio.Dsp;

namespace NexAudio.App.Controls;

/// <summary>
/// The drum trigger's last hits, newest on the right: a bar for each, as loud as the hit (after the spill filter).
/// Hits that fired are in the section colour with their layer number; hits that were too soft are hollow,
/// so a ghost note that did not play stands out at once. The dashed line is the threshold.
/// </summary>
public sealed class HitHistory : Control
{
    public static readonly StyledProperty<TriggerHit[]?> HitsProperty = AvaloniaProperty.Register<HitHistory, TriggerHit[]?>(nameof(Hits));
    public static readonly StyledProperty<double> ThresholdProperty = AvaloniaProperty.Register<HitHistory, double>(nameof(Threshold), -24);
    public static readonly StyledProperty<int> LayersProperty = AvaloniaProperty.Register<HitHistory, int>(nameof(Layers), 1);
    public static readonly StyledProperty<double> RateProperty = AvaloniaProperty.Register<HitHistory, double>(nameof(Rate), 48000);

    static HitHistory() => AffectsRender<HitHistory>(HitsProperty, ThresholdProperty, LayersProperty);

    public TriggerHit[]? Hits { get => GetValue(HitsProperty); set => SetValue(HitsProperty, value); }
    public double Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public int Layers { get => GetValue(LayersProperty); set => SetValue(LayersProperty, value); }
    public double Rate { get => GetValue(RateProperty); set => SetValue(RateProperty, value); }

    private const int Shown = 32;

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(GraphKit.Back, b, 8);
        using var clip = ctx.PushClip(b);
        const double lo = -60;
        var w = b.Width;
        var h = b.Height;
        const double left = 30, bottom = 18;
        double Y(double db) => (h - bottom) - (Math.Clamp(db, lo, 0) - lo) / -lo * (h - bottom - 8);

        for (var db = -50.0; db < 0; db += 10)
        {
            ctx.DrawLine(GraphKit.Grid, new Point(left, Y(db)), new Point(w, Y(db)));
            ctx.DrawText(GraphKit.Text($"{db:0}"), new Point(4, Y(db) - 6));
        }
        var accent = GraphKit.Accent(this);

        // The band where hits are shown as too soft, under the bars.
        var ty = Y(Threshold);
        var nearY = Y(Threshold - DrumTrigger.NearDb);
        ctx.FillRectangle(GraphKit.Tint(accent, 18), new Rect(left, ty, w - left, Math.Max(0, nearY - ty)));
        var layers = Math.Max(1, Layers);

        var hits = Hits ?? [];
        var start = Math.Max(0, hits.Length - Shown);
        var slot = (w - left - 8) / Shown;
        var bar = Math.Max(3, slot * 0.6);
        if (hits.Length == 0)
        {
            var t = GraphKit.Text("Hits show here as the drummer plays", 10);
            ctx.DrawText(t, new Point(left + (w - left - t.Width) / 2, (h - bottom) / 2 + 10));
        }
        var hollow = new Pen(GraphKit.Off, 1.2) { DashStyle = new DashStyle([2, 2], 0) };
        for (var i = start; i < hits.Length; i++)
        {
            var hit = hits[i];
            var x = left + 4 + (i - start + (Shown - (hits.Length - start))) * slot + (slot - bar) / 2;
            var top = Y(hit.Level);
            var r = new Rect(x, top, bar, Math.Max(1, (h - bottom) - top));
            if (hit.Kind == HitKind.Fired)
            {
                ctx.FillRectangle(GraphKit.Tint(accent, i == hits.Length - 1 ? (byte)255 : (byte)190), r, 2);
                if (layers > 1 && bar >= 8)
                {
                    var n = GraphKit.Text($"{hit.Layer + 1}", 8.5, GraphKit.Back);
                    ctx.DrawText(n, new Point(x + (bar - n.Width) / 2, top + 2));
                }
            }
            else ctx.DrawRectangle(null, hollow, r, 2);
        }
        // How hard counts as each layer, and the threshold, drawn over the bars so they always read.
        for (var i = 1; i < layers; i++)
        {
            var ly = Y(Threshold + DrumTrigger.SpanDb * i / layers);
            ctx.DrawLine(new Pen(GraphKit.Tint(accent, 70), 1) { DashStyle = new DashStyle([1, 3], 0) }, new Point(left, ly), new Point(w, ly));
            var lt = GraphKit.Text($"layer {i + 1}", 8.5, GraphKit.Tint(accent, 200));
            ctx.FillRectangle(GraphKit.Back, new Rect(left + 2, ly - lt.Height - 1, lt.Width + 6, lt.Height), 3);
            ctx.DrawText(lt, new Point(left + 5, ly - lt.Height - 1));
        }
        ctx.DrawLine(new Pen(GraphKit.Tint(accent, 200), 1.2) { DashStyle = new DashStyle([4, 3], 0) }, new Point(left, ty), new Point(w, ty));
        var tt = GraphKit.Text($"threshold {Threshold:0} dB", 9, GraphKit.Tint(accent, 230));
        ctx.FillRectangle(GraphKit.Back, new Rect(left + 2, ty + 2, tt.Width + 6, tt.Height), 3);
        ctx.DrawText(tt, new Point(left + 5, ty + 2));
        // How far apart the last two hits were (fast rolls and flams want a short hold).
        if (hits.Length >= 2)
        {
            var gap = (hits[^1].Sample - hits[^2].Sample) * 1000.0 / Math.Max(1, Rate);
            var g = GraphKit.Text($"last gap {gap:0} ms", 9);
            ctx.DrawText(g, new Point(w - g.Width - 6, h - bottom + 3));
        }
        var lbl = GraphKit.Text($"last {Shown} hits", 9);
        ctx.DrawText(lbl, new Point(left + 4, h - bottom + 3));
    }
}
