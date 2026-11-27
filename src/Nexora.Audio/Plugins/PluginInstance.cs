using System.Runtime.InteropServices;
using static Nexora.Audio.Plugins.Vst3;

namespace Nexora.Audio.Plugins;

/// <summary>One loaded plugin inside a plugin host process.</summary>
public interface IPluginInstance : IDisposable
{
    string Name { get; }
    /// <summary>The delay the plugin itself adds, in samples.</summary>
    int Latency { get; }
    bool HasEditor { get; }
    event Action? LatencyChanged;
    event Action? Edited;

    /// <summary>Processes one block of stereo sound (on the host's audio thread).</summary>
    void Process(float[] inL, float[] inR, float[] outL, float[] outR, int frames);

    /// <summary>The plugin's settings, to save with the scene (base64), or nulls when it has none.</summary>
    (string? Component, string? Controller) GetState();

    /// <summary>Opens the plugin's own window (on the host's main thread).</summary>
    void ShowEditor(string title);
    void CloseEditor();
}

/// <summary>
/// Stand-in plugins built into NEXORA, used to test the sandbox without any third-party plugin:
/// "builtin:gain" multiplies the sound, "builtin:crash" stops the host process after a while.
/// </summary>
public sealed class TestPlugin : IPluginInstance
{
    private readonly float _gain;
    private readonly int _crashAfter;
    private int _blocks;

    public TestPlugin(string kind, string? state)
    {
        Name = kind;
        _gain = state is { Length: > 0 } && float.TryParse(state, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 0.5f;
        _crashAfter = kind == "builtin:crash" ? 200 : -1;
        Latency = kind == "builtin:latency" ? 64 : 0;
    }

    public string Name { get; }
    public int Latency { get; }
    public bool HasEditor => false;
    public event Action? LatencyChanged { add { } remove { } }
    public event Action? Edited { add { } remove { } }

    public void Process(float[] inL, float[] inR, float[] outL, float[] outR, int frames)
    {
        if (_crashAfter > 0 && ++_blocks > _crashAfter) throw new InvalidOperationException("Test plugin crash");
        for (var n = 0; n < frames; n++)
        {
            outL[n] = inL[n] * _gain;
            outR[n] = inR[n] * _gain;
        }
    }

    public (string?, string?) GetState() => (_gain.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
    public void ShowEditor(string title) { }
    public void CloseEditor() { }
    public void Dispose() { }
}

/// <summary>A real VST3 plugin, loaded from its .vst3 file.</summary>
public sealed unsafe class Vst3Plugin : IPluginInstance
{
    private IntPtr _lib, _factory, _component, _processor, _controller, _view;
    private readonly HostApplication _host = new();
    private readonly ComponentHandler _handler = new();
    private readonly ParameterChanges _changes = new();
    private readonly PlugFrame _frame = new();
    private ProcessData* _data;
    private AudioBusBuffers* _in, _out;
    private float** _inPtrs, _outPtrs;
    private int _inCh = 2, _outCh = 2;
    private readonly int _maxFrames;
    private readonly double _sampleRate;
    private bool _active;
    private IntPtr _window;

    public string Name { get; }
    public int Latency { get; private set; }
    public bool HasEditor { get; private set; }
    public event Action? LatencyChanged;
    public event Action? Edited;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetFactoryFn();

    public Vst3Plugin(string path, Guid classId, double sampleRate, int maxFrames, string? componentState, string? controllerState)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("VST3 plugins run on Windows.");
        _sampleRate = sampleRate;
        _maxFrames = maxFrames;
        var dll = PluginScanner.BinaryOf(path) ?? throw new FileNotFoundException("The plugin file was not found.", path);
        _lib = NativeLibrary.Load(dll);
        if (NativeLibrary.TryGetExport(_lib, "InitDll", out var init)) ((delegate* unmanaged<byte>)init)();
        var get = NativeLibrary.GetExport(_lib, "GetPluginFactory");
        _factory = ((delegate* unmanaged<IntPtr>)get)();
        if (_factory == IntPtr.Zero) throw new InvalidOperationException("The plugin has no factory.");

        var iid = IComponent;
        IntPtr obj;
        Check(((delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)Vt(_factory)[6])(_factory, &classId, &iid, &obj), "create the plugin");
        _component = obj;
        var hostPtr = _host.Ptr;
        Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(_component)[3])(_component, hostPtr), "start the plugin");
        _processor = Query(_component, IAudioProcessor);
        if (_processor == IntPtr.Zero) throw new InvalidOperationException("This plugin does not process audio.");

