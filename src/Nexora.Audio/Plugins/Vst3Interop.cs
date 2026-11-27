using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Nexora.Audio.Plugins;

/// <summary>
/// The parts of the VST3 plugin standard NEX-Audio needs, written straight against its published binary
/// interface (COM-style objects with tables of functions). Plugins are only ever loaded inside a plugin host
/// process, never in the sound engine itself.
/// </summary>
internal static unsafe class Vst3
{
    public const int kResultOk = 0, kResultTrue = 0, kResultFalse = 1, kNoInterface = unchecked((int)0x80004002), kNotImplemented = unchecked((int)0x80004001), kInvalidArgument = unchecked((int)0x80070057);

    /// <summary>A VST3 interface id from its four 32-bit parts (Windows byte order, the same as a COM GUID).</summary>
    public static Guid Iid(uint l1, uint l2, uint l3, uint l4) =>
        new(l1, (ushort)(l2 >> 16), (ushort)(l2 & 0xFFFF), (byte)(l3 >> 24), (byte)(l3 >> 16), (byte)(l3 >> 8), (byte)l3, (byte)(l4 >> 24), (byte)(l4 >> 16), (byte)(l4 >> 8), (byte)l4);

    public static readonly Guid FUnknown = Iid(0x00000000, 0x00000000, 0xC0000000, 0x00000046);
    public static readonly Guid IPluginFactory = Iid(0x7A4D811C, 0x52114A1F, 0xAED9D2EE, 0x0B43BF9F);
    public static readonly Guid IPluginFactory2 = Iid(0x0007B650, 0xF24B4C0B, 0xA464EDB9, 0xF00B2ABB);
    public static readonly Guid IPluginBase = Iid(0x22888DDB, 0x156E45AE, 0x8358B348, 0x08190625);
    public static readonly Guid IComponent = Iid(0xE831FF31, 0xF2D54301, 0x928EBBEE, 0x25697802);
    public static readonly Guid IAudioProcessor = Iid(0x42043F99, 0xB7DA453C, 0xA569E79D, 0x9AAEC33D);
    public static readonly Guid IEditController = Iid(0xDCD7BBE3, 0x7742448D, 0xA874AACC, 0x979C759E);
    public static readonly Guid IPlugView = Iid(0x5BC32507, 0xD06049EA, 0xA6151B52, 0x2B755B29);
    public static readonly Guid IConnectionPoint = Iid(0x70A4156F, 0x6E6E4026, 0x989148BF, 0xAA60D8D1);
    public static readonly Guid IHostApplication = Iid(0x58E595CC, 0xDB2D4969, 0x8B6AAF8C, 0x36A664E5);
    public static readonly Guid IComponentHandler = Iid(0x93A0BEA3, 0x0BD045DB, 0x8E890B0C, 0xC1E46AC6);
    public static readonly Guid IBStream = Iid(0xC3BF6EA2, 0x30994752, 0x9B6BF990, 0x1EE33E9B);
    public static readonly Guid IParameterChanges = Iid(0xA4779663, 0x0BB64A56, 0xB44384A8, 0x466FEB9D);
    public static readonly Guid IParamValueQueue = Iid(0x01263A18, 0xED074F6F, 0x98C9D356, 0x4686F9BA);
    public static readonly Guid IPlugFrame = Iid(0x367FAF01, 0xAFA94693, 0x8D4DA2A0, 0xED0882A3);
    public static readonly Guid IMessage = Iid(0x936F033B, 0xC6C047DB, 0xBB0882F8, 0x13C1E613);
    public static readonly Guid IAttributeList = Iid(0x1E5F0AEB, 0xCC7F4533, 0xA2544011, 0x38AD5EE4);

