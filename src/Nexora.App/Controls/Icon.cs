using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Nexora.App.Controls;

/// <summary>A thin line icon (24×24 grid, round caps), drawn in the current text colour.</summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<double> StrokeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Stroke), 1.7);

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty, StrokeProperty);
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Kind is null || !Icons.TryGet(Kind, out var geometry)) return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var scale = size / 24.0;
        var dx = (Bounds.Width - size) / 2;
        var dy = (Bounds.Height - size) / 2;
        var pen = new Pen(Foreground ?? Brushes.White, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            context.DrawGeometry(null, pen, geometry);
    }
}

public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["scripture"] = "M6 3 H18 A2 2 0 0 1 20 5 V19 A2 2 0 0 1 18 21 H6 A2 2 0 0 1 4 19 V5 A2 2 0 0 1 6 3 Z M12 8 V14 M9 11 H15",
        ["lyrics"] = "M9 18 V5 L21 3 V16 M6 15 A3 3 0 1 0 6 21 A3 3 0 1 0 6 15 Z M18 13 A3 3 0 1 0 18 19 A3 3 0 1 0 18 13 Z",
        ["camera"] = "M16 13 L21.2 16.5 A0.5 0.5 0 0 0 22 16.1 V7.9 A0.5 0.5 0 0 0 21.2 7.5 L16 11 M4 6 H14 A2 2 0 0 1 16 8 V16 A2 2 0 0 1 14 18 H4 A2 2 0 0 1 2 16 V8 A2 2 0 0 1 4 6 Z",
        ["announcements"] = "M11 5 L6 9 H2 V15 H6 L11 19 Z M15.5 8.5 A5 5 0 0 1 15.5 15.5 M19 5 A10 10 0 0 1 19 19",
        ["lowerthirds"] = "M4 4 H20 A2 2 0 0 1 22 6 V18 A2 2 0 0 1 20 20 H4 A2 2 0 0 1 2 18 V6 A2 2 0 0 1 4 4 Z M6 15 H14 M6 12 H10",
        ["countdown"] = "M10 2 H14 M12 14 L15 11 M12 6 A8 8 0 1 0 12 22 A8 8 0 1 0 12 6 Z",
        ["minister"] = "M12 8 V12 L14.5 14.5 M12 4 A8 8 0 1 0 12 20 A8 8 0 1 0 12 4 Z M5 3 L2 6 M19 3 L22 6",
        ["live"] = "M4.9 19.1 C1 15.2 1 8.8 4.9 4.9 M7.8 16.2 C5.5 13.9 5.5 10.1 7.8 7.7 M16.2 7.8 C18.5 10.1 18.5 13.9 16.2 16.3 M19.1 4.9 C23 8.8 23 15.1 19.1 19 M12 10 A2 2 0 1 0 12 14 A2 2 0 1 0 12 10 Z",
        ["settings"] = "M12 9 A3 3 0 1 0 12 15 A3 3 0 1 0 12 9 Z M12 2 V4.5 M12 19.5 V22 M4.9 4.9 L6.7 6.7 M17.3 17.3 L19.1 19.1 M2 12 H4.5 M19.5 12 H22 M4.9 19.1 L6.7 17.3 M17.3 6.7 L19.1 4.9",
        ["signout"] = "M15 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H15 M10 17 L15 12 L10 7 M15 12 H3",
        ["pin"] = "M12 17 V22 M9 4 H15 M10 4 V10.5 L7 14 V16 H17 V14 L14 10.5 V4",
        ["takedown"] = "M12 3 V15 M7 10 L12 15 L17 10 M5 21 H19",
        ["plus"] = "M12 5 V19 M5 12 H19",
        ["check"] = "M5 12.5 L10 17.5 L19.5 7",
        ["x"] = "M18 6 L6 18 M6 6 L18 18",
        ["monitor"] = "M4 4 H20 A2 2 0 0 1 22 6 V15 A2 2 0 0 1 20 17 H4 A2 2 0 0 1 2 15 V6 A2 2 0 0 1 4 4 Z M8 21 H16 M12 17 V21",
        ["stage"] = "M3 19 H21 M5 19 L7 9 H17 L19 19 M9 9 V6 H15 V9",
        ["users"] = "M16 21 V19 A4 4 0 0 0 12 15 H6 A4 4 0 0 0 2 19 V21 M9 3 A4 4 0 1 0 9 11 A4 4 0 1 0 9 3 Z M22 21 V19 A4 4 0 0 0 19 15.1 M16 3.1 A4 4 0 0 1 16 10.9",
        ["network"] = "M9 2 H15 V8 H9 Z M2 16 H8 V22 H2 Z M16 16 H22 V22 H16 Z M5 16 V13 H19 V16 M12 13 V8",
        ["palette"] = "M12 2 A10 10 0 1 0 13 22 A2 2 0 0 0 14.5 18.7 A2 2 0 0 1 16 15.3 H18.5 C20.5 15.3 22 14 22 12 A10 10 0 0 0 12 2 Z M7.5 10.5 L7.6 10.5 M12 7.5 L12.1 7.5 M16.5 10.5 L16.6 10.5",
        ["pc"] = "M3 4 H21 V15 H3 Z M8 20 H16 M12 15 V20",
        ["eye"] = "M2 12 C2 12 5.5 5 12 5 C18.5 5 22 12 22 12 C22 12 18.5 19 12 19 C5.5 19 2 12 2 12 Z M12 9 A3 3 0 1 0 12 15 A3 3 0 1 0 12 9 Z",
        ["square"] = "M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z",
        ["upload"] = "M12 15 V3 M7 8 L12 3 L17 8 M21 15 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V15",
        ["trash"] = "M3 6 H21 M8 6 V4 H16 V6 M6 6 L7 21 H17 L18 6",
        ["shield"] = "M12 22 C12 22 20 18 20 12 V5 L12 2 L4 5 V12 C4 18 12 22 12 22 Z",
        ["refresh"] = "M21 12 A9 9 0 1 1 18.4 5.6 M21 3 V9 H15",
        ["arrowup"] = "M12 19 V5 M5 12 L12 5 L19 12",
        ["arrowdown"] = "M12 5 V19 M5 12 L12 19 L19 12",
        ["send"] = "M5 12 H19 M13 6 L19 12 L13 18",
        ["clear"] = "M4 4 L20 20 M9 4 H20 V15 M4 9 V20 H15",
        ["search"] = "M11 4 A7 7 0 1 0 11 18 A7 7 0 1 0 11 4 Z M21 21 L16 16",
        ["key"] = "M7.5 13 A4.5 4.5 0 1 0 7.5 22 A4.5 4.5 0 1 0 7.5 13 Z M10.7 14.3 L21 4 M17 8 L20 11 M15 10 L17 12",
        ["wifi"] = "M5 12.5 A10 10 0 0 1 19 12.5 M8.5 16 A5 5 0 0 1 15.5 16 M12 19.5 L12.01 19.5 M2 9 A15 15 0 0 1 22 9",
        ["info"] = "M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z M12 16 V12 M12 8 L12.01 8",
        ["play"] = "M7 4 L19 12 L7 20 Z",
        ["pause"] = "M7 4 H10 V20 H7 Z M14 4 H17 V20 H14 Z",
        ["stop"] = "M6 6 H18 V18 H6 Z",
        ["restart"] = "M3 12 A9 9 0 1 0 5.6 5.6 M3 3 V9 H9",
        ["record"] = "M12 6 A6 6 0 1 0 12 18 A6 6 0 1 0 12 6 Z",
        ["film"] = "M3 4 H21 V20 H3 Z M7 4 V20 M17 4 V20 M3 8 H7 M3 12 H7 M3 16 H7 M17 8 H21 M17 12 H21 M17 16 H21",
        ["image"] = "M3 4 H21 V20 H3 Z M8 9 A1.5 1.5 0 1 0 8 12 A1.5 1.5 0 1 0 8 9 Z M21 15 L16 10 L5 20",
        ["edit"] = "M4 20 H8 L19 9 L15 5 L4 16 Z M13 7 L17 11",
        ["download"] = "M12 3 V15 M7 10 L12 15 L17 10 M21 15 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V15",
        ["list"] = "M8 6 H21 M8 12 H21 M8 18 H21 M3 6 H3.01 M3 12 H3.01 M3 18 H3.01",
        ["broadcast"] = "M12 11 A1 1 0 1 0 12 13 A1 1 0 1 0 12 11 Z M8 8 A5.5 5.5 0 0 0 8 16 M16 8 A5.5 5.5 0 0 1 16 16 M5 5 A9.5 9.5 0 0 0 5 19 M19 5 A9.5 9.5 0 0 1 19 19",
        ["logo"] = "M12 3 L21 12 L12 21 L3 12 Z",
        ["moon"] = "M21 12.8 A9 9 0 1 1 11.2 3 A7 7 0 0 0 21 12.8 Z",
        ["faders"] = "M6 3 V21 M12 3 V21 M18 3 V21 M3.5 14 H8.5 M9.5 8 H14.5 M15.5 16 H20.5",
        ["heart"] = "M12 21 C12 21 3 15 3 8.5 A4.5 4.5 0 0 1 12 6 A4.5 4.5 0 0 1 21 8.5 C21 15 12 21 12 21 Z",
    };

    private static readonly Dictionary<string, Geometry> Cache = new();

    public static bool TryGet(string kind, out Geometry geometry)
    {
        if (Cache.TryGetValue(kind, out geometry!)) return true;
        if (!Paths.TryGetValue(kind, out var data)) return false;
        geometry = Geometry.Parse(data);
        Cache[kind] = geometry;
        return true;
    }

    public static string ForSection(Nexora.Core.Model.SectionKind kind) => kind switch
    {
        Nexora.Core.Model.SectionKind.Scripture => "scripture",
        Nexora.Core.Model.SectionKind.Lyrics => "lyrics",
        Nexora.Core.Model.SectionKind.Camera => "camera",
        Nexora.Core.Model.SectionKind.Announcements => "announcements",
        Nexora.Core.Model.SectionKind.LowerThirds => "lowerthirds",
        Nexora.Core.Model.SectionKind.Countdown => "countdown",
        Nexora.Core.Model.SectionKind.MinisterTimer => "minister",
        Nexora.Core.Model.SectionKind.Audio => "faders",
        _ => "square",
    };
}
