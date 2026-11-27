using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nexora.App.Controls;

/// <summary>
/// A colour that knobs, graphs and meters inside a panel draw with instead of the gold accent.
/// Set it once on a panel (<c>c:Paint.Tint="{StaticResource EqTone}"</c>) and everything inside takes it.
/// </summary>
public sealed class Paint : AvaloniaObject
{
    public static readonly AttachedProperty<IBrush?> TintProperty =
        AvaloniaProperty.RegisterAttached<Paint, Control, IBrush?>("Tint", inherits: true);

    public static IBrush? GetTint(Control c) => c.GetValue(TintProperty);
    public static void SetTint(Control c, IBrush? value) => c.SetValue(TintProperty, value);

    private static readonly IBrush Fallback = new SolidColorBrush(Color.Parse("#E2BE76"));

    /// <summary>The colour for this control: its panel's tint, or the app's accent.</summary>
    public static IBrush Of(Control c) =>
        c.GetValue(TintProperty) ?? (c.TryFindResource("Accent", c.ActualThemeVariant, out var a) && a is IBrush b ? b : Fallback);
}
