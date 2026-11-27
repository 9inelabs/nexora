using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Nexora.App.Controls;

/// <summary>Small converters used in the views.</summary>
public static class Conv
{
    /// <summary>True → the gold accent border (the current scene), false → none.</summary>
    public static readonly IValueConverter CurrentBorder = new FuncValueConverter<bool, IBrush>(b => b ? new SolidColorBrush(Color.Parse("#E2BE76")) : Brushes.Transparent);

    /// <summary>True → 0.45 (faded), false → 1.</summary>
    /// <summary>"Yes text|No text" chosen by a true or false value.</summary>
    public static readonly IValueConverter YesNo = new FuncValueConverter<bool, string, string>((b, p) =>
    {
        var parts = (p ?? "Yes|No").Split('|');
        return b ? parts[0] : parts.Length > 1 ? parts[1] : "";
    });

    public static readonly IValueConverter LessThan = new FuncValueConverter<int, string, bool>((n, p) => n < int.Parse(p ?? "0", System.Globalization.CultureInfo.InvariantCulture));
    public static readonly IValueConverter AtLeast = new FuncValueConverter<int, string, bool>((n, p) => n >= int.Parse(p ?? "0", System.Globalization.CultureInfo.InvariantCulture));

    public static readonly IValueConverter DimIf = new FuncValueConverter<bool, double>(b => b ? 0.45 : 1);

    public static readonly IValueConverter TimeLabel = new FuncValueConverter<bool, string>(toTime => toTime ? "Until" : "Minutes");

    /// <summary>Seconds → 1:23 or 1:02:03.</summary>
    public static readonly IValueConverter Clock = new FuncValueConverter<object?, string>(v =>
        Nexora.Core.Model.CountdownShow.Format((int)Math.Max(0, Math.Round(System.Convert.ToDouble(v ?? 0)))));
}
