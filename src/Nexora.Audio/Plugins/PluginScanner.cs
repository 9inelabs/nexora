using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using static Nexora.Audio.Plugins.Vst3;

namespace Nexora.Audio.Plugins;

/// <summary>One plugin found on this PC.</summary>
public sealed record PluginInfo(string Path, string ClassId, string Name, string Vendor, string Category, string SubCategories, string Version)
{
    public bool IsInstrument => SubCategories.Contains("Instrument", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The result of looking through the plugin folders.</summary>
public sealed class PluginList
{
    public List<PluginInfo> Plugins { get; set; } = new();
    /// <summary>Plugin files that could not be opened, with the reason.</summary>
    public Dictionary<string, string> Failed { get; set; } = new();
    /// <summary>When each file was last looked at (its last-changed time), so unchanged files are not opened again.</summary>
    public Dictionary<string, long> Seen { get; set; } = new();
    public DateTime ScannedAt { get; set; }
}

/// <summary>
/// Finds VST3 plugins in the standard Windows folder and reads their names.
/// Each file is opened in its own short-lived process, so a broken plugin cannot take NEXORA down.
/// </summary>
public static class PluginScanner
{
    public static readonly string[] BuiltIn = { "builtin:gain", "builtin:latency", "builtin:crash" };

    /// <summary>The folders searched for .vst3 plugins.</summary>
    public static IEnumerable<string> Folders()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        if (common.Length > 0) yield return System.IO.Path.Combine(common, "VST3");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0) yield return System.IO.Path.Combine(local, "Programs", "Common", "VST3");
    }

    /// <summary>Finds every .vst3 bundle or file under the given folders (not inside other bundles).</summary>
    public static List<string> FindFiles(IEnumerable<string> folders)
    {
        var found = new List<string>();
        foreach (var f in folders)
            if (Directory.Exists(f)) Walk(f, found, 0);
        return found;
    }

