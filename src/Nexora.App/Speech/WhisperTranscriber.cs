using System.Text;
using Nexora.App.Services;
using Nexora.Core.Speech;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Nexora.App.Speech;

/// <summary>
/// Whisper (whisper.cpp) on this PC: free, offline, and good with accents. Uses the graphics card through Vulkan
/// when there is one (NVIDIA, AMD or Intel), otherwise the CPU.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    private static readonly object LoadGate = new();
    private static bool _ordered;
    private readonly WhisperFactory _factory;
    private readonly int _threads;

    private WhisperTranscriber(WhisperFactory factory, SpeechModel model, bool gpu)
    {
        _factory = factory;
        Model = model;
        UsesGraphicsCard = gpu;
        // Leave two cores for NEXORA and Windows.
        _threads = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
        Name = $"Whisper {model.Id}" + (gpu ? " · graphics card" : $" · CPU, {_threads} cores");
    }

    public string Name { get; }
    public SpeechModel Model { get; }
    public bool UsesGraphicsCard { get; }

    /// <summary>Loads a model. Tries the graphics card first, then the CPU.</summary>
    public static WhisperTranscriber Open(SpeechModel model, bool allowGraphicsCard = true)
    {
        lock (LoadGate)
        {
            if (!_ordered)
            {
                _ordered = true;
                RuntimeOptions.RuntimeLibraryOrder = allowGraphicsCard
                    ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
                    : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
            }
            var factory = WhisperFactory.FromPath(model.Path);
            var loaded = RuntimeOptions.LoadedLibrary;
            var gpu = loaded is RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda;
            AppLog.Write($"Whisper: {model.File} on {loaded} ({WhisperFactory.GetRuntimeInfo()?.Split('\n').FirstOrDefault()?.Trim()})");
            return new WhisperTranscriber(factory, model, gpu);
        }
    }

    public async Task<string> TranscribeAsync(float[] audio, string prompt, CancellationToken ct)
    {
        // A short piece is padded to one second so Whisper does not stumble on it.
        if (audio.Length < 16000)
        {
            var padded = new float[16000];
            audio.CopyTo(padded, 0);
            audio = padded;
        }
        await using var p = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(_threads)
            .WithNoContext()
            .WithPrompt(prompt)
            .WithTemperature(0)
            .WithNoSpeechThreshold(0.6f)
            .Build();
        var sb = new StringBuilder();
        await foreach (var seg in p.ProcessAsync(audio, ct))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(seg.Text.Trim());
        }
        return sb.ToString();
    }

    /// <summary>
    /// How fast this PC is with this model: the seconds it takes to write down 5 seconds of speech-like sound.
    /// Under 1.5 s is comfortable; over 4 s means a smaller model would keep up better.
    /// </summary>
    public async Task<double> SpeedTestAsync(CancellationToken ct)
    {
        var audio = new float[5 * 16000];
        var rnd = new Random(7);
        for (var i = 0; i < audio.Length; i++)
        {
            // Pulses of tone and noise in a voice's range, so the whole model runs as it would for speech.
            var env = 0.5 + 0.5 * Math.Sin(i * 2 * Math.PI * 3 / 16000);
            audio[i] = (float)(env * (0.15 * Math.Sin(i * 2 * Math.PI * 180 / 16000) + 0.05 * Math.Sin(i * 2 * Math.PI * 720 / 16000) + 0.02 * (rnd.NextDouble() - 0.5)));
        }
        await TranscribeAsync(audio, "", ct); // first run warms up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await TranscribeAsync(audio, "", ct);
        return sw.Elapsed.TotalSeconds;
    }

    public void Dispose() => _factory.Dispose();
}
