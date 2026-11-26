using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nexora.App.Controls;

/// <summary>The green, yellow and red bar on the Minister Timer, sized by how long each colour lasts.</summary>
public sealed class ColourBar : Control
{
    public static readonly StyledProperty<double> GreenProperty = AvaloniaProperty.Register<ColourBar, double>(nameof(Green), 1);
    public static readonly StyledProperty<double> YellowProperty = AvaloniaProperty.Register<ColourBar, double>(nameof(Yellow), 1);
    public static readonly StyledProperty<double> RedProperty = AvaloniaProperty.Register<ColourBar, double>(nameof(Red), 1);

    static ColourBar() => AffectsRender<ColourBar>(GreenProperty, YellowProperty, RedProperty);

    public double Green { get => GetValue(GreenProperty); set => SetValue(GreenProperty, value); }
    public double Yellow { get => GetValue(YellowProperty); set => SetValue(YellowProperty, value); }
    public double Red { get => GetValue(RedProperty); set => SetValue(RedProperty, value); }

    public override void Render(DrawingContext context)
    {
        var b = Bounds;
        var total = Math.Max(0.001, Green + Yellow + Red);
        var h = b.Height;
        var r = h / 2;
        using (context.PushClip(new RoundedRect(new Rect(0, 0, b.Width, h), r)))
        {
            var x = 0.0;
            foreach (var (share, brush) in new[] { (Green, MinisterRenderer.Green), (Yellow, MinisterRenderer.Yellow), (Red, MinisterRenderer.Red) })
            {
                var w = b.Width * share / total;
                context.FillRectangle(brush, new Rect(x, 0, Math.Max(0, w - 2), h));
                x += w;
            }
        }
    }
}
