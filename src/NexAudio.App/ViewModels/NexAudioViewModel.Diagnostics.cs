using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexora.Audio.Engine;
using Nexora.Audio.Ipc;
using Nexora.Core.Config;

namespace NexAudio.App.ViewModels;

/// <summary>One figure on the Diagnostics page: good (green), to watch (amber) or a problem (red).</summary>
public sealed record DiagRow(string Label, string Value, string Note, int State)
{
    public bool Good => State == 0;
    public bool Watch => State == 1;
    public bool Bad => State == 2;
    /// <summary>Not part of this setup (grey): shown so nothing looks connected when it is not.</summary>
    public bool Off => State == 3;
}

/// <summary>One line of the event log.</summary>
public sealed record EventRow(string Time, string Tag, string Text, int Level)
{
    public bool IsInfo => Level == 0;
    public bool IsWarning => Level == 1;
    public bool IsProblem => Level == 2;
}

public partial class NexAudioViewModel
{
    public ObservableCollection<DiagRow> DiagSound { get; } = [];
    public ObservableCollection<DiagRow> DiagProblems { get; } = [];
    public ObservableCollection<DiagRow> DiagLinks { get; } = [];
    public ObservableCollection<EventRow> EventRows { get; } = [];
    public ObservableCollection<DiagRow> DiagHardware { get; } = [];

    [ObservableProperty] private string _diagVerdict = "";
    [ObservableProperty] private string _diagAdvice = "";
    [ObservableProperty] private int _diagState;
    [ObservableProperty] private bool _onlyProblems;
    private string _eventsKey = "";

    public bool DiagGood => DiagState == 0;
    public bool DiagWatch => DiagState == 1;
    public bool DiagBad => DiagState == 2;
    partial void OnDiagStateChanged(int value)
    {
        OnPropertyChanged(nameof(DiagGood));
        OnPropertyChanged(nameof(DiagWatch));
        OnPropertyChanged(nameof(DiagBad));
    }

    partial void OnOnlyProblemsChanged(bool value)
    {
        _eventsKey = "";
        BuildEvents();
    }

    public bool OnDiagnostics => Tab == NexAudioTab.Diagnostics;