        // The controller (the plugin's knobs and window) may be the same object or a separate one.
        _controller = Query(_component, IEditController);
        var separate = false;
        if (_controller == IntPtr.Zero)
        {
            Guid ctrlId;
            if (((delegate* unmanaged<IntPtr, Guid*, int>)Vt(_component)[5])(_component, &ctrlId) == kResultOk && ctrlId != Guid.Empty)
            {
                var eiid = IEditController;
                IntPtr c;
                if (((delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)Vt(_factory)[6])(_factory, &ctrlId, &eiid, &c) == kResultOk)
                {
                    _controller = c;
                    separate = true;
                    ((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(_controller)[3])(_controller, hostPtr);
                }
            }
        }
        if (separate)
        {
            var a = Query(_component, IConnectionPoint);
            var b = Query(_controller, IConnectionPoint);
            if (a != IntPtr.Zero && b != IntPtr.Zero)
            {
                ((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(a)[3])(a, b);
                ((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(b)[3])(b, a);
            }
        }
        if (_controller != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(_controller)[16])(_controller, _handler.Ptr);
            _handler.Edited += () => Edited?.Invoke();
            _handler.Restart += OnRestart;
        }

        // Saved settings.
        if (componentState is { Length: > 0 })
        {
            var bytes = Convert.FromBase64String(componentState);
            var s = new HostStream(bytes);
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(_component)[12])(_component, s.Ptr);
            if (_controller != IntPtr.Zero)
            {
                var s2 = new HostStream(bytes);
                ((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(_controller)[5])(_controller, s2.Ptr);
            }
        }
        if (controllerState is { Length: > 0 } && _controller != IntPtr.Zero)
        {
            var s = new HostStream(Convert.FromBase64String(controllerState));
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(_controller)[6])(_controller, s.Ptr);
        }

        // Stereo in and out (or what the plugin insists on).
        var ins = stackalloc ulong[1] { SpeakerStereo };
        var outs = stackalloc ulong[1] { SpeakerStereo };
        var nIn = ((delegate* unmanaged<IntPtr, int, int, int>)Vt(_component)[7])(_component, MediaAudio, DirInput);
        var nOut = ((delegate* unmanaged<IntPtr, int, int, int>)Vt(_component)[7])(_component, MediaAudio, DirOutput);
        if (((delegate* unmanaged<IntPtr, ulong*, int, ulong*, int, int>)Vt(_processor)[3])(_processor, ins, nIn > 0 ? 1 : 0, outs, nOut > 0 ? 1 : 0) != kResultOk)
        {
            ulong arr;
            if (nIn > 0 && ((delegate* unmanaged<IntPtr, int, int, ulong*, int>)Vt(_processor)[4])(_processor, DirInput, 0, &arr) == kResultOk) _inCh = Math.Clamp(BitCount(arr), 1, 2);
            if (nOut > 0 && ((delegate* unmanaged<IntPtr, int, int, ulong*, int>)Vt(_processor)[4])(_processor, DirOutput, 0, &arr) == kResultOk) _outCh = Math.Clamp(BitCount(arr), 1, 2);
        }
        if (nIn == 0) _inCh = 0;
        if (nIn > 0) ((delegate* unmanaged<IntPtr, int, int, int, byte, int>)Vt(_component)[10])(_component, MediaAudio, DirInput, 0, 1);
        if (nOut > 0) ((delegate* unmanaged<IntPtr, int, int, int, byte, int>)Vt(_component)[10])(_component, MediaAudio, DirOutput, 0, 1);

        if (((delegate* unmanaged<IntPtr, int, int>)Vt(_processor)[5])(_processor, 0) != kResultOk)
            throw new InvalidOperationException("This plugin cannot work with 32-bit sound.");
        var setup = new ProcessSetup { ProcessMode = 0, SymbolicSampleSize = 0, MaxSamplesPerBlock = maxFrames, SampleRate = sampleRate };
        Check(((delegate* unmanaged<IntPtr, ProcessSetup*, int>)Vt(_processor)[7])(_processor, &setup), "set up the plugin");

        _data = (ProcessData*)NativeMemory.AllocZeroed((nuint)sizeof(ProcessData));
        _in = (AudioBusBuffers*)NativeMemory.AllocZeroed((nuint)sizeof(AudioBusBuffers));
        _out = (AudioBusBuffers*)NativeMemory.AllocZeroed((nuint)sizeof(AudioBusBuffers));
        _inPtrs = (float**)NativeMemory.AllocZeroed((nuint)(2 * sizeof(IntPtr)));
        _outPtrs = (float**)NativeMemory.AllocZeroed((nuint)(2 * sizeof(IntPtr)));
        for (var c = 0; c < 2; c++)
        {
            _inPtrs[c] = (float*)NativeMemory.AllocZeroed((nuint)(maxFrames * sizeof(float)));
            _outPtrs[c] = (float*)NativeMemory.AllocZeroed((nuint)(maxFrames * sizeof(float)));
        }
        Activate();
        HasEditor = _controller != IntPtr.Zero;
        Name = Path.GetFileNameWithoutExtension(path);
    }

