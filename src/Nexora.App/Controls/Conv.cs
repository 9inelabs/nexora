using Avalonia.Data.Converters;

namespace Nexora.App.Controls;

/// <summary>Small converters used in the views.</summary>
public static class Conv
{
    /// <summary>True → 0.45 (faded), false → 1.</summary>
    public static readonly IValueConverter DimIf = new FuncValueConverter<bool, double>(b => b ? 0.45 : 1);

    public static readonly IValueConverter TimeLabel = new FuncValueConverter<bool, string>(toTime => toTime ? "Until" : "Minutes");

    /// <summary>Seconds → 1:23 or 1:02:03.</summary>
    public static readonly IValueConverter Clock = new FuncValueConverter<object?, string>(v =>
        Nexora.Core.Model.CountdownShow.Format((int)Math.Max(0, Math.Round(System.Convert.ToDouble(v ?? 0)))));
}
