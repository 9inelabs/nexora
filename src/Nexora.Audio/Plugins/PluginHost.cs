using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexora.Audio.Plugins;

/// <summary>
/// The memory shared between the sound engine and one plugin host process.
/// The engine writes a block of sound and raises "posted"; the host processes it and raises "done".
/// The engine never waits: if the host is late (or has crashed) the engine plays the sound through unchanged.
/// </summary>
public sealed unsafe class PluginShm : IDisposable
{
    public const int Magic = 0x3150584E; // "NXP1"
    private const int HeaderBytes = 64;
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;

    public PluginShm(string path, int block, bool create)
    {
        Block = block;
        var size = HeaderBytes + 4L * block * sizeof(float);
        if (create)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)) fs.SetLength(size);
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        _file = MemoryMappedFile.CreateFromFile(stream, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
        _view = _file.CreateViewAccessor(0, size);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
        if (create)
        {
            *(int*)(_base + 4) = block;
            *(int*)_base = Magic;
        }
        else if (*(int*)_base != Magic || *(int*)(_base + 4) != block)
            throw new InvalidDataException("The shared sound memory is not set up.");
        InL = (float*)(_base + HeaderBytes);
        InR = InL + block;
        OutL = InR + block;
        OutR = OutL + block;
    }

    public int Block { get; }
    public float* InL { get; }
    public float* InR { get; }
    public float* OutL { get; }
    public float* OutR { get; }

    /// <summary>The number of the last block the engine handed over.</summary>
    public int Posted { get => Volatile.Read(ref *(int*)(_base + 8)); set => Volatile.Write(ref *(int*)(_base + 8), value); }
    /// <summary>The number of the last block the host finished.</summary>
    public int Done { get => Volatile.Read(ref *(int*)(_base + 12)); set => Volatile.Write(ref *(int*)(_base + 12), value); }
    /// <summary>The plugin's own delay, in samples, as the host last saw it.</summary>
    public int Latency { get => Volatile.Read(ref *(int*)(_base + 16)); set => Volatile.Write(ref *(int*)(_base + 16), value); }
    /// <summary>Counts up while the host is alive.</summary>
    public int Beat { get => Volatile.Read(ref *(int*)(_base + 20)); set => Volatile.Write(ref *(int*)(_base + 20), value); }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>Wakes the host when a block is waiting. A named event on Windows; elsewhere the host checks often.</summary>
public sealed class PluginSignal : IDisposable
{
    private readonly EventWaitHandle? _event;

    public PluginSignal(string? name)
    {
        if (name is { Length: > 0 } && name != "-" && OperatingSystem.IsWindows())
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, name);
    }

    public void Set() => _event?.Set();

    /// <summary>Waits up to the given time for a signal (or briefly yields when there is no event).</summary>
    public void Wait(int ms)
    {
        if (_event != null) _event.WaitOne(ms);
        else Thread.Sleep(0);
    }

    public void Dispose() => _event?.Dispose();
}

/// <summary>
/// The "--plugin-host" process: runs one plugin, away from the sound engine, so a plugin that crashes
/// only takes itself down. Commands arrive on standard input and replies go out on standard output,
/// one JSON object per line.
/// </summary>
public static class PluginHost
{
    /// <summary>Runs a host process: "--plugin-host &lt;memory file&gt; &lt;event name or -&gt; &lt;block&gt; &lt;sample rate&gt;".</summary>
    public static int RunProcess(string[] args)
    {
        var i = Array.IndexOf(args, "--plugin-host");
        if (i < 0 || args.Length < i + 5) return 2;
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var stdin = new StreamReader(Console.OpenStandardInput());
        return Run(args[i + 1], args[i + 2], int.Parse(args[i + 3]), double.Parse(args[i + 4], System.Globalization.CultureInfo.InvariantCulture),
            stdin, stdout, () => Environment.Exit(3));
    }