    /// <summary>Rebuilds the page from the engine's and the desk's latest status (only while it is showing).</summary>
    private void BuildDiagnostics()
    {
        if (!OnDiagnostics) return;
        OnPropertyChanged(nameof(OnTestDevice));
        OnPropertyChanged(nameof(UnplugText));
        var e = Status;
        var d = DeskStatus;
        var running = e.State == EngineState.Running;
        var ppm = e.MeasuredRate > 0 && e.SampleRate > 0 ? Math.Abs((e.MeasuredRate - e.SampleRate) / e.SampleRate * 1e6) : 0;

        Replace(DiagSound,
        [
            new("Driver", e.Driver ?? "none", running ? e.State.ToString() : e.Message, running ? 0 : e.State == EngineState.Problem ? 2 : 1),
            new("Channels", e.Inputs > 0 ? $"{e.Inputs} in · {e.Outputs} out" : "—", "The X32 USB card gives 32 each way.", e.Inputs is 0 or >= 32 ? 0 : 1),
            new("Sample rate", e.SampleRate > 0 ? $"{e.SampleRate / 1000:0.#} kHz" : "—", "The rate the driver was opened at.", 0),
            new("Clock", DiagReport.ClockText(e), "Measured against the PC's own clock, over 10 seconds.", ppm < 200 ? 0 : ppm < 2000 ? 1 : 2),
            new("Buffer", e.BufferSize > 0 ? $"{e.BufferSize} samples · {e.BufferMs:0.00} ms" : "—", "Smaller is quicker, bigger is safer.", e.BufferSize is 0 or <= 256 ? 0 : 1),
            new("Driver latency", e.SampleRate > 0 ? $"{e.DriverRoundTripMs:0.0} ms round trip" : "—", $"In {e.InputLatency}, out {e.OutputLatency} samples, as the driver reports it.", e.DriverRoundTripMs < 12 ? 0 : 1),
            new("Mix latency", $"{d.MixLatencyMs:0.0} ms", "Added by plugins and delay alignment.", d.MixLatencyMs < 5 ? 0 : 1),
            new("Load", running ? $"{e.LoadAverage * 100:0}% · peak {e.LoadPeak * 100:0}%" : "—", "Of each buffer's time.", e.LoadPeak < 0.7 ? 0 : e.LoadPeak < 0.85 ? 1 : 2),
        ]);

        Replace(DiagProblems,
        [
            new("Dropouts, last 10 min", e.RecentDropouts.ToString(), "Late buffers plus driver problems.", e.RecentDropouts == 0 ? 0 : e.RecentDropouts < 5 ? 1 : 2),
            new("Late buffers", e.LateBuffers.ToString(), "Since the sound started.", e.LateBuffers == 0 ? 0 : 1),
            new("Driver problems", e.DriverProblems.ToString(), "Lost sync or reported errors.", e.DriverProblems == 0 ? 0 : 1),
            new("Device lost", e.DeviceLosses == 0 ? "never" : $"{e.DeviceLosses}× · last {e.LastLossUtc?.ToLocalTime():HH:mm:ss}", "X32 off or the USB cable out. NEX-Audio opens it again by itself.", e.DeviceLosses == 0 ? 0 : 2),
            new("Driver restarts", e.Restarts.ToString(), "Times NEX-Audio opened the driver again.", e.Restarts == 0 ? 0 : 1),
            new("Mix faults", e.MixFaults == 0 ? "none" : $"{e.MixFaults} buffer(s)", e.LastFault ?? "The plain patch plays for that buffer, then the mix is loaded again.", e.MixFaults == 0 ? 0 : 2),
            new("Bad samples silenced", e.BadSamples.ToString(), "Samples that were not numbers, stopped before the speakers.", e.BadSamples == 0 ? 0 : 2),
        ]);

        var host = d.Settings.X32Host;
        Replace(DiagLinks,
        [
            new("X32 link", d.X32Connected ? $"linked · {d.X32Name}" : host is { Length: > 0 } ? "not answering" : "not set up",
                d.X32Connected ? "Names, shadow mix and failsafe scenes go over the network." : host is { Length: > 0 } ? $"Trying {host}." : "Set it up on the X32 link page.",
                d.X32Connected || d.Settings.Offline ? 0 : host is { Length: > 0 } ? 2 : 1),
            new("X32 link lost", d.X32Drops == 0 ? "never" : $"{d.X32Drops}×", d.X32LastHeardUtc is { } h ? $"Last answered {h.ToLocalTime():HH:mm:ss}." : "Not heard yet.", d.X32Drops == 0 ? 0 : 1),
            new("Failsafe", d.Settings.HandedOver ? "handed over" : d.Settings.Armed ? "armed" : "off",
                d.FailsafeNote ?? (d.Settings.Armed ? $"The X32 takes over after {d.Settings.TimeoutMs / 1000.0:0.0} s of silence." : "Arm it on the Failsafe tab before a service."),
                d.Settings.HandedOver ? 2 : d.Settings.Armed ? 0 : 1),
            new("Stream to Live Output", d.StreamPackets > 0 ? $"{d.StreamPackets:N0} packets sent" : "not sending", d.Settings.LiveOutputHost ?? "No Live Output PC set.", d.StreamPackets > 0 || d.Settings.Offline ? 0 : 1),
        ]);

        var usb = e.Driver is { } drv && drv.Contains("X-USB", StringComparison.OrdinalIgnoreCase);
        const string needs = "Not available: needs extra hardware this desk does not have.";
        Replace(DiagHardware,
        [
            new("X32 USB card", usb ? "in use" : e.Driver == SimulatedDevice.DeviceName ? "test device" : "not found", "32 in, 32 out, 44.1 or 48 kHz.", usb ? 0 : 1),
            new("X32 as a control surface", d.Settings.X32Surface ? "on" : "off", "Remote tab.", d.Settings.X32Surface ? 0 : 3),
            new("MADI", "unavailable", needs, 3),
            new("Dante", "unavailable", needs, 3),
            new("AES/EBU", "unavailable", needs, 3),
            new("Optocore / SoundGrid", "unavailable", needs, 3),
            new("Word clock", "unavailable", "The X32 is the clock master over USB.", 3),
            new("GPIO", "unavailable", needs, 3),
            e.SampleRates.Count == 0
                ? new("96 kHz", "—", "Shown once the sound runs.", 3)
                : e.SampleRates.Any(r => r >= 88200)
                    ? new("96 kHz", "possible", "This driver offers 88.2 or 96 kHz.", 0)
                    : new("96 kHz", "unavailable", "This driver runs at 44.1 or 48 kHz only (as the X32 USB card does).", 3),
        ]);

        var worst = DiagSound.Concat(DiagProblems).Concat(DiagLinks).Select(r => r.State).Where(st => st < 3).DefaultIfEmpty(0).Max();
        DiagState = worst;
        (DiagVerdict, DiagAdvice) = worst switch
        {
            0 => ("Everything is healthy", "No dropouts, the clock is steady and the links are up."),
            1 => ("Healthy, with things to watch", "Amber figures will not stop the service, but look at them before the next one."),
            _ => ("Something needs attention", "Red figures changed, or could change, what the room heard. The event log below says when and what."),
        };
        BuildEvents();
    }

    private void BuildEvents()
    {
        var list = DeskStatus.Events.Where(e => !OnlyProblems || e.Level != EventLevel.Info).Take(60).ToList();
        var key = string.Join("|", list.Select(e => e.Utc.Ticks));
        if (key == _eventsKey) return;
        _eventsKey = key;
        EventRows.Clear();
        foreach (var e in list)
            EventRows.Add(new EventRow(e.Utc.ToLocalTime().ToString("ddd HH:mm:ss"), e.Level switch
            {
                EventLevel.Problem => "PROBLEM",
                EventLevel.Warning => "WATCH",
                _ => "INFO",
            }, e.Text, (int)e.Level));
        OnPropertyChanged(nameof(HasEvents));
    }

    private static void Replace(ObservableCollection<DiagRow> into, IReadOnlyList<DiagRow> rows)
    {
        if (into.Count == rows.Count && into.SequenceEqual(rows)) return;
        into.Clear();
        foreach (var r in rows) into.Add(r);
    }

    public bool HasEvents => EventRows.Count > 0;

    /// <summary>The test device is in use, so its pretend cable can be pulled to try the recovery.</summary>
    public bool OnTestDevice => Status.Driver == SimulatedDevice.DeviceName;
    public string UnplugText => Status.PretendUnplugged ? "Plug the test device back in" : "Pull the test device's cable";

    [RelayCommand]
    private Task PretendUnplug() =>
        _engine.SendAsync(new DeskCommandMsg("test.unplug", new() { ["on"] = Status.PretendUnplugged ? "0" : "1" }));

    [RelayCommand]
    private Task SaveReport() => _engine.SendAsync(new DeskCommandMsg("diag.report"));

    [RelayCommand]
    private void OpenEventsFolder()
    {
        if (IsRemoteDesk)
        {
            Notice = $"The log folder is on {RemoteDeskName}. The newest events show here on this page.";
            return;
        }
        var dir = Path.Combine(AppPaths.Root, "audio", "events");
        try
        {
            Directory.CreateDirectory(dir);
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception) { }
    }
}
