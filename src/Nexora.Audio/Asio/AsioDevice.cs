using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Nexora.Audio.Engine;

namespace Nexora.Audio.Asio;

/// <summary>An ASIO driver listed in Windows (for the X32: the Behringer X-USB ASIO driver).</summary>
public sealed record AsioDriverInfo(string Name, Guid Clsid)
{
    /// <summary>True for drivers that look like the X32's own USB card.</summary>
    public bool LooksLikeX32 =>
        Name.Contains("X-USB", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("X32", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Behringer", StringComparison.OrdinalIgnoreCase);
}

public static class AsioDriverList
{
    /// <summary>The ASIO drivers installed on this PC (they register themselves under HKLM\SOFTWARE\ASIO).</summary>
    public static IReadOnlyList<AsioDriverInfo> Find()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return FindWindows();
    }

    [SupportedOSPlatform("windows")]
    private static List<AsioDriverInfo> FindWindows()
    {
        var list = new List<AsioDriverInfo>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASIO");
            if (root is null) return list;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("CLSID") is string clsid && Guid.TryParse(clsid.Trim('{', '}', ' '), out var g))
                    list.Add(new AsioDriverInfo(name, g));
            }
        }
        catch (Exception) { }
        return list.OrderByDescending(d => d.LooksLikeX32).ThenBy(d => d.Name).ToList();
    }
}

