using System.Runtime.InteropServices;
using Nexora.App.Services;

namespace Nexora.App.Controls;

/// <summary>
/// The NDI runtime, loaded at run time from the folder the NDI installer sets (NDI_RUNTIME_DIR_V6 or V5).
/// When the runtime is not installed, everything here reports "not available" and the rest of the app carries on.
/// </summary>
public static unsafe class Ndi
{
    private const string Lib = "Processing.NDI.Lib.x64.dll";
    private static readonly object Gate = new();
    private static bool _tried;
    private static IntPtr _lib;
    private static IntPtr _finder;

    public static string? Problem { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FindCreate
    {
        [MarshalAs(UnmanagedType.U1)] public bool ShowLocal;
        public IntPtr Groups;
        public IntPtr ExtraIps;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Source
    {
        public IntPtr Name;
        public IntPtr Url;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecvCreate
    {
        public Source Source;
        public int ColorFormat;   // 0 = BGRX/BGRA
        public int Bandwidth;     // 100 = highest
        [MarshalAs(UnmanagedType.U1)] public bool AllowFielded;
        public IntPtr Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrame
    {
        public int XRes, YRes;
        public uint FourCC;
        public int FrameRateN, FrameRateD;
        public float Aspect;
        public int Format;
        public long Timecode;
        public IntPtr Data;
        public int Stride;
        public IntPtr Metadata;
        public long Timestamp;
    }

    /// <summary>NDIlib_audio_frame_v2_t: floating point, one block per channel.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrame
    {
        public int SampleRate;
        public int Channels;
        public int Samples;
        public long Timecode;
        public IntPtr Data;
        public int ChannelStride;
        public IntPtr Metadata;
        public long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SendCreate
    {
        public IntPtr Name;
        public IntPtr Groups;
        [MarshalAs(UnmanagedType.U1)] public bool ClockVideo;
        [MarshalAs(UnmanagedType.U1)] public bool ClockAudio;
    }

    private delegate bool InitializeFn();
    private delegate IntPtr FindCreateFn(ref FindCreate create);
    private delegate IntPtr FindSourcesFn(IntPtr finder, out uint count);
    private delegate IntPtr RecvCreateFn(ref RecvCreate create);
    private delegate void RecvDestroyFn(IntPtr recv);
    private delegate int RecvCaptureFn(IntPtr recv, out VideoFrame video, IntPtr audio, IntPtr meta, uint timeoutMs);
    private delegate void RecvFreeVideoFn(IntPtr recv, ref VideoFrame video);
    private delegate int RecvCaptureAudioFn(IntPtr recv, IntPtr video, out AudioFrame audio, IntPtr meta, uint timeoutMs);
    private delegate void RecvFreeAudioFn(IntPtr recv, ref AudioFrame audio);
    private delegate IntPtr SendCreateFn(ref SendCreate create);
    private delegate void SendDestroyFn(IntPtr send);
    private delegate void SendVideoFn(IntPtr send, ref VideoFrame video);

    private static FindSourcesFn? _sources;
    private static RecvCreateFn? _recvCreate;
    private static RecvDestroyFn? _recvDestroy;
    private static RecvCaptureFn? _capture;
    private static RecvFreeVideoFn? _free;
    private static RecvCaptureAudioFn? _captureAudio;
    private static RecvFreeAudioFn? _freeAudio;
    private static SendCreateFn? _sendCreate;
    private static SendDestroyFn? _sendDestroy;
    private static SendVideoFn? _sendVideo;

    public static bool Available
    {
        get
        {
            lock (Gate)
            {
                if (_tried) return _lib != IntPtr.Zero;
                _tried = true;
                try { Load(); }
                catch (Exception ex)
                {
                    _lib = IntPtr.Zero;
                    Problem = "NDI is not installed on this PC. Install the free NDI Tools (ndi.video/tools) to use NDI cameras.";
                    AppLog.Write("NDI: " + ex.Message);
                }
                return _lib != IntPtr.Zero;
            }
        }
    }

    private static void Load()
    {
        var dirs = new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" }
            .Select(Environment.GetEnvironmentVariable).Where(d => !string.IsNullOrWhiteSpace(d)).Cast<string>()
            .Append(AppContext.BaseDirectory);
        foreach (var d in dirs)
        {
            var p = Path.Combine(d, Lib);
            if (File.Exists(p) && NativeLibrary.TryLoad(p, out _lib)) break;
        }
        if (_lib == IntPtr.Zero && !NativeLibrary.TryLoad(Lib, out _lib)) throw new DllNotFoundException(Lib);

        T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));
        if (!Fn<InitializeFn>("NDIlib_initialize")()) throw new InvalidOperationException("NDI could not start (this CPU may be unsupported).");
        _sources = Fn<FindSourcesFn>("NDIlib_find_get_current_sources");
        _recvCreate = Fn<RecvCreateFn>("NDIlib_recv_create_v3");
        _recvDestroy = Fn<RecvDestroyFn>("NDIlib_recv_destroy");
        _capture = Fn<RecvCaptureFn>("NDIlib_recv_capture_v2");
        _free = Fn<RecvFreeVideoFn>("NDIlib_recv_free_video_v2");
        _captureAudio = Fn<RecvCaptureAudioFn>("NDIlib_recv_capture_v2");
        _freeAudio = Fn<RecvFreeAudioFn>("NDIlib_recv_free_audio_v2");
        _sendCreate = Fn<SendCreateFn>("NDIlib_send_create");
        _sendDestroy = Fn<SendDestroyFn>("NDIlib_send_destroy");
        _sendVideo = Fn<SendVideoFn>("NDIlib_send_send_video_v2");
        var fc = new FindCreate { ShowLocal = true };
        _finder = Fn<FindCreateFn>("NDIlib_find_create_v2")(ref fc);
    }

    /// <summary>The NDI sources seen on the network right now ("PC NAME (Camera 1)").</summary>
    public static IReadOnlyList<string> Sources()
    {
        if (!Available || _finder == IntPtr.Zero || _sources is null) return [];
        try
        {
            var arr = _sources(_finder, out var n);
            var list = new List<string>();
            for (var i = 0; i < n; i++)
            {
                var s = Marshal.PtrToStructure<Source>(arr + i * sizeof(Source));
                if (Marshal.PtrToStringUTF8(s.Name) is { Length: > 0 } name) list.Add(name);
            }
            return list;
        }
        catch (Exception ex)
        {
            AppLog.Write("NDI sources: " + ex.Message);
            return [];
        }
    }

    /// <summary>A receiver for the sound only (no picture), for listening to the pastor's mic over NDI.</summary>
    internal static IntPtr AudioReceiver(string sourceName) => Receiver(sourceName, -10);

    /// <summary>2 = sound, 0 = nothing, -1 = error.</summary>
    internal static int CaptureAudio(IntPtr recv, out AudioFrame frame, uint timeoutMs)
    {
        frame = default;
        if (_captureAudio is null) return -1;
        var kind = _captureAudio(recv, IntPtr.Zero, out frame, IntPtr.Zero, timeoutMs);
        return kind switch { 2 => 2, 4 => -1, _ => 0 };
    }

    internal static void FreeAudio(IntPtr recv, ref AudioFrame frame) => _freeAudio?.Invoke(recv, ref frame);

    internal static IntPtr Receiver(string sourceName) => Receiver(sourceName, 100);

    private static IntPtr Receiver(string sourceName, int bandwidth)
    {
        if (!Available || _recvCreate is null) return IntPtr.Zero;
        var name = Marshal.StringToCoTaskMemUTF8(sourceName);
        var me = Marshal.StringToCoTaskMemUTF8("NEXORA");
        try
        {
            var rc = new RecvCreate { Source = new Source { Name = name }, ColorFormat = 0, Bandwidth = bandwidth, AllowFielded = false, Name = me };
            return _recvCreate(ref rc);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(me);
        }
    }

    internal static void FreeReceiver(IntPtr r)
    {
        if (r != IntPtr.Zero) _recvDestroy?.Invoke(r);
    }

    /// <summary>1 = video, 0 = nothing, -1 = error.</summary>
    internal static int Capture(IntPtr recv, out VideoFrame frame, uint timeoutMs)
    {
        frame = default;
        if (_capture is null) return -1;
        var kind = _capture(recv, out frame, IntPtr.Zero, IntPtr.Zero, timeoutMs);
        return kind switch { 1 => 1, 4 => -1, _ => 0 }; // 1 video, 4 error
    }

    internal static void Free(IntPtr recv, ref VideoFrame frame) => _free?.Invoke(recv, ref frame);

    /// <summary>A named NDI output, so other PCs (and OBS, vMix…) can take this picture.</summary>
    public sealed class Sender : IDisposable
    {
        private IntPtr _send;

        public Sender(string name)
        {
            if (!Available || _sendCreate is null) return;
            var n = Marshal.StringToCoTaskMemUTF8(name);
            try
            {
                var sc = new SendCreate { Name = n, ClockVideo = false, ClockAudio = false };
                _send = _sendCreate(ref sc);
            }
            finally { Marshal.FreeCoTaskMem(n); }
        }

        public bool Ok => _send != IntPtr.Zero;

        public void Send(IntPtr bgra, int width, int height, int stride, int fps = 30)
        {
            if (_send == IntPtr.Zero || _sendVideo is null) return;
            var f = new VideoFrame
            {
                XRes = width, YRes = height, FourCC = 0x41524742, FrameRateN = fps * 1000, FrameRateD = 1000,
                Aspect = width / (float)height, Format = 1, Timecode = long.MaxValue, Data = bgra, Stride = stride,
            };
            _sendVideo(_send, ref f);
        }

        public void Dispose()
        {
            if (_send != IntPtr.Zero) _sendDestroy?.Invoke(_send);
            _send = IntPtr.Zero;
        }
    }
}

/// <summary>A picture received over NDI, on its own thread.</summary>
public sealed class NdiFeed : VideoFeed
{
    private readonly CancellationTokenSource _stop = new();

    public NdiFeed(string source)
    {
        if (!Ndi.Available)
        {
            Problem = Ndi.Problem;
            return;
        }
        var t = new Thread(() => Run(source)) { IsBackground = true, Name = "NDI " + source };
        t.Start();
    }

    private void Run(string source)
    {
        var recv = IntPtr.Zero;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (recv == IntPtr.Zero)
                {
                    recv = Ndi.Receiver(source);
                    if (recv == IntPtr.Zero)
                    {
                        Problem = "Cannot connect to " + source;
                        Thread.Sleep(1000);
                        continue;
                    }
                }
                var kind = Ndi.Capture(recv, out var f, 500);
                if (kind == 1)
                {
                    try
                    {
                        Problem = null;
                        Publish(f.Data, f.XRes, f.YRes, f.Stride);
                    }
                    finally { Ndi.Free(recv, ref f); }
                }
                else if (kind < 0)
                {
                    Ndi.FreeReceiver(recv);
                    recv = IntPtr.Zero;
                    Thread.Sleep(500);
                }
            }
        }
        catch (Exception ex)
        {
            Problem = "NDI stopped: " + ex.Message;
            AppLog.Write(Problem);
        }
        finally { Ndi.FreeReceiver(recv); }
    }

    public override void Dispose() => _stop.Cancel();
}