    private static void Walk(string dir, List<string> found, int depth)
    {
        if (depth > 6) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return; }
        foreach (var e in entries)
        {
            if (e.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) { found.Add(e); continue; }
            if (Directory.Exists(e)) Walk(e, found, depth + 1);
        }
    }

    /// <summary>The actual program file inside a plugin bundle (X.vst3/Contents/x86_64-win/X.vst3), or the file itself.</summary>
    public static string? BinaryOf(string path)
    {
        if (File.Exists(path)) return path;
        if (!Directory.Exists(path)) return null;
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64-win" : "x86_64-win";
        foreach (var a in new[] { arch, "x86_64-win" })
        {
            var p = System.IO.Path.Combine(path, "Contents", a, name);
            if (File.Exists(p)) return p;
        }
        var any = System.IO.Path.Combine(path, "Contents", arch);
        if (Directory.Exists(any))
        {
            var first = Directory.EnumerateFiles(any, "*.vst3").FirstOrDefault();
            if (first != null) return first;
        }
        return null;
    }

    private static long Stamp(string path)
    {
        try { return (File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path)).Ticks; }
        catch { return 0; }
    }

    /// <summary>
    /// Looks through the plugin folders, reusing what is known for unchanged files.
    /// <paramref name="exe"/> is this program, started with "--plugin-scan &lt;file&gt;" for each new file.
    /// </summary>
    public static PluginList Scan(PluginList? known, string exe, IEnumerable<string>? folders = null, TimeSpan? perFile = null, Action<string>? progress = null)
    {
        var list = new PluginList { ScannedAt = DateTime.UtcNow };
        var files = FindFiles(folders ?? Folders());
        foreach (var file in files)
        {
            var stamp = Stamp(file);
            if (known != null && known.Seen.TryGetValue(file, out var s) && s == stamp)
            {
                list.Plugins.AddRange(known.Plugins.Where(p => p.Path == file));
                if (known.Failed.TryGetValue(file, out var why)) list.Failed[file] = why;
                list.Seen[file] = stamp;
                continue;
            }
            progress?.Invoke(System.IO.Path.GetFileNameWithoutExtension(file));
            list.Seen[file] = stamp;
            try
            {
                var found = ScanOutOfProcess(exe, file, perFile ?? TimeSpan.FromSeconds(20));
                if (found.Count == 0) list.Failed[file] = "No sound effects were found in this file.";
                list.Plugins.AddRange(found);
            }
            catch (Exception ex) { list.Failed[file] = ex.Message; }
        }
        list.Plugins.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static List<PluginInfo> ScanOutOfProcess(string exe, string file, TimeSpan limit)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--plugin-scan");
        psi.ArgumentList.Add(file);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the plugin checker.");
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit((int)limit.TotalMilliseconds))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException("The plugin took too long to open.");
        }
        var text = output.Result.Trim();
        if (p.ExitCode != 0) throw new InvalidOperationException(text.Length > 0 && !text.StartsWith('[') ? text : "The plugin could not be opened.");
        return JsonSerializer.Deserialize<List<PluginInfo>>(text) ?? new();
    }

    /// <summary>Runs inside the "--plugin-scan" process: opens one file and prints what it holds.</summary>
    public static int RunScanProcess(string file)
    {
        try
        {
            var found = ReadFile(file);
            Console.Out.Write(JsonSerializer.Serialize(found));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Out.Write(ex.Message);
            return 1;
        }
    }

    /// <summary>Opens a plugin file in this process and lists its audio effects.</summary>
    public static unsafe List<PluginInfo> ReadFile(string file)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("VST3 plugins run on Windows.");
        var dll = BinaryOf(file) ?? throw new FileNotFoundException("The plugin file was not found.");
        var lib = NativeLibrary.Load(dll);
        if (NativeLibrary.TryGetExport(lib, "InitDll", out var init)) ((delegate* unmanaged<byte>)init)();
        var factory = ((delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(lib, "GetPluginFactory"))();
        if (factory == IntPtr.Zero) throw new InvalidOperationException("The plugin has no factory.");

        var vendor = "";
        var fsize = Marshal.SizeOf<PFactoryInfo>();
        var fbuf = Marshal.AllocHGlobal(fsize);
        try
        {
            if (((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(factory)[3])(factory, fbuf) == kResultOk)
                vendor = Marshal.PtrToStructure<PFactoryInfo>(fbuf).Vendor ?? "";
        }
        finally { Marshal.FreeHGlobal(fbuf); }

        var f2 = Query(factory, IPluginFactory2);
        var count = ((delegate* unmanaged<IntPtr, int>)Vt(factory)[4])(factory);
        var result = new List<PluginInfo>();
        var size = Math.Max(Marshal.SizeOf<PClassInfo2>(), Marshal.SizeOf<PClassInfo>());
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (f2 != IntPtr.Zero && ((delegate* unmanaged<IntPtr, int, IntPtr, int>)Vt(f2)[7])(f2, i, buf) == kResultOk)
                {
                    var c = Marshal.PtrToStructure<PClassInfo2>(buf);
                    if (c.Category != "Audio Module Class") continue;
                    result.Add(new PluginInfo(file, c.Cid.ToString(), c.Name, string.IsNullOrEmpty(c.Vendor) ? vendor : c.Vendor, c.Category, c.SubCategories ?? "", c.Version ?? ""));
                }
                else if (((delegate* unmanaged<IntPtr, int, IntPtr, int>)Vt(factory)[5])(factory, i, buf) == kResultOk)
                {
                    var c = Marshal.PtrToStructure<PClassInfo>(buf);
                    if (c.Category != "Audio Module Class") continue;
                    result.Add(new PluginInfo(file, c.Cid.ToString(), c.Name, vendor, c.Category, "", ""));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            Release(f2);
            Release(factory);
        }
        return result;
    }

    public static PluginList Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<PluginList>(File.ReadAllText(file)) ?? new() : new(); }
        catch { return new(); }
    }

    public static void Save(string file, PluginList list)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, file, true);
    }
}