/// <summary>
/// Talks to an ASIO driver directly, the way pro audio programs do, with no extra layers.
/// Every call except the buffer callback must come from the one thread that opened the driver.
/// The buffer callback converts, runs <see cref="EngineCore"/> and converts back, with no allocations.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class AsioDevice : IAudioDevice
{
    private const int AseOk = 0, AseSuccess = 0x3f4847a0;

    // Only one ASIO driver can run in a process, so the driver's callbacks find it here.
    private static AsioDevice? s_current;

    private readonly AsioDriverInfo _info;
    private IntPtr _drv;
    private IntPtr* _vt;
    private BufferInfo* _buffers;
    private Callbacks* _callbacks;
    private SampleFormat[] _inFormats = [], _outFormats = [];
    private int _nIn, _nOut, _frames;
    private double _bufferSeconds;
    private bool _postOutput, _running;
    private EngineCore? _core;
    private AudioStats? _stats;
    private volatile bool _reset;

    public AsioDevice(AsioDriverInfo info) => _info = info;

    public string Name => _info.Name;
    public DeviceFormat? Format { get; private set; }
    public bool HasSettingsPanel => _drv != IntPtr.Zero;
    public bool ResetRequested => _reset;

    /// <summary>The rates NEX-Audio offers, when the driver can do them.</summary>
    private static readonly double[] Candidates = [44100, 48000, 88200, 96000];

    public void Open(int bufferSize, double sampleRate = 0)
    {
        if (_drv != IntPtr.Zero) Close();
        var clsid = _info.Clsid;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref clsid, out _drv);
        if (hr != 0 || _drv == IntPtr.Zero)
            throw new AudioDeviceException($"Windows could not load the driver \"{Name}\" (code 0x{hr:X8}). Reinstall the X32 USB driver and try again.");
        _vt = *(IntPtr**)_drv;

        if (((delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int>)_vt[3])(_drv, IntPtr.Zero) == 0)
        {
            var why = ErrorMessage();
            Release();
            throw new AudioDeviceException($"The driver \"{Name}\" did not start: {why}. Check the X32 is switched on and its USB cable is plugged into this PC.");
        }

        int nIn, nOut;
        Check(((delegate* unmanaged[Thiscall]<IntPtr, int*, int*, int>)_vt[9])(_drv, &nIn, &nOut), "read the channel count");
        int min, max, pref, gran;
        Check(((delegate* unmanaged[Thiscall]<IntPtr, int*, int*, int*, int*, int>)_vt[11])(_drv, &min, &max, &pref, &gran), "read the buffer sizes");
        // Which rates the driver can run at (canSampleRate), and switch when another one is asked for (setSampleRate).
        var rates = new List<double>();
        foreach (var c in Candidates)
            if (((delegate* unmanaged[Thiscall]<IntPtr, double, int>)_vt[12])(_drv, c) == AseOk) rates.Add(c);
        double rate;
        Check(((delegate* unmanaged[Thiscall]<IntPtr, double*, int>)_vt[13])(_drv, &rate), "read the sample rate");
        if (sampleRate > 0 && Math.Abs(rate - sampleRate) >= 1 && rates.Any(r => Math.Abs(r - sampleRate) < 1)
            && ((delegate* unmanaged[Thiscall]<IntPtr, double, int>)_vt[14])(_drv, sampleRate) == AseOk)
            Check(((delegate* unmanaged[Thiscall]<IntPtr, double*, int>)_vt[13])(_drv, &rate), "read the sample rate");

        var sizes = AllowedSizes(min, max, pref, gran);
        var frames = bufferSize > 0 && sizes.Contains(bufferSize) ? bufferSize : pref;

        // NEX-Audio uses every channel the driver offers, up to 64 each way.
        _nIn = Math.Min(nIn, 64);
        _nOut = Math.Min(nOut, 64);
        _inFormats = new SampleFormat[_nIn];
        _outFormats = new SampleFormat[_nOut];
        var inNames = new List<string>();
        var outNames = new List<string>();
        string? typeName = null;
        for (var i = 0; i < _nIn + _nOut; i++)
        {
            var input = i < _nIn;
            ChannelInfo ci = default;
            ci.Channel = input ? i : i - _nIn;
            ci.IsInput = input ? 1 : 0;
            Check(((delegate* unmanaged[Thiscall]<IntPtr, ChannelInfo*, int>)_vt[18])(_drv, &ci), "read channel details");
            if (!SampleConvert.IsSupported(ci.Type))
                throw new AudioDeviceException($"The driver \"{Name}\" uses a sample format NEX-Audio does not support yet (type {ci.Type}).");
            var f = (SampleFormat)ci.Type;
            typeName ??= Describe(f);
            if (input)
            {
                _inFormats[i] = f;
                inNames.Add(AnsiName(ci.Name, 32));
            }
            else
            {
                _outFormats[i - _nIn] = f;
                outNames.Add(AnsiName(ci.Name, 32));
            }
        }

        var count = _nIn + _nOut;
        _buffers = (BufferInfo*)NativeMemory.AllocZeroed((nuint)(count * sizeof(BufferInfo)));
        for (var i = 0; i < count; i++)
        {
            _buffers[i].IsInput = i < _nIn ? 1 : 0;
            _buffers[i].ChannelNum = i < _nIn ? i : i - _nIn;
        }
        _callbacks = (Callbacks*)NativeMemory.AllocZeroed((nuint)sizeof(Callbacks));
        _callbacks->BufferSwitch = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, void>)&OnBufferSwitch;
        _callbacks->SampleRateDidChange = (IntPtr)(delegate* unmanaged[Cdecl]<double, void>)&OnSampleRateChanged;
        _callbacks->AsioMessage = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, IntPtr, double*, int>)&OnAsioMessage;
        _callbacks->BufferSwitchTimeInfo = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, IntPtr>)&OnBufferSwitchTimeInfo;

        s_current = this;
        Check(((delegate* unmanaged[Thiscall]<IntPtr, BufferInfo*, int, int, Callbacks*, int>)_vt[19])(_drv, _buffers, count, frames, _callbacks),
            $"set up its buffers at {frames} samples");

        int inLat, outLat;
        if (((delegate* unmanaged[Thiscall]<IntPtr, int*, int*, int>)_vt[10])(_drv, &inLat, &outLat) != AseOk)
        {
            inLat = frames;
            outLat = frames;
        }
        // Drivers that support outputReady get the output sooner, which cuts one buffer of delay.
        _postOutput = ((delegate* unmanaged[Thiscall]<IntPtr, int>)_vt[23])(_drv) == AseOk;

        _frames = frames;
        _bufferSeconds = frames / rate;
        _reset = false;
        Format = new DeviceFormat(_nIn, _nOut, rate, frames, inLat, outLat, typeName ?? "", sizes, inNames, outNames) { SampleRates = rates };
    }

    public void Start(EngineCore core, AudioStats stats)
    {
        if (_drv == IntPtr.Zero) throw new AudioDeviceException("The driver is not open.");
        _core = core;
        _stats = stats;
        s_current = this;
        Check(((delegate* unmanaged[Thiscall]<IntPtr, int>)_vt[7])(_drv), "start");
        _running = true;
    }

    public void Stop()
    {
        if (_drv == IntPtr.Zero || !_running) return;
        ((delegate* unmanaged[Thiscall]<IntPtr, int>)_vt[8])(_drv);
        _running = false;
    }

    public void Close()
    {
        if (_drv == IntPtr.Zero) return;
        Stop();
        ((delegate* unmanaged[Thiscall]<IntPtr, int>)_vt[20])(_drv); // disposeBuffers
        Release();
        if (_buffers != null) NativeMemory.Free(_buffers);
        if (_callbacks != null) NativeMemory.Free(_callbacks);
        _buffers = null;
        _callbacks = null;
        _core = null;
        if (s_current == this) s_current = null;
        Format = null;
    }

    public void ShowSettingsPanel()
    {
        if (_drv != IntPtr.Zero) ((delegate* unmanaged[Thiscall]<IntPtr, int>)_vt[21])(_drv);
    }

    public void Dispose() => Close();

    private void Release()
    {
        if (_drv == IntPtr.Zero) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)_vt[2])(_drv);
        _drv = IntPtr.Zero;
        _vt = null;
    }

    // ---- Callbacks from the driver's audio thread ----------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBufferSwitch(int index, int direct) => s_current?.Buffer(index);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr OnBufferSwitchTimeInfo(IntPtr time, int index, int direct)
    {
        s_current?.Buffer(index);
        return time;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSampleRateChanged(double rate)
    {
        if (s_current is { } d) d._reset = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnAsioMessage(int selector, int value, IntPtr message, double* opt)
    {
        var d = s_current;
        switch (selector)
        {
            case 1: // which messages do you understand?
                return value is 2 or 3 or 4 or 5 or 6 or 15 ? 1 : 0;
            case 2: // engine version
                return 2;
            case 3: // reset request (for example the buffer size was changed in the driver's settings)
            case 4: // buffer size change
                if (d is not null) d._reset = true;
                return 1;
            case 5: // the driver lost sync, so a click was probably heard
                d?._stats?.Resync();
                return 1;
            case 6: // latencies changed
                if (d is not null) d._reset = true;
                return 1;
            case 15: // overload
                d?._stats?.Overload();
                return 1;
            default:
                return 0;
        }
    }

    private void Buffer(int index)
    {
        var core = _core;
        var stats = _stats;
        if (core is null || _buffers == null) return;
        var begin = Stopwatch.GetTimestamp();
        var frames = _frames;
        for (var i = 0; i < _nIn; i++)
        {
            var p = index == 0 ? _buffers[i].Buffer0 : _buffers[i].Buffer1;
            SampleConvert.ToFloat(_inFormats[i], (void*)p, core.In[i], frames);
        }
        core.Process(frames);
        for (var o = 0; o < _nOut; o++)
        {
            var b = _buffers[_nIn + o];
            var p = index == 0 ? b.Buffer0 : b.Buffer1;
            SampleConvert.FromFloat(_outFormats[o], core.Out[o], (void*)p, frames);
        }
        if (_postOutput) ((delegate* unmanaged[Thiscall]<IntPtr, int>)_vt[23])(_drv);
        stats?.Record(begin, Stopwatch.GetTimestamp(), _bufferSeconds);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private void Check(int result, string what)
    {
        if (result is AseOk or AseSuccess) return;
        var why = ErrorMessage();
        throw new AudioDeviceException($"The driver \"{Name}\" could not {what}: {why} (code {result}).");
    }

    private string ErrorMessage()
    {
        if (_drv == IntPtr.Zero) return "no details";
        var buf = stackalloc byte[256];
        ((delegate* unmanaged[Thiscall]<IntPtr, byte*, void>)_vt[6])(_drv, buf);
        var s = AnsiName(buf, 256);
        return s.Length == 0 ? "no details" : s;
    }

    private static string AnsiName(byte* p, int max)
    {
        var len = 0;
        while (len < max && p[len] != 0) len++;
        return Encoding.Latin1.GetString(p, len).Trim();
    }

    internal static List<int> AllowedSizes(int min, int max, int pref, int gran)
    {
        var list = new List<int>();
        if (min <= 0 || max < min) return [pref];
        if (gran == -1)
        {
            for (var s = min; s <= max; s *= 2) list.Add(s);
        }
        else if (gran <= 0 || min == max)
        {
            list.Add(min);
            if (max != min) list.Add(max);
        }
        else
        {
            for (var s = min; s <= max && list.Count < 64; s += gran)
                if (s is 16 or 32 or 48 or 64 or 96 or 128 or 192 or 256 or 384 or 512 or 1024 || s == pref || s == min || s == max) list.Add(s);
        }
        if (!list.Contains(pref)) list.Add(pref);
        list.Sort();
        return list.Distinct().ToList();
    }

    private static string Describe(SampleFormat f) => f switch
    {
        SampleFormat.Int16 => "16-bit",
        SampleFormat.Int24 => "24-bit",
        SampleFormat.Int32 => "32-bit",
        SampleFormat.Int32In24 => "24-bit in 32",
        SampleFormat.Float32 => "32-bit float",
        SampleFormat.Float64 => "64-bit float",
        _ => f.ToString(),
    };

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferInfo
    {
        public int IsInput;
        public int ChannelNum;
        public IntPtr Buffer0;
        public IntPtr Buffer1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChannelInfo
    {
        public int Channel;
        public int IsInput;
        public int IsActive;
        public int ChannelGroup;
        public int Type;
        public fixed byte Name[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public IntPtr BufferSwitch;
        public IntPtr SampleRateDidChange;
        public IntPtr AsioMessage;
        public IntPtr BufferSwitchTimeInfo;
    }
}

public sealed class AudioDeviceException(string message) : Exception(message);
