using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Nexora.App.Services;

namespace Nexora.App.Controls;

/// <summary>Lists the capture cards, webcams and sound inputs Windows knows about (their DirectShow names).</summary>
public static class DirectShow
{
    private static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid VideoInputCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid AudioInputCategory = new("33D9A762-90C8-11d0-BD43-00A0C911CE86");

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid category, out IEnumMoniker? enumerator, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object? value, IntPtr errorLog);

        [PreserveSig]
        int Write([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    public static IReadOnlyList<string> VideoDevices() => List(VideoInputCategory);

    public static IReadOnlyList<string> AudioDevices() => List(AudioInputCategory);

    private static IReadOnlyList<string> List(Guid category)
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows()) return names;
        try
        {
            var type = Type.GetTypeFromCLSID(SystemDeviceEnum);
            if (type is null || Activator.CreateInstance(type) is not ICreateDevEnum devEnum) return names;
            if (devEnum.CreateClassEnumerator(ref category, out var en, 0) != 0 || en is null) return names;
            var bagId = typeof(IPropertyBag).GUID;
            var monikers = new IMoniker[1];
            while (en.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var m = monikers[0];
                try
                {
                    m.BindToStorage(null!, null!, ref bagId, out var bagObj);
                    if (bagObj is IPropertyBag bag && bag.Read("FriendlyName", out var v, IntPtr.Zero) == 0 && v is string s && s.Length > 0)
                        names.Add(s);
                    if (bagObj is not null) Marshal.ReleaseComObject(bagObj);
                }
                catch (Exception) { /* a device that will not describe itself is skipped */ }
                finally { Marshal.ReleaseComObject(m); }
            }
            Marshal.ReleaseComObject(en);
            Marshal.ReleaseComObject(devEnum);
        }
        catch (Exception ex)
        {
            AppLog.Write("DirectShow: " + ex.Message);
        }
        return names.Distinct().ToList();
    }
}
