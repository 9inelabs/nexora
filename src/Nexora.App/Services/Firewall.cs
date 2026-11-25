using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Nexora.App.Services;

public static class Firewall
{
    /// <summary>
    /// Adds a Windows Firewall rule so other church PCs can reach NEXORA.
    /// Windows asks for permission (UAC) once.
    /// </summary>
    public static (bool Ok, string Message) AllowNexora()
    {
        if (!OperatingSystem.IsWindows()) return (false, "Only needed on Windows.");
        var exe = Environment.ProcessPath;
        if (exe is null) return (false, "Cannot find NEXORA.exe.");
        var args = "/c netsh advfirewall firewall delete rule name=\"NEXORA\" >nul 2>&1 & " +
                   $"netsh advfirewall firewall add rule name=\"NEXORA\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
        try
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(15000);
            return p is { ExitCode: 0 } ? (true, "NEXORA is allowed through Windows Firewall.") : (false, "Windows Firewall did not accept the change.");
        }
        catch (Exception ex)
        {
            return (false, "Permission was not given. " + ex.Message);
        }
    }

    public static IReadOnlyList<string> LanAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address.ToString());
            }
        }
        catch { }
        return list;
    }
}
