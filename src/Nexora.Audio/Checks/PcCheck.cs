using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nexora.Audio.Asio;

namespace Nexora.Audio.Checks;

public enum CheckLevel
{
    Pass,
    Warn,
    Fail,
}

/// <summary>One line of the PC check: what was checked, what was found, and what to do about it.</summary>
public sealed record CheckItem(string Title, CheckLevel Level, string Found, string Advice);

/// <summary>
/// Checks that this church PC is fit to run the X32's sound: processor, memory, Windows, power,
/// USB power saving, the X32 driver and the network. It only reads; it changes nothing.
/// </summary>
public static class PcCheck
{
    private const string UsbSettingsGroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    public static IReadOnlyList<CheckItem> Run(bool isLiveOutputPc, string dataRoot)
    {
        var list = new List<CheckItem>
        {
            Processor(),
            Memory(),
            WindowsVersion(),
        };
        if (OperatingSystem.IsWindows())
        {
            list.Add(PowerPlan());
            list.Add(UsbPowerSaving());
            list.Add(Battery());
        }
        list.Add(Driver());
        list.Add(Network());
        list.Add(Disk(dataRoot));
        list.Add(isLiveOutputPc
            ? new CheckItem("Other jobs on this PC", CheckLevel.Warn, "This PC is also the Live Output PC.",
                "Run NEX-Audio on a different PC, so a heavy video job can never cause a click in the sound.")
            : new CheckItem("Other jobs on this PC", CheckLevel.Pass, "This PC is not the Live Output PC.",
                "During services, use this PC for NEX-Audio only."));
        return list;
    }

    public static CheckItem Processor()
    {
        var cores = Environment.ProcessorCount;
        var name = OperatingSystem.IsWindows() ? CpuName() : null;
        var found = $"{name ?? "Processor"} with {cores} threads";
        return cores switch
        {
            >= 8 => new CheckItem("Processor", CheckLevel.Pass, found, "Enough for 32 channels with effects."),
            >= 4 => new CheckItem("Processor", CheckLevel.Warn, found,
                "Fine for Phase 1. Watch the engine load once effects and plugins arrive; 8 or more threads is better."),
            _ => new CheckItem("Processor", CheckLevel.Fail, found, "Too few threads for live mixing. Use a newer PC."),
        };
    }

    public static CheckItem Memory()
    {
        var gb = TotalMemoryGb();
        if (gb <= 0) return new CheckItem("Memory", CheckLevel.Warn, "Could not read the memory size.", "Check the PC has at least 16 GB.");
        var found = $"{gb:0} GB";
        return gb switch
        {
            >= 15.5 => new CheckItem("Memory", CheckLevel.Pass, found, "Enough. 32 GB is ideal once many plugins are loaded."),
            >= 7.5 => new CheckItem("Memory", CheckLevel.Warn, found, "Enough to start. Plan an upgrade to 16 GB or more before adding plugins."),
            _ => new CheckItem("Memory", CheckLevel.Fail, found, "Too little for live mixing. Add memory (16 GB or more)."),
        };
    }

    public static CheckItem WindowsVersion()
    {
        if (!OperatingSystem.IsWindows())
            return new CheckItem("Windows", CheckLevel.Warn, RuntimeInformation.OSDescription, "NEX-Audio runs the X32 on Windows only.");
        var build = Environment.OSVersion.Version.Build;
        return build switch
        {
            >= 22000 => new CheckItem("Windows", CheckLevel.Pass, $"Windows 11 (build {build})", "Pause Windows updates on Sundays so the PC never restarts mid-service."),
            >= 19041 => new CheckItem("Windows", CheckLevel.Warn, $"Windows 10 (build {build})", "Works. Windows 10 support has ended, so plan to move to Windows 11."),
            _ => new CheckItem("Windows", CheckLevel.Fail, $"Windows build {build}", "Too old. Update Windows before using this PC for sound."),
        };
    }