    public const ulong SpeakerMono = 1UL << 19, SpeakerStereo = 0x3;
    public const int MediaAudio = 0, DirInput = 0, DirOutput = 1;
    public const int RestartLatencyChanged = 1 << 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PClassInfo
    {
        public Guid Cid;
        public int Cardinality;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Category;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PClassInfo2
    {
        public Guid Cid;
        public int Cardinality;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Category;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
        public uint ClassFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string SubCategories;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Vendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string SdkVersion;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PFactoryInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Vendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Url;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Email;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessSetup
    {
        public int ProcessMode;        // 0 realtime
        public int SymbolicSampleSize; // 0 = 32-bit float
        public int MaxSamplesPerBlock;
        public double SampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBusBuffers
    {
        public int NumChannels;
        public ulong SilenceFlags;
        public float** ChannelBuffers32;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessData
    {
        public int ProcessMode;
        public int SymbolicSampleSize;
        public int NumSamples;
        public int NumInputs;
        public int NumOutputs;
        public AudioBusBuffers* Inputs;
        public AudioBusBuffers* Outputs;
        public IntPtr InputParameterChanges;
        public IntPtr OutputParameterChanges;
        public IntPtr InputEvents;
        public IntPtr OutputEvents;
        public IntPtr ProcessContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ViewRect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    // ---- Calling plugin objects -------------------------------------------------------------

    public static IntPtr* Vt(IntPtr obj) => *(IntPtr**)obj;

    public static IntPtr Query(IntPtr obj, Guid iid)
    {
        if (obj == IntPtr.Zero) return IntPtr.Zero;
        IntPtr result;
        var r = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Vt(obj)[0])(obj, &iid, &result);
        return r == kResultOk ? result : IntPtr.Zero;
    }

    public static uint AddRef(IntPtr obj) => obj == IntPtr.Zero ? 0 : ((delegate* unmanaged<IntPtr, uint>)Vt(obj)[1])(obj);
    public static uint Release(IntPtr obj) => obj == IntPtr.Zero ? 0 : ((delegate* unmanaged<IntPtr, uint>)Vt(obj)[2])(obj);

    // ---- Host objects: small COM objects whose functions are C# methods ------------------------

    /// <summary>The memory behind one host object: its function table, a handle to the C# object and a reference count.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Native
    {
        public IntPtr Vtbl;
        public IntPtr Handle;
        public int Refs;
    }

    private static readonly ConcurrentDictionary<IntPtr, byte> Live = new();

    public static IntPtr Wrap(HostObject target, IntPtr* vtbl)
    {
        var n = (Native*)NativeMemory.AllocZeroed((nuint)sizeof(Native));
        n->Vtbl = (IntPtr)vtbl;
        n->Handle = GCHandle.ToIntPtr(GCHandle.Alloc(target));
        n->Refs = 1;
        Live[(IntPtr)n] = 0;
        return (IntPtr)n;
    }

    public static T? Self<T>(IntPtr self) where T : class
    {
        if (self == IntPtr.Zero) return null;
        var n = (Native*)self;
        return GCHandle.FromIntPtr(n->Handle).Target as T;
    }

    public static IntPtr* Table(int count)
    {
        var t = (IntPtr*)NativeMemory.AllocZeroed((nuint)(count * sizeof(IntPtr)));
        t[0] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
        t[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&AddRefHost;
        t[2] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&ReleaseHost;
        return t;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(IntPtr self, Guid* iid, IntPtr* obj)
    {
        var o = Self<HostObject>(self);
        if (o is null || obj == null) return kInvalidArgument;
        if (*iid == FUnknown || o.Implements(*iid))
        {
            ((Native*)self)->Refs++;
            *obj = self;
            return kResultOk;
        }
        *obj = IntPtr.Zero;
        return kNoInterface;
    }

    [UnmanagedCallersOnly]
    private static uint AddRefHost(IntPtr self) => (uint)Interlocked.Increment(ref ((Native*)self)->Refs);

    [UnmanagedCallersOnly]
    private static uint ReleaseHost(IntPtr self)
    {
        // Host objects live as long as the plugin host process; the count is kept for the plugin's sake.
        var r = Interlocked.Decrement(ref ((Native*)self)->Refs);
        return (uint)Math.Max(r, 0);
    }

    public static string Utf16(char* s, int max)
    {
        var len = 0;
        while (len < max && s[len] != 0) len++;
        return new string(s, 0, len);
    }

    public static void WriteUtf16(char* dst, int max, string text)
    {
        var n = Math.Min(text.Length, max - 1);
        for (var i = 0; i < n; i++) dst[i] = text[i];
        dst[n] = '\0';
    }
}

/// <summary>A C# object the plugin sees as a VST3 host interface.</summary>
internal abstract class HostObject
{
    public IntPtr Ptr { get; protected set; }
    public abstract bool Implements(Guid iid);
}

/// <summary>Tells the plugin who is hosting it, and makes messages for it.</summary>
internal sealed unsafe class HostApplication : HostObject
{
    private static readonly IntPtr* VTable = Build();

    public HostApplication() => Ptr = Vst3.Wrap(this, VTable);
    public override bool Implements(Guid iid) => iid == Vst3.IHostApplication;

    private static IntPtr* Build()
    {
        var t = Vst3.Table(5);
        t[3] = (IntPtr)(delegate* unmanaged<IntPtr, char*, int>)&GetName;
        t[4] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)&CreateInstance;
        return t;
    }

    [UnmanagedCallersOnly]
    private static int GetName(IntPtr self, char* name)
    {
        Vst3.WriteUtf16(name, 128, "NEXORA NEX-Audio");
        return Vst3.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int CreateInstance(IntPtr self, Guid* cid, Guid* iid, IntPtr* obj)
    {
        if (*cid == Vst3.IMessage || *iid == Vst3.IMessage)
        {
            *obj = new HostMessage().Ptr;
            return Vst3.kResultOk;
        }
        if (*cid == Vst3.IAttributeList || *iid == Vst3.IAttributeList)
        {
            *obj = new AttributeList().Ptr;
            return Vst3.kResultOk;
        }
        *obj = IntPtr.Zero;
        return Vst3.kNotImplemented;
    }
}

internal sealed unsafe class HostMessage : HostObject
{
    private static readonly IntPtr* VTable = Build();
    private IntPtr _id;
    private readonly AttributeList _attrs = new();

    public HostMessage() => Ptr = Vst3.Wrap(this, VTable);
    public override bool Implements(Guid iid) => iid == Vst3.IMessage;

    private static IntPtr* Build()
    {
        var t = Vst3.Table(6);
        t[3] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr>)&GetId;
        t[4] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&SetId;
        t[5] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr>)&GetAttributes;
        return t;
    }

    [UnmanagedCallersOnly] private static IntPtr GetId(IntPtr self) => Vst3.Self<HostMessage>(self)?._id ?? IntPtr.Zero;

    [UnmanagedCallersOnly]
    private static void SetId(IntPtr self, IntPtr id)
    {
        if (Vst3.Self<HostMessage>(self) is not { } m) return;
        var s = Marshal.PtrToStringAnsi(id) ?? "";
        if (m._id != IntPtr.Zero) Marshal.FreeHGlobal(m._id);
        m._id = Marshal.StringToHGlobalAnsi(s);
    }

    [UnmanagedCallersOnly] private static IntPtr GetAttributes(IntPtr self) => Vst3.Self<HostMessage>(self)?._attrs.Ptr ?? IntPtr.Zero;
}

internal sealed unsafe class AttributeList : HostObject
{
    private static readonly IntPtr* VTable = Build();
    private readonly Dictionary<string, object> _v = [];
    private readonly List<IntPtr> _blobs = [];

    public AttributeList() => Ptr = Vst3.Wrap(this, VTable);
    public override bool Implements(Guid iid) => iid == Vst3.IAttributeList;

    private static IntPtr* Build()
    {
        var t = Vst3.Table(11);
        t[3] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, long, int>)&SetInt;
        t[4] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, long*, int>)&GetInt;
        t[5] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, double, int>)&SetFloat;
        t[6] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, double*, int>)&GetFloat;
        t[7] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, char*, int>)&SetString;
        t[8] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, char*, uint, int>)&GetString;
        t[9] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte*, uint, int>)&SetBinary;
        t[10] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte**, uint*, int>)&GetBinary;
        return t;
    }

    private static string Key(IntPtr id) => Marshal.PtrToStringAnsi(id) ?? "";

    [UnmanagedCallersOnly]
    private static int SetInt(IntPtr self, IntPtr id, long v)
    {
        if (Vst3.Self<AttributeList>(self) is not { } a) return Vst3.kInvalidArgument;
        lock (a._v) a._v[Key(id)] = v;
        return Vst3.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int GetInt(IntPtr self, IntPtr id, long* v)
    {
        if (Vst3.Self<AttributeList>(self) is not { } a) return Vst3.kInvalidArgument;
        lock (a._v)
            if (a._v.TryGetValue(Key(id), out var o) && o is long l) { *v = l; return Vst3.kResultOk; }
        return Vst3.kResultFalse;
    }

    [UnmanagedCallersOnly]
    private static int SetFloat(IntPtr self, IntPtr id, double v)
    {
        if (Vst3.Self<AttributeList>(self) is not { } a) return Vst3.kInvalidArgument;
        lock (a._v) a._v[Key(id)] = v;
        return Vst3.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int GetFloat(IntPtr self, IntPtr id, double* v)
    {
        if (Vst3.Self<AttributeList>(self) is not { } a) return Vst3.kInvalidArgument;
        lock (a._v)
            if (a._v.TryGetValue(Key(id), out var o) && o is double d) { *v = d; return Vst3.kResultOk; }
        return Vst3.kResultFalse;
    }

    [UnmanagedCallersOnly]
    private static int SetString(IntPtr self, IntPtr id, char* s)
    {
        if (Vst3.Self<AttributeList>(self) is not { } a) return Vst3.kInvalidArgument;
        lock (a._v) a._v[Key(id)] = Vst3.Utf16(s, 1 << 20);
        return Vst3.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int GetString(IntPtr self, IntPtr id, char* dst, uint sizeBytes)
    {
        if (Vst3.Self<AttributeList>(self) is not { } a) return Vst3.kInvalidArgument;
        lock (a._v)
            if (a._v.TryGetValue(Key(id), out var o) && o is string s)
            {
                Vst3.WriteUtf16(dst, (int)(sizeBytes / 2), s);
                return Vst3.kResultOk;
            }
        return Vst3.kResultFalse;
    }

    [UnmanagedCallersOnly]
    private static int SetBinary(IntPtr self, IntPtr id, byte* data, uint size)
    {
        if (Vst3.Self<AttributeList>(self) is not { } a) return Vst3.kInvalidArgument;
        var copy = new byte[size];
        new ReadOnlySpan<byte>(data, (int)size).CopyTo(copy);
        lock (a._v) a._v[Key(id)] = copy;
        return Vst3.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int GetBinary(IntPtr self, IntPtr id, byte** data, uint* size)
    {
        if (Vst3.Self<AttributeList>(self) is not { } a) return Vst3.kInvalidArgument;
        lock (a._v)
        {
            if (!a._v.TryGetValue(Key(id), out var o) || o is not byte[] b) return Vst3.kResultFalse;
            // The plugin may keep this pointer while the message lives: a native copy that stays put.
            var p = Marshal.AllocHGlobal(Math.Max(1, b.Length));
            Marshal.Copy(b, 0, p, b.Length);
            a._blobs.Add(p);
            *data = (byte*)p;
            *size = (uint)b.Length;
            return Vst3.kResultOk;
        }
    }
}

/// <summary>A plugin's saved settings travel through this (the VST3 IBStream), backed by a byte array.</summary>
internal sealed unsafe class HostStream : HostObject
{
    private static readonly IntPtr* VTable = Build();
    private readonly MemoryStream _ms;

    public HostStream(byte[]? data = null)
    {
        _ms = data is null ? new MemoryStream() : new MemoryStream(data, 0, data.Length, writable: false, publiclyVisible: true);
        Ptr = Vst3.Wrap(this, VTable);
    }

    public override bool Implements(Guid iid) => iid == Vst3.IBStream;
    public byte[] ToArray() => _ms.ToArray();
    public void Rewind() => _ms.Position = 0;

    private static IntPtr* Build()
    {
        var t = Vst3.Table(7);
        t[3] = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, int*, int>)&Read;
        t[4] = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, int*, int>)&Write;
        t[5] = (IntPtr)(delegate* unmanaged<IntPtr, long, int, long*, int>)&Seek;
        t[6] = (IntPtr)(delegate* unmanaged<IntPtr, long*, int>)&Tell;
        return t;
    }

    [UnmanagedCallersOnly]
    private static int Read(IntPtr self, byte* buf, int n, int* read)
    {
        if (Vst3.Self<HostStream>(self) is not { } s) return Vst3.kInvalidArgument;
        var got = s._ms.Read(new Span<byte>(buf, Math.Max(0, n)));
        if (read != null) *read = got;
        return Vst3.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int Write(IntPtr self, byte* buf, int n, int* written)
    {
        if (Vst3.Self<HostStream>(self) is not { } s || !s._ms.CanWrite) return Vst3.kResultFalse;
        s._ms.Write(new ReadOnlySpan<byte>(buf, Math.Max(0, n)));
        if (written != null) *written = n;
        return Vst3.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int Seek(IntPtr self, long pos, int mode, long* result)
    {
        if (Vst3.Self<HostStream>(self) is not { } s) return Vst3.kInvalidArgument;
        var origin = mode switch { 1 => SeekOrigin.Current, 2 => SeekOrigin.End, _ => SeekOrigin.Begin };
        try
        {
            var r = s._ms.Seek(pos, origin);
            if (result != null) *result = r;
            return Vst3.kResultOk;
        }
        catch (Exception) { return Vst3.kResultFalse; }
    }

    [UnmanagedCallersOnly]
    private static int Tell(IntPtr self, long* pos)
    {
        if (Vst3.Self<HostStream>(self) is not { } s) return Vst3.kInvalidArgument;
        if (pos != null) *pos = s._ms.Position;
        return Vst3.kResultOk;
    }
}

/// <summary>Hears about edits made in the plugin's own window and passes them to the sound processing.</summary>
internal sealed unsafe class ComponentHandler : HostObject
{
    private static readonly IntPtr* VTable = Build();
    private readonly ConcurrentQueue<(uint Id, double Value)> _edits = new();

    public ComponentHandler() => Ptr = Vst3.Wrap(this, VTable);
    public override bool Implements(Guid iid) => iid == Vst3.IComponentHandler;

    public event Action? Edited;
    public event Action<int>? Restart;

    public bool TryTake(out (uint Id, double Value) edit) => _edits.TryDequeue(out edit);

    private static IntPtr* Build()
    {
        var t = Vst3.Table(7);
        t[3] = (IntPtr)(delegate* unmanaged<IntPtr, uint, int>)&BeginEdit;
        t[4] = (IntPtr)(delegate* unmanaged<IntPtr, uint, double, int>)&PerformEdit;
        t[5] = (IntPtr)(delegate* unmanaged<IntPtr, uint, int>)&EndEdit;
        t[6] = (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&RestartComponent;
        return t;
    }

    [UnmanagedCallersOnly] private static int BeginEdit(IntPtr self, uint id) => Vst3.kResultOk;

    [UnmanagedCallersOnly]
    private static int PerformEdit(IntPtr self, uint id, double value)
    {
        if (Vst3.Self<ComponentHandler>(self) is not { } h) return Vst3.kInvalidArgument;
        h._edits.Enqueue((id, value));
        h.Edited?.Invoke();
        return Vst3.kResultOk;
    }

    [UnmanagedCallersOnly] private static int EndEdit(IntPtr self, uint id) => Vst3.kResultOk;

    [UnmanagedCallersOnly]
    private static int RestartComponent(IntPtr self, int flags)
    {
        Vst3.Self<ComponentHandler>(self)?.Restart?.Invoke(flags);
        return Vst3.kResultOk;
    }
}

/// <summary>
/// Parameter changes for one processing block (edits from the plugin window). Fixed size, so the audio
/// thread never allocates: up to 64 parameters with one point each per block.
/// </summary>
internal sealed unsafe class ParameterChanges : HostObject
{
    private static readonly IntPtr* VTable = Build();
    private readonly ValueQueue[] _queues;
    private int _count;

    public ParameterChanges()
    {
        _queues = Enumerable.Range(0, 64).Select(_ => new ValueQueue()).ToArray();
        Ptr = Vst3.Wrap(this, VTable);
    }

    public override bool Implements(Guid iid) => iid == Vst3.IParameterChanges;

    public void Clear() => _count = 0;

    public void Add(uint id, double value)
    {
        for (var i = 0; i < _count; i++)
            if (_queues[i].Id == id)
            {
                _queues[i].Value = value;
                return;
            }
        if (_count >= _queues.Length) return;
        _queues[_count].Id = id;
        _queues[_count].Value = value;
        _count++;
    }

    public int Count => _count;

    private static IntPtr* Build()
    {
        var t = Vst3.Table(6);
        t[3] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&GetCount;
        t[4] = (IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr>)&GetData;
        t[5] = (IntPtr)(delegate* unmanaged<IntPtr, uint*, int*, IntPtr>)&AddData;
        return t;
    }

    [UnmanagedCallersOnly] private static int GetCount(IntPtr self) => Vst3.Self<ParameterChanges>(self)?._count ?? 0;

    [UnmanagedCallersOnly]
    private static IntPtr GetData(IntPtr self, int index)
    {
        var p = Vst3.Self<ParameterChanges>(self);
        return p is null || index < 0 || index >= p._count ? IntPtr.Zero : p._queues[index].Ptr;
    }

    [UnmanagedCallersOnly]
    private static IntPtr AddData(IntPtr self, uint* id, int* index)
    {
        // Only used for output changes, which NEX-Audio does not ask for.
        if (index != null) *index = 0;
        return IntPtr.Zero;
    }

    internal sealed class ValueQueue : HostObject
    {
        private static readonly IntPtr* QTable = BuildQueue();
        public uint Id;
        public double Value;

        public ValueQueue() => Ptr = Vst3.Wrap(this, QTable);
        public override bool Implements(Guid iid) => iid == Vst3.IParamValueQueue;

        private static IntPtr* BuildQueue()
        {
            var t = Vst3.Table(7);
            t[3] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&GetId;
            t[4] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&GetPointCount;
            t[5] = (IntPtr)(delegate* unmanaged<IntPtr, int, int*, double*, int>)&GetPoint;
            t[6] = (IntPtr)(delegate* unmanaged<IntPtr, int, double, int*, int>)&AddPoint;
            return t;
        }

        [UnmanagedCallersOnly] private static uint GetId(IntPtr self) => Vst3.Self<ValueQueue>(self)?.Id ?? 0;
        [UnmanagedCallersOnly] private static int GetPointCount(IntPtr self) => 1;

        [UnmanagedCallersOnly]
        private static int GetPoint(IntPtr self, int index, int* offset, double* value)
        {
            if (Vst3.Self<ValueQueue>(self) is not { } q || index != 0) return Vst3.kResultFalse;
            *offset = 0;
            *value = q.Value;
            return Vst3.kResultOk;
        }

        [UnmanagedCallersOnly] private static int AddPoint(IntPtr self, int offset, double value, int* index) => Vst3.kResultFalse;
    }
}

/// <summary>Lets the plugin ask for its window to be resized.</summary>
internal sealed unsafe class PlugFrame : HostObject
{
    private static readonly IntPtr* VTable = Build();

    public PlugFrame() => Ptr = Vst3.Wrap(this, VTable);
    public override bool Implements(Guid iid) => iid == Vst3.IPlugFrame;

    public Func<IntPtr, Vst3.ViewRect, bool>? OnResize { get; set; }

    private static IntPtr* Build()
    {
        var t = Vst3.Table(4);
        t[3] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, Vst3.ViewRect*, int>)&ResizeView;
        return t;
    }

    [UnmanagedCallersOnly]
    private static int ResizeView(IntPtr self, IntPtr view, Vst3.ViewRect* rect)
    {
        var f = Vst3.Self<PlugFrame>(self);
        if (f?.OnResize is null || rect == null) return Vst3.kResultFalse;
        return f.OnResize(view, *rect) ? Vst3.kResultOk : Vst3.kResultFalse;
    }
}
