using System.Net.Http;
using Nexora.App.Services;
using Nexora.Core.Config;

namespace Nexora.App.Speech;

/// <summary>A Whisper model NEXORA can listen with.</summary>
/// <param name="File">The file name, the same everywhere (ggml-small.en.bin).</param>
/// <param name="MinBytes">A smaller file is a broken download.</param>
public sealed record SpeechModel(string Id, string File, string Name, string Size, string Best, long MinBytes)
{
    public string Path => System.IO.Path.Combine(AppPaths.SpeechDir, File);
    public bool OnThisPc => System.IO.File.Exists(Path) && new FileInfo(Path).Length >= MinBytes;
    public string Url => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + File;
}

/// <summary>
/// The Whisper models (free, MIT licence), where they live on this PC, and the three ways to get one:
/// download once from the internet, copy from the Live Output PC over the church network, or import a file.
/// </summary>
public static class SpeechModels
{
    public static readonly IReadOnlyList<SpeechModel> All =
    [
        new("base.en", "ggml-base.en.bin", "Fast", "148 MB", "Older PCs (4 cores). Quickest, a little less accurate.", 100_000_000),
        new("small.en", "ggml-small.en.bin", "Recommended", "488 MB", "PCs without a graphics card (6 cores or more).", 400_000_000),
        new("medium.en-q5_0", "ggml-medium.en-q5_0.bin", "More accurate", "539 MB", "Strong PCs (8 cores or more), or a modest graphics card.", 450_000_000),
        new("large-v3-turbo-q5_0", "ggml-large-v3-turbo-q5_0.bin", "Best", "574 MB", "PCs with a graphics card (NVIDIA, AMD or Intel).", 480_000_000),
    ];

    public static SpeechModel? Get(string? id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>The model that suits this PC: the best with a graphics card, otherwise by how many cores the CPU has.</summary>
    public static SpeechModel Suggest(bool graphicsCard)
    {
        if (graphicsCard) return All[3];
        var cores = Environment.ProcessorCount;
        return cores >= 12 ? All[2] : cores >= 6 ? All[1] : All[0];
    }

    /// <summary>The models on this PC, best first.</summary>
    public static IReadOnlyList<SpeechModel> Installed => All.Where(m => m.OnThisPc).Reverse().ToList();

    /// <summary>A Whisper model file starts with "lmgg" (the ggml magic number).</summary>
    public static bool LooksLikeModel(string path)
    {
        try
        {
            using var f = System.IO.File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return f.Read(head) == 4 && head.SequenceEqual("lmgg"u8);
        }
        catch (Exception) { return false; }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromHours(2) };

    /// <summary>Downloads a model from the internet (once). Reports progress 0 to 1.</summary>
    public static async Task DownloadAsync(SpeechModel m, IProgress<double> progress, CancellationToken ct)
    {
        var tmp = m.Path + ".part";
        using var res = await Http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"The download did not start ({(int)res.StatusCode}). Check the internet on this PC, or import the file instead.");
        var total = res.Content.Headers.ContentLength ?? 0;
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = System.IO.File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long got = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
                if (total > 0) progress.Report((double)got / total);
            }
        }
        Finish(m, tmp);
    }

    /// <summary>Copies a model from the Live Output PC (which keeps every model one PC has got).</summary>
    public static async Task<bool> FromLiveOutputAsync(SpeechModel m, CancellationToken ct)
    {
        if (AppServices.Instance.Client is not { } c) return false;
        var tmp = m.Path + ".lan";
        if (!await c.DownloadPathAsync("speech/" + m.File, tmp, ct)) return false;
        Finish(m, tmp);
        return true;
    }

    /// <summary>The models the Live Output PC has (empty when it has none or cannot be reached).</summary>
    public static async Task<IReadOnlyList<SpeechModel>> OnLiveOutputAsync(CancellationToken ct = default)
    {
        if (AppServices.Instance.Client is not { } c) return [];
        try
        {
            var list = await c.GetJsonAsync<List<SharedFile>>("speech/index.json", ct) ?? [];
            return All.Where(m => list.Any(f => f.Name == m.File && f.Size >= m.MinBytes)).ToList();
        }
        catch (Exception) { return []; }
    }

    private sealed record SharedFile(string Name, long Size);

    /// <summary>Uses a model file the operator picked (from a USB stick, or downloaded on another computer).</summary>
    public static SpeechModel Import(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var m = All.FirstOrDefault(x => x.File.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("That is not one of the speech models NEXORA knows. The file name must stay as downloaded, for example ggml-small.en.bin.");
        if (!LooksLikeModel(path)) throw new InvalidOperationException("That file is not a Whisper model, or it is damaged.");
        if (new FileInfo(path).Length < m.MinBytes) throw new InvalidOperationException("That file is too small: the download did not finish. Download it again.");
        var tmp = m.Path + ".import";
        System.IO.File.Copy(path, tmp, overwrite: true);
        Finish(m, tmp);
        return m;
    }

    private static void Finish(SpeechModel m, string tmp)
    {
        if (new FileInfo(tmp).Length < m.MinBytes || !LooksLikeModel(tmp))
        {
            TryDelete(tmp);
            throw new InvalidOperationException("The model file arrived damaged. Try again.");
        }
        System.IO.File.Move(tmp, m.Path, overwrite: true);
        AppLog.Write($"Speech model ready: {m.File}");
    }

    public static void Remove(SpeechModel m) => TryDelete(m.Path);

    private static void TryDelete(string p)
    {
        try { if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch (Exception) { }
    }
}