    /// <summary>
    /// The host itself. <paramref name="onFatal"/> runs when the plugin fails while processing sound
    /// (a real host process simply ends there).
    /// </summary>
    public static int Run(string shmPath, string eventName, int block, double sampleRate, TextReader input, TextWriter output, Action onFatal)
    {
        var send = new object();
        void Send(JsonObject o)
        {
            lock (send)
            {
                try { output.WriteLine(o.ToJsonString()); } catch { }
            }
        }

        PluginShm shm;
        try { shm = new PluginShm(shmPath, block, false); }
        catch (Exception ex)
        {
            Send(new JsonObject { ["ev"] = "error", ["message"] = ex.Message });
            return 1;
        }
        using var _shm = shm;
        using var signal = new PluginSignal(eventName);

        var mainQueue = new BlockingCollection<Action>();
        var windows = OperatingSystem.IsWindows();
        uint mainThread = windows ? Win32.GetCurrentThreadId() : 0;
        void OnMain(Action a)
        {
            mainQueue.Add(a);
            if (windows) Win32.PostThreadMessageW(mainThread, 0x0400, IntPtr.Zero, IntPtr.Zero);
        }

        IPluginInstance? plugin = null;
        var audioLock = new object();
        var running = true;
        Thread? audio = null;

        void StartAudio()
        {
            audio = new Thread(() =>
            {
                var inL = new float[block];
                var inR = new float[block];
                var outL = new float[block];
                var outR = new float[block];
                var beat = 0;
                shm.Done = shm.Posted; // catch up with the engine
                while (Volatile.Read(ref running))
                {
                    var posted = shm.Posted;
                    if (posted == shm.Done)
                    {
                        signal.Wait(20);
                        if ((++beat & 15) == 0) shm.Beat++;
                        continue;
                    }
                    unsafe
                    {
                        new ReadOnlySpan<float>(shm.InL, block).CopyTo(inL);
                        new ReadOnlySpan<float>(shm.InR, block).CopyTo(inR);
                    }
                    try
                    {
                        lock (audioLock) plugin!.Process(inL, inR, outL, outR, block);
                    }
                    catch (Exception ex)
                    {
                        Send(new JsonObject { ["ev"] = "error", ["message"] = "The plugin stopped: " + ex.Message, ["fatal"] = true });
                        Volatile.Write(ref running, false);
                        onFatal();
                        return;
                    }
                    unsafe
                    {
                        outL.CopyTo(new Span<float>(shm.OutL, block));
                        outR.CopyTo(new Span<float>(shm.OutR, block));
                    }
                    shm.Done = posted;
                    shm.Beat++;
                }
            })
            { IsBackground = true, Name = "Plugin sound", Priority = ThreadPriority.Highest };
            audio.Start();
        }

        void Handle(JsonObject cmd)
        {
            switch ((string?)cmd["cmd"])
            {
                case "load":
                    try
                    {
                        var path = (string?)cmd["path"] ?? "";
                        var state = (string?)cmd["state"];
                        var ctrl = (string?)cmd["ctrl"];
                        plugin = path.StartsWith("builtin:", StringComparison.Ordinal)
                            ? new TestPlugin(path, state)
                            : new Vst3Plugin(path, Guid.Parse((string?)cmd["classId"] ?? ""), sampleRate, block, state, ctrl);
                        plugin.LatencyChanged += () =>
                        {
                            shm.Latency = plugin.Latency;
                            Send(new JsonObject { ["ev"] = "latency", ["latency"] = plugin.Latency });
                        };
                        plugin.Edited += () => Send(new JsonObject { ["ev"] = "edited" });
                        shm.Latency = plugin.Latency;
                        StartAudio();
                        Send(new JsonObject { ["ev"] = "ready", ["name"] = plugin.Name, ["latency"] = plugin.Latency, ["editor"] = plugin.HasEditor });
                    }
                    catch (Exception ex)
                    {
                        Send(new JsonObject { ["ev"] = "error", ["message"] = ex.Message, ["fatal"] = true });
                        running = false;
                    }
                    break;
                case "editor":
                    plugin?.ShowEditor((string?)cmd["title"] ?? "Plugin");
                    break;
                case "close-editor":
                    plugin?.CloseEditor();
                    break;
                case "state":
                    if (plugin != null)
                    {
                        (string? c, string? k) st;
                        lock (audioLock) st = plugin.GetState();
                        Send(new JsonObject { ["ev"] = "state", ["id"] = (int?)cmd["id"] ?? 0, ["c"] = st.c, ["k"] = st.k });
                    }
                    break;
                case "quit":
                    running = false;
                    break;
            }
        }

        // Commands come in on their own thread and are run on the main thread (plugin windows need it).
        var reader = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = input.ReadLine()) != null)
                {
                    if (JsonNode.Parse(line) is JsonObject o) OnMain(() => Handle(o));
                }
            }
            catch { }
            OnMain(() => running = false); // the engine went away
        })
        { IsBackground = true, Name = "Plugin commands" };
        reader.Start();

        // Keeps plugin requests (like a latency change) moving.
        var ticker = new Timer(_ => OnMain(() => (plugin as Vst3Plugin)?.Service(audioLock)), null, 100, 100);

        if (windows) WindowsLoop(mainQueue, () => Volatile.Read(ref running));
        else
            while (Volatile.Read(ref running))
                if (mainQueue.TryTake(out var a, 100)) a();

        ticker.Dispose();
        Volatile.Write(ref running, false);
        audio?.Join(500);
        try { plugin?.Dispose(); } catch { }
        Send(new JsonObject { ["ev"] = "bye" });
        return 0;
    }

    private static void WindowsLoop(BlockingCollection<Action> queue, Func<bool> running)
    {
        while (running())
        {
            if (Win32.GetMessageW(out var m, IntPtr.Zero, 0, 0) <= 0) break;
            if (m.Hwnd == IntPtr.Zero && m.Message == 0x0400)
            {
                while (queue.TryTake(out var a)) a();
                continue;
            }
            Win32.TranslateMessage(ref m);
            Win32.DispatchMessageW(ref m);
        }
    }
}
