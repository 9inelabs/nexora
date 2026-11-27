using System.Text.Json;
using Nexora.Core.Config;

namespace NexAudio.App.Services;

/// <summary>One of your own fader banks: a name and the strips on it, in order.</summary>
public sealed record UserBank(string Name, List<string> Strips);

/// <summary>What a utility fader moves: a strip's fader, or its send to one bus.</summary>
public sealed record UtilityTarget(string StripId, string? BusId = null);

/// <summary>
/// How one engineer likes the desk laid out on this PC: their fader banks, utility faders, text size, contrast
/// and meters. Kept per user, so two people on the same desk each get their own.
/// </summary>
public sealed record UserPrefs
{
    public const int MaxBanks = 8;
    public const int MaxBankStrips = 24;

    public List<UserBank> Banks { get; init; } = [];
    public UtilityTarget?[] Utility { get; init; } = [null, null];
    /// <summary>1, 1.15 or 1.3.</summary>
    public double TextScale { get; init; } = 1;
    public bool HighContrast { get; init; }
    /// <summary>How long a peak line stays: "off", "1", "3" (seconds) or "hold" (until cleared).</summary>
    public string PeakHold { get; init; } = "1";
    /// <summary>"fast", "medium" or "slow".</summary>
    public string Fall { get; init; } = "medium";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FileFor(string userId)
    {
        var safe = new string(userId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safe.Length == 0) safe = "anyone";
        return Path.Combine(AppPaths.LocalDir, "users", safe + ".json");
    }

    public static UserPrefs Load(string userId)
    {
        try
        {
            var f = FileFor(userId);
            if (File.Exists(f)) return (JsonSerializer.Deserialize<UserPrefs>(File.ReadAllText(f), Json) ?? new UserPrefs()).Clean();
        }
        catch (Exception) { }
        return new UserPrefs();
    }

    public void Save(string userId)
    {
        try
        {
            var f = FileFor(userId);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(Clean(), Json));
            File.Move(f + ".tmp", f, true);
        }
        catch (Exception ex) { AppLog.Write("NEX-Audio layout: " + ex.Message); }
    }

    /// <summary>Within the limits, whatever was in the file.</summary>
    public UserPrefs Clean()
    {
        var u = Utility is { Length: 2 } ? Utility : [Utility?.ElementAtOrDefault(0), Utility?.ElementAtOrDefault(1)];
        return this with
        {
            Banks = (Banks ?? []).Where(b => b is not null).Take(MaxBanks)
                .Select(b => new UserBank(string.IsNullOrWhiteSpace(b.Name) ? "My bank" : b.Name.Trim()[..Math.Min(b.Name.Trim().Length, 20)],
                    (b.Strips ?? []).Distinct().Take(MaxBankStrips).ToList())).ToList(),
            Utility = u,
            TextScale = TextScale >= 1.25 ? 1.3 : TextScale >= 1.1 ? 1.15 : 1,
            PeakHold = PeakHold is "off" or "1" or "3" or "hold" ? PeakHold : "1",
            Fall = Fall is "fast" or "medium" or "slow" ? Fall : "medium",
        };
    }
}
