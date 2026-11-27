using System.Globalization;
using System.Text;
using Nexora.Audio.Ipc;

namespace Nexora.Audio.Engine;

/// <summary>
/// A plain text report of how the desk is doing, to keep after a service or send to whoever looks after the sound PC:
/// the driver, clock, latency, load, dropouts, the X32 link, and the last events.
/// </summary>
public static class DiagReport
{
    public static string Text(EngineStatus e, DeskStatus d, IReadOnlyList<DeskEvent> events, DateTime nowUtc)
    {
        var c = CultureInfo.InvariantCulture;
        var b = new StringBuilder();
        void L(string label, string value) => b.Append(c, $"{label,-22}{value}").AppendLine();

        b.AppendLine("NEX-Audio diagnostics report");
        L("Made", nowUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", c));
        L("PC", Environment.MachineName);
        L("Engine version", e.EngineVersion);
        b.AppendLine();
        b.AppendLine("Sound");
        L("State", $"{e.State}: {e.Message}");
        L("Driver", e.Driver ?? "none");
        L("Inputs / outputs", $"{e.Inputs} / {e.Outputs}");
        L("Sample rate", e.SampleRate > 0 ? $"{e.SampleRate:0} Hz" : "—");
        L("Measured clock", ClockText(e));
        L("Buffer", e.BufferSize > 0 ? $"{e.BufferSize} samples ({e.BufferMs:0.00} ms)" : "—");
        L("Driver latency", e.SampleRate > 0 ? $"in {e.InputLatency}, out {e.OutputLatency} samples ({e.DriverRoundTripMs:0.0} ms round trip)" : "—");
        L("Mix latency", $"{d.MixLatencyMs:0.0} ms (plugins and delay alignment)");
        if (e.Latency is { MedianSamples: { } med } lat && lat.SampleRate > 0)
            L("Measured round trip", $"{med * 1000.0 / lat.SampleRate:0.00} ms (output {lat.Output} to input {lat.Input})");
        L("Load", $"{e.LoadAverage * 100:0}% average, {e.LoadPeak * 100:0}% peak");
        L("Running since", e.RunningSinceUtc == DateTime.MinValue ? "—" : e.RunningSinceUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", c));
        b.AppendLine();
        b.AppendLine("Problems since the sound started");
        L("Late buffers", e.LateBuffers.ToString(c));
        L("Driver problems", e.DriverProblems.ToString(c));
        L("Dropouts, last 10 min", e.RecentDropouts.ToString(c));
        L("Driver restarts", e.Restarts.ToString(c));
        L("Device lost", e.DeviceLosses == 0 ? "never" : $"{e.DeviceLosses} time(s), last {e.LastLossUtc?.ToLocalTime():HH:mm:ss}");
        L("Mix faults", e.MixFaults == 0 ? "none" : $"{e.MixFaults} buffer(s), last: {e.LastFault}");
        L("Bad samples silenced", e.BadSamples.ToString(c));
        b.AppendLine();
        b.AppendLine("X32 and failsafe");
        L("X32 link", d.X32Connected ? $"connected ({d.X32Name})" : d.Settings.X32Host is { Length: > 0 } h ? $"not answering ({h})" : "not set up");
        L("X32 link lost", d.X32Drops == 0 ? "never" : $"{d.X32Drops} time(s)");
        L("X32 last answered", d.X32LastHeardUtc?.ToLocalTime().ToString("HH:mm:ss", c) ?? "never");
        L("Failsafe", d.Settings.Armed ? d.Settings.HandedOver ? "HANDED OVER to the X32" : "armed" : "not armed");
        if (d.FailsafeNote is { } fn) L("Failsafe note", fn);
        L("Offline", d.Settings.Offline ? "yes" : "no");
        b.AppendLine();
        b.AppendLine("Events (newest first)");
        foreach (var ev in events)
            b.Append(c, $"{ev.Utc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {EventLog.Tag(ev.Level)}  {ev.Text}").AppendLine();
        if (events.Count == 0) b.AppendLine("(none)");
        return b.ToString();
    }

    public static string ClockText(EngineStatus e)
    {
        if (e.SampleRate <= 0) return "—";
        if (e.MeasuredRate <= 0) return "measuring (takes 10 seconds)";
        var ppm = (e.MeasuredRate - e.SampleRate) / e.SampleRate * 1e6;
        var note = Math.Abs(ppm) < 200 ? "steady" : Math.Abs(ppm) < 2000 ? "a little off: check the X32 clock source" : "wrong: the X32 and the driver are not at the same rate";
        return string.Create(CultureInfo.InvariantCulture, $"{e.MeasuredRate:0.0} Hz ({ppm:+0;-0;0} ppm, {note})");
    }

    /// <summary>Writes the report into audio/reports and returns the file.</summary>
    public static string Write(string audioDir, string text, DateTime nowUtc)
    {
        var dir = Path.Combine(audioDir, "reports");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"diagnostics-{nowUtc.ToLocalTime():yyyy-MM-dd-HHmmss}.txt");
        File.WriteAllText(file, text);
        return file;
    }
}
