using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexora.Audio.Ipc;

public enum EngineState
{
    /// <summary>No driver open: the X32's outputs fed from NEX-Audio are silent.</summary>
    Stopped,
    Starting,
    Running,
    /// <summary>NEX-Audio wants to run but the driver failed or stopped sending audio. It keeps trying.</summary>
    Problem,
}

/// <summary>The result of one round-trip latency test.</summary>
public sealed record LatencyResult(
    int Output,
    int Input,
    IReadOnlyList<long> Rounds,
    long? MedianSamples,
    double SampleRate,
    DateTime AtUtc)
{
    public double? Milliseconds => MedianSamples is { } s ? s * 1000.0 / SampleRate : null;
    public int Heard => Rounds.Count(r => r >= 0);
}

/// <summary>Everything the NEX-Audio screen shows about the engine.</summary>
public sealed record EngineStatus(
    EngineState State,
    string Message,
    string? Driver,
    IReadOnlyList<string> Drivers,
    double SampleRate,
    int BufferSize,
    IReadOnlyList<int> BufferSizes,
    int Inputs,
    int Outputs,
    IReadOnlyList<string> InputNames,
    IReadOnlyList<string> OutputNames,
    int InputLatency,
    int OutputLatency,
    string SampleType,
    double LoadAverage,
    double LoadPeak,
    long LateBuffers,
    long DriverProblems,
    int Restarts,
    bool HasSettingsPanel,
    IReadOnlyList<int> Patch,
    bool LatencyRunning,
    LatencyResult? Latency,
    string EngineVersion,
    DateTime RunningSinceUtc)
{
    public static EngineStatus Empty { get; } = new(EngineState.Stopped, "", null, [], 0, 0, [], 0, 0, [], [], 0, 0, "", 0, 0, 0, 0, 0, false, [], false, null, "", DateTime.MinValue);

    /// <summary>The driver's own figure for the round trip (in plus out), in milliseconds.</summary>
    public double DriverRoundTripMs => SampleRate > 0 ? (InputLatency + OutputLatency) * 1000.0 / SampleRate : 0;
    public double BufferMs => SampleRate > 0 ? BufferSize * 1000.0 / SampleRate : 0;
}

/// <summary>Messages between the NEX-Audio screen and the engine process, one JSON object per line.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(StatusMsg), "status")]
[JsonDerivedType(typeof(MetersMsg), "meters")]
[JsonDerivedType(typeof(NoticeMsg), "notice")]
[JsonDerivedType(typeof(OpenDriverMsg), "open")]
[JsonDerivedType(typeof(CloseDriverMsg), "close")]
[JsonDerivedType(typeof(SetPatchMsg), "patch")]
[JsonDerivedType(typeof(LatencyTestMsg), "latency")]
[JsonDerivedType(typeof(DriverPanelMsg), "panel")]
[JsonDerivedType(typeof(RefreshDriversMsg), "drivers")]
[JsonDerivedType(typeof(ResetClipsMsg), "clips")]
[JsonDerivedType(typeof(QuitMsg), "quit")]
public abstract record AudioMessage
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ToLine() => JsonSerializer.Serialize(this, Json);

    public static AudioMessage? FromLine(string line)
    {
        try { return JsonSerializer.Deserialize<AudioMessage>(line, Json); }
        catch (JsonException) { return null; }
    }
}

// Engine to screen
public sealed record StatusMsg(EngineStatus Status) : AudioMessage;

/// <summary>Peak levels since the last meters message, 0 to 1, about 30 times a second.</summary>
public sealed record MetersMsg(float[] In, float[] Out, bool[] InClip, bool[] OutClip) : AudioMessage;

public sealed record NoticeMsg(string Text, bool IsError) : AudioMessage;

// Screen to engine
public sealed record OpenDriverMsg(string Driver, int BufferSize) : AudioMessage;
public sealed record CloseDriverMsg : AudioMessage;
public sealed record SetPatchMsg(int[] Patch) : AudioMessage;
public sealed record LatencyTestMsg(int Output, int Input) : AudioMessage;
public sealed record DriverPanelMsg : AudioMessage;
public sealed record RefreshDriversMsg : AudioMessage;
public sealed record ResetClipsMsg : AudioMessage;
/// <summary>Stops the sound and closes the engine process (only when someone presses Shut down engine).</summary>
public sealed record QuitMsg : AudioMessage;
