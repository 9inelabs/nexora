using System.Text.Json;
using Nexora.Core.Protocol;

namespace Nexora.Core.Config;

public enum PcRole
{
    NotSet,
    LiveOutput,
    Terminal,
}

/// <summary>What this PC is, set by the first-run wizard. Stored in this-pc.json.</summary>
public sealed class ThisPcConfig
{
    public PcRole Role { get; set; } = PcRole.NotSet;
    public string PcId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string PcName { get; set; } = Environment.MachineName.ToUpperInvariant();
    public string PairingCode { get; set; } = "";
    public int Port { get; set; } = Wire.DefaultPort;

    /// <summary>Terminal only: the Live Output PC it paired with, matched on discovery.</summary>
    public string? ServerId { get; set; }

    /// <summary>Terminal only: a fixed address typed in as a backup (for example 192.168.1.20 or 192.168.1.20:7400).</summary>
    public string? ManualAddress { get; set; }

    public static ThisPcConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
                return JsonSerializer.Deserialize<ThisPcConfig>(File.ReadAllText(AppPaths.ConfigFile), Wire.Options) ?? new();
        }
        catch (Exception)
        {
            // A damaged file falls back to the first-run wizard rather than crashing.
        }
        return new ThisPcConfig();
    }

    public void Save()
    {
        var tmp = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions(Wire.Options) { WriteIndented = true }));
        File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
    }

    public static Uri? ParseAddress(string? text, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.Contains("://")) text = "ws://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return null;
        var port = u.IsDefaultPort ? defaultPort : u.Port;
        return new UriBuilder("ws", u.Host, port, Wire.Path).Uri;
    }
}
