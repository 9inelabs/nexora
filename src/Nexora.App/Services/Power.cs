using System.Runtime.InteropServices;

namespace Nexora.App.Services;

/// <summary>Keeps Windows from sleeping or turning the screens off while NEXORA is showing a service.</summary>
public static class Power
{
    private const uint Continuous = 0x80000000, SystemRequired = 0x00000001, DisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    public static void KeepAwake(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { SetThreadExecutionState(on ? Continuous | SystemRequired | DisplayRequired : Continuous); }
        catch (Exception ex) { AppLog.Write("Sleep prevention: " + ex.Message); }
    }
}
