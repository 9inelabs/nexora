namespace Nexora.App.Controls;

/// <summary>
/// The size of the screen being laid out. Every screen is laid out in its own shape: its shorter side is
/// 1080 units, so a 16:9 screen is 1920×1080 and a square LED wall is 1080×1080. Renderers read
/// <see cref="W"/> and <see cref="H"/>; a screen sets them with <see cref="Use"/> while it builds.
/// Outside a build they follow the main audience screen, so previews and slide splitting match it.
/// </summary>
public static class CanvasSize
{
    [ThreadStatic] private static (double W, double H)? _current;

    /// <summary>The main audience screen's shape (set from the Live Output PC's screen list).</summary>
    public static (double W, double H) Audience { get; set; } = (1920, 1080);

    /// <summary>The first Stage Monitor's shape.</summary>
    public static (double W, double H) Stage { get; set; } = (1920, 1080);

    public static double W => (_current ?? Audience).W;
    public static double H => (_current ?? Audience).H;

    /// <summary>Lays out in this size until the returned scope is disposed.</summary>
    public static IDisposable Use(double w, double h)
    {
        var before = _current;
        _current = (w, h);
        return new Scope(() => _current = before);
    }

    private sealed class Scope(Action undo) : IDisposable
    {
        private Action? _undo = undo;
        public void Dispose() { _undo?.Invoke(); _undo = null; }
    }
}
