using System.Reflection;
using Nexora.Core.Config;

namespace NexAudio.App.Services;

/// <summary>Everything NEX-Audio runs on this PC: the sound engine host and the sign-in link to the Live Output PC.</summary>
public sealed class DeskServices
{
    public static DeskServices Instance { get; } = new();

    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0")
        .Split('+')[0];

    /// <summary>The sound engine, its own process, kept running and restarted if it ever stops.</summary>
    public AudioEngineHost Audio { get; } = new();

    public Session Live { get; } = new();

    /// <summary>True when NEXORA on this same PC is the Live Output PC (the PC check warns about sharing it).</summary>
    public bool IsLiveOutputPc => DeskPaths.NexoraConfig()?.Role == PcRole.LiveOutput;

    public void Start()
    {
        // The sound was on when NEX-Audio last closed (or the PC restarted): bring it straight back, before anyone signs in.
        if (AudioEngineHost.SoundWasOn) Audio.Start();
        Live.Start(Version);
    }
}