    [SupportedOSPlatform("windows")]
    public static CheckItem PowerPlan()
    {
        var output = Run("powercfg", "/getactivescheme");
        if (output is null) return new CheckItem("Power plan", CheckLevel.Warn, "Could not read the power plan.", "Set Control Panel > Power Options to High performance.");
        var name = output.Contains('(') ? output[(output.IndexOf('(') + 1)..].TrimEnd(')', ' ', '\r', '\n') : output.Trim();
        var fast = output.Contains("High performance", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("e9a42b02-d5df-448d-aa00-03f14749eb61", StringComparison.OrdinalIgnoreCase);
        return fast
            ? new CheckItem("Power plan", CheckLevel.Pass, name, "The processor stays at full speed.")
            : new CheckItem("Power plan", CheckLevel.Warn, name,
                "Set Control Panel > Power Options to High performance. Power saving slows the processor down and can cause clicks.");
    }

    [SupportedOSPlatform("windows")]
    public static CheckItem UsbPowerSaving()
    {
        var output = Run("powercfg", $"/query SCHEME_CURRENT {UsbSettingsGroup} {UsbSelectiveSuspend}");
        if (output is null || !output.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase))
            return new CheckItem("USB power saving", CheckLevel.Warn, "Could not read the USB setting.",
                "In Power Options > Change advanced settings > USB settings, turn USB selective suspend off.");
        var on = output.Split('\n').FirstOrDefault(l => l.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase))?.Trim().EndsWith('1') == true;
        return on
            ? new CheckItem("USB power saving", CheckLevel.Warn, "USB selective suspend is on.",
                "Turn it off (Power Options > Change advanced settings > USB settings), so Windows never pauses the X32's USB link.")
            : new CheckItem("USB power saving", CheckLevel.Pass, "USB selective suspend is off.", "Windows will not pause the X32's USB link.");
    }

    [SupportedOSPlatform("windows")]
    public static CheckItem Battery()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 || s.BatteryFlag == 255)
            return new CheckItem("Mains power", CheckLevel.Pass, "Desktop PC (no battery).", "Plug it into the UPS (battery backup).");
        return s.ACLineStatus == 1
            ? new CheckItem("Mains power", CheckLevel.Pass, "Laptop, plugged in.", "Keep it plugged in during services.")
            : new CheckItem("Mains power", CheckLevel.Warn, "Laptop, running on battery.", "Plug it in. On battery Windows slows the processor down.");
    }

    public static CheckItem Driver()
    {
        if (!OperatingSystem.IsWindows())
            return new CheckItem("X32 USB driver", CheckLevel.Warn, "Not on Windows.", "Install the X-USB ASIO driver on the Windows PC wired to the X32.");
        var drivers = AsioDriverList.Find();
        if (drivers.FirstOrDefault(d => d.LooksLikeX32) is { } x)
            return new CheckItem("X32 USB driver", CheckLevel.Pass, x.Name, "Installed. Plug the X32's USB cable into a port on the back of the PC, not a hub.");
        if (drivers.Count > 0)
            return new CheckItem("X32 USB driver", CheckLevel.Warn, "Other ASIO drivers: " + string.Join(", ", drivers.Select(d => d.Name)),
                "The X32's own driver was not found. Download the X-USB ASIO driver from Behringer's X32 page and install it.");
        return new CheckItem("X32 USB driver", CheckLevel.Fail, "No ASIO driver installed.",
            "Download the X-USB ASIO driver from Behringer's X32 page, install it, and restart the PC.");
    }

    public static CheckItem Network()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();
            var wired = up.Any(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT);
            var wifi = up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            if (wired) return new CheckItem("Network", CheckLevel.Pass, "Wired network connected.", "The X32 link and the failsafe use the wired church network.");
            if (wifi) return new CheckItem("Network", CheckLevel.Warn, "Wi-Fi only.", "Use a network cable. Wi-Fi can drop out, and the failsafe relies on the network.");
            return new CheckItem("Network", CheckLevel.Fail, "No network connected.", "Plug this PC into the church network switch, the same one as the X32.");
        }
        catch (Exception)
        {
            return new CheckItem("Network", CheckLevel.Warn, "Could not read the network.", "Use a wired connection to the church network.");
        }
    }

    public static CheckItem Disk(string dataRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dataRoot)) ?? "/";
            var free = new DriveInfo(root).AvailableFreeSpace / 1024d / 1024 / 1024;
            return free >= 20
                ? new CheckItem("Free disk space", CheckLevel.Pass, $"{free:0} GB free", "Plenty for scenes and logs.")
                : new CheckItem("Free disk space", CheckLevel.Warn, $"{free:0} GB free", "Free up space; Windows slows down when the disk is nearly full.");
        }
        catch (Exception)
        {
            return new CheckItem("Free disk space", CheckLevel.Warn, "Could not read the disk.", "Keep at least 20 GB free.");
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static string? Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? CpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double TotalMemoryGb()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                return GlobalMemoryStatusEx(ref m) ? m.TotalPhys / 1024d / 1024 / 1024 : 0;
            }
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
