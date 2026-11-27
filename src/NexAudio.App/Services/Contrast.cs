using Avalonia;
using Avalonia.Media;

namespace NexAudio.App.Services;

/// <summary>
/// The high-contrast look: a black ground, white writing and brighter lines, for a bright room or tired eyes.
/// It changes the theme's own colours in place, so every page follows at once.
/// </summary>
public static class Contrast
{
    private static readonly (string Key, string High)[] Swaps =
    [
        ("Ground", "#000000"), ("Surface1", "#000000"), ("Surface2", "#0D0D0F"), ("Surface3", "#1E1F23"), ("Surface4", "#2E3036"),
        ("Text1", "#FFFFFF"), ("Text2", "#F0F0F0"), ("Text3", "#CFCFD4"),
        ("Line", "#59FFFFFF"), ("Line2", "#8CFFFFFF"),
        ("TextControlBackground", "#0D0D0F"), ("TextControlBorderBrush", "#8CFFFFFF"),
    ];

    private static readonly Dictionary<string, Color> Normal = [];
    public static bool On { get; private set; }
    /// <summary>The fader strips' background in high contrast.</summary>
    public static readonly IBrush StripCard = new SolidColorBrush(Color.Parse("#0A0A0C"));

    public static void Apply(bool on)
    {
        if (Application.Current is not { } app) return;
        On = on;
        foreach (var (key, high) in Swaps)
        {
            if (!app.TryGetResource(key, app.ActualThemeVariant, out var r) || r is not SolidColorBrush b) continue;
            if (!Normal.ContainsKey(key)) Normal[key] = b.Color;
            try { b.Color = on ? Color.Parse(high) : Normal[key]; }
            catch (InvalidOperationException) { } // a frozen brush: leave it
        }
    }
}
