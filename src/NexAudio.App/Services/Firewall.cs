using System.Diagnostics;

namespace NexAudio.App.Services;

public static class Firewall
{
    /// <summary>
    /// Adds a Windows Firewall rule so tablets and phones on the church network can open the remote.
    /// Windows asks for permission once.
    /// </summary>
    public static (bool Ok, string Message) AllowRemote(int port)
    {
        if (!OperatingSystem.IsWindows()) return (false, "Only needed on Windows.");
        var args = "/c netsh advfirewall firewall delete rule name=\"NEX-Audio remote\" >nul 2>&1 & " +
                   $"netsh advfirewall firewall add rule name=\"NEX-Audio remote\" dir=in action=allow protocol=TCP localport={port} enable=yes profile=any";
        try
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(15000);
            return p is { ExitCode: 0 } ? (true, "Tablets and phones can reach the remote through Windows Firewall.") : (false, "Windows Firewall did not accept the change.");
        }
        catch (Exception ex)
        {
            return (false, "Permission was not given. " + ex.Message);
        }
    }
}