    private static int BitCount(ulong v) => System.Numerics.BitOperations.PopCount(v);

    private static void Check(int result, string what)
    {
        if (result != kResultOk) throw new InvalidOperationException($"The plugin could not {what} (code {result}).");
    }

    private void Activate()
    {
        ((delegate* unmanaged<IntPtr, byte, int>)Vt(_component)[11])(_component, 1);
        ((delegate* unmanaged<IntPtr, byte, int>)Vt(_processor)[8])(_processor, 1);
        Latency = (int)((delegate* unmanaged<IntPtr, uint>)Vt(_processor)[6])(_processor);
        _active = true;
    }

    private void Deactivate()
    {
        if (!_active) return;
        _active = false;
        ((delegate* unmanaged<IntPtr, byte, int>)Vt(_processor)[8])(_processor, 0);
        ((delegate* unmanaged<IntPtr, byte, int>)Vt(_component)[11])(_component, 0);
    }

    private volatile bool _restartLatency;

    private void OnRestart(int flags)
    {
        if ((flags & RestartLatencyChanged) != 0) _restartLatency = true;
    }

    /// <summary>Called by the host's main loop: applies a latency change the plugin asked for.</summary>
    public void Service(object audioLock)
    {
        if (!_restartLatency) return;
        _restartLatency = false;
        lock (audioLock)
        {
            Deactivate();
            Activate();
        }
        LatencyChanged?.Invoke();
    }

    public void Process(float[] inL, float[] inR, float[] outL, float[] outR, int frames)
    {
        if (!_active) return;
        frames = Math.Min(frames, _maxFrames);
        new ReadOnlySpan<float>(inL, 0, frames).CopyTo(new Span<float>(_inPtrs[0], frames));
        new ReadOnlySpan<float>(inR, 0, frames).CopyTo(new Span<float>(_inPtrs[1], frames));
        if (_inCh == 1)
            for (var n = 0; n < frames; n++) _inPtrs[0][n] = (inL[n] + inR[n]) * 0.5f;

        _changes.Clear();
        while (_handler.TryTake(out var e)) _changes.Add(e.Id, e.Value);

        _in->NumChannels = _inCh;
        _in->SilenceFlags = 0;
        _in->ChannelBuffers32 = _inPtrs;
        _out->NumChannels = _outCh;
        _out->SilenceFlags = 0;
        _out->ChannelBuffers32 = _outPtrs;
        _data->ProcessMode = 0;
        _data->SymbolicSampleSize = 0;
        _data->NumSamples = frames;
        _data->NumInputs = _inCh > 0 ? 1 : 0;
        _data->NumOutputs = 1;
        _data->Inputs = _inCh > 0 ? _in : null;
        _data->Outputs = _out;
        _data->InputParameterChanges = _changes.Ptr;
        ((delegate* unmanaged<IntPtr, ProcessData*, int>)Vt(_processor)[9])(_processor, _data);

        new ReadOnlySpan<float>(_outPtrs[0], frames).CopyTo(outL.AsSpan(0, frames));
        if (_outCh >= 2) new ReadOnlySpan<float>(_outPtrs[1], frames).CopyTo(outR.AsSpan(0, frames));
        else new ReadOnlySpan<float>(_outPtrs[0], frames).CopyTo(outR.AsSpan(0, frames));
    }

