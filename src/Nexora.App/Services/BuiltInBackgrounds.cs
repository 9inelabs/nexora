using System.Text.Json;
using Avalonia.Media.Imaging;
using Nexora.Core.Config;

namespace Nexora.App.Services;

/// <summary>One picture or video loop that comes with NEXORA.</summary>
public sealed class BuiltInBackground
{
    public required string Id { get; init; }
    public required bool IsVideo { get; init; }
    public required string Title { get; init; }
    /// <summary>The file name a theme stores (nexora-p01.jpg, nexora-v01.mp4).</summary>
    public required string File { get; init; }
    public required string ThumbFile { get; init; }
    public string Page { get; init; } = "";

    private Bitmap? _thumb;
    private bool _tried;

    /// <summary>The small picture for the chooser, loaded when first shown.</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (_tried) return _thumb;
            _tried = true;
            try
            {
                var path = Path.Combine(AppPaths.BuiltInBackgroundsDir, ThumbFile);
                if (System.IO.File.Exists(path)) _thumb = new Bitmap(path);
            }
            catch (Exception ex) { AppLog.Write("Background thumbnail: " + ex.Message); }
            return _thumb;
        }
    }

    public string Tooltip => (IsVideo ? "Video loop: " : "Picture: ") + Title;
}

/// <summary>The pictures and video loops installed with NEXORA (Backgrounds/backgrounds.json in the install folder).</summary>
public static class BuiltInBackgrounds
{
    private static List<BuiltInBackground>? _all;

    public static IReadOnlyList<BuiltInBackground> All => _all ??= Load();
    public static IReadOnlyList<BuiltInBackground> Pictures => All.Where(b => !b.IsVideo).ToList();
    public static IReadOnlyList<BuiltInBackground> Loops => All.Where(b => b.IsVideo).ToList();

    public static BuiltInBackground? Find(string? file) => file is null ? null : All.FirstOrDefault(b => b.File == file);

    private static List<BuiltInBackground> Load()
    {
        var list = new List<BuiltInBackground>();
        try
        {
            var path = Path.Combine(AppPaths.BuiltInBackgroundsDir, "backgrounds.json");
            if (!System.IO.File.Exists(path)) return list;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var file = e.GetProperty("file").GetString();
                if (string.IsNullOrEmpty(file) || !System.IO.File.Exists(Path.Combine(AppPaths.BuiltInBackgroundsDir, file))) continue;
                list.Add(new BuiltInBackground
                {
                    Id = e.GetProperty("id").GetString() ?? file,
                    IsVideo = e.GetProperty("kind").GetString() == "video",
                    Title = e.GetProperty("title").GetString() ?? "",
                    File = file,
                    ThumbFile = e.TryGetProperty("thumb", out var t) ? t.GetString() ?? "" : "",
                    Page = e.TryGetProperty("page", out var p) ? p.GetString() ?? "" : "",
                });
            }
        }
        catch (Exception ex) { AppLog.Write("Built-in backgrounds: " + ex.Message); }
        return list;
    }
}