    public (string?, string?) GetState()
    {
        string? comp = null, ctrl = null;
        var s = new HostStream();
        if (((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(_component)[13])(_component, s.Ptr) == kResultOk) comp = Convert.ToBase64String(s.ToArray());
        if (_controller != IntPtr.Zero)
        {
            var c = new HostStream();
            if (((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(_controller)[7])(_controller, c.Ptr) == kResultOk) ctrl = Convert.ToBase64String(c.ToArray());
        }
        return (comp, ctrl);
    }

    // ---- The plugin's own window -------------------------------------------------------------

    public void ShowEditor(string title)
    {
        if (!OperatingSystem.IsWindows() || _controller == IntPtr.Zero) return;
        if (_window != IntPtr.Zero)
        {
            Win32.Show(_window);
            return;
        }
        var name = Marshal.StringToHGlobalAnsi("editor");
        try { _view = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Vt(_controller)[17])(_controller, name); }
        finally { Marshal.FreeHGlobal(name); }
        if (_view == IntPtr.Zero) return;
        var hwndType = Marshal.StringToHGlobalAnsi("HWND");
        try
        {
            if (((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(_view)[3])(_view, hwndType) != kResultOk) return;
            ViewRect r;
            if (((delegate* unmanaged<IntPtr, ViewRect*, int>)Vt(_view)[9])(_view, &r) != kResultOk || r.Width <= 0) r = new ViewRect { Right = 600, Bottom = 400 };
            _window = Win32.CreateWindow(title, r.Width, r.Height, OnWindowClosed);
            _frame.OnResize = (v, rect) =>
            {
                Win32.ResizeClient(_window, rect.Width, rect.Height);
                var rr = rect;
                ((delegate* unmanaged<IntPtr, ViewRect*, int>)Vt(v)[10])(v, &rr);
                return true;
            };
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(_view)[12])(_view, _frame.Ptr);
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Vt(_view)[4])(_view, _window, hwndType);
            Win32.Show(_window);
        }
        finally { Marshal.FreeHGlobal(hwndType); }
    }

    private void OnWindowClosed() => CloseEditor();

    public void CloseEditor()
    {
        if (_view != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, int>)Vt(_view)[5])(_view);
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(_view)[12])(_view, IntPtr.Zero);
            Release(_view);
            _view = IntPtr.Zero;
        }
        if (_window != IntPtr.Zero)
        {
            Win32.Destroy(_window);
            _window = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        try
        {
            CloseEditor();
            Deactivate();
            if (_controller != IntPtr.Zero && _controller != _component)
            {
                ((delegate* unmanaged<IntPtr, int>)Vt(_controller)[4])(_controller);
                Release(_controller);
            }
            if (_component != IntPtr.Zero)
            {
                ((delegate* unmanaged<IntPtr, int>)Vt(_component)[4])(_component);
                Release(_component);
            }
            Release(_factory);
        }
        catch (Exception) { }
        // The library stays loaded: some plugins misbehave when unloaded, and the process ends soon anyway.
    }
}

/// <summary>Just enough of the Windows window functions to give a plugin a window of its own.</summary>
internal static unsafe class Win32
{
    private const uint WsOverlapped = 0x00000000, WsCaption = 0x00C00000, WsSysMenu = 0x00080000, WsMinimize = 0x00020000, WsClipChildren = 0x02000000;
    private const int WmClose = 0x0010, WmDestroy = 0x0002;
    private static readonly Dictionary<IntPtr, Action> Closers = [];
    private static bool _registered;
    private static readonly string ClassName = "NexoraPluginWindow";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public IntPtr WndProc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRect(ref Rect r, uint style, bool menu);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(IntPtr name);
    [DllImport("user32.dll")] public static extern int GetMessageW(out Msg m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Msg m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref Msg m);
    [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    private const uint Style = WsOverlapped | WsCaption | WsSysMenu | WsMinimize | WsClipChildren;

    public static IntPtr CreateWindow(string title, int width, int height, Action onClose)
    {
        if (!_registered)
        {
            var c = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
                Instance = GetModuleHandleW(IntPtr.Zero),
                ClassName = ClassName,
                Background = (IntPtr)6,
            };
            RegisterClassExW(ref c);
            _registered = true;
        }
        var r = new Rect { Right = width, Bottom = height };
        AdjustWindowRect(ref r, Style, false);
        var h = CreateWindowExW(0, ClassName, title, Style, 120, 120, r.Right - r.Left, r.Bottom - r.Top, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
        Closers[h] = onClose;
        return h;
    }

    public static void ResizeClient(IntPtr h, int width, int height)
    {
        var r = new Rect { Right = width, Bottom = height };
        AdjustWindowRect(ref r, Style, false);
        SetWindowPos(h, IntPtr.Zero, 0, 0, r.Right - r.Left, r.Bottom - r.Top, 0x0002 | 0x0004); // no move, no z-order
    }

    public static void Show(IntPtr h)
    {
        ShowWindow(h, 5);
        SetForegroundWindow(h);
    }

    public static void Destroy(IntPtr h)
    {
        Closers.Remove(h);
        DestroyWindow(h);
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WmClose)
        {
            // Closing the window keeps the plugin running; only its window goes.
            if (Closers.TryGetValue(h, out var close)) close();
            return IntPtr.Zero;
        }
        return DefWindowProcW(h, m, w, l);
    }
}
