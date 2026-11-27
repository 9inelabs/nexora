using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Nexora.Audio.Surface;

/// <summary>A MIDI port a fader box sends on.</summary>
public interface IMidiIn : IDisposable
{
    /// <summary>Short messages (status and data bytes). Raised on the driver's own thread: copy and get out.</summary>
    event Action<byte[]>? Received;
}

/// <summary>A MIDI port to send to a fader box on (motor faders, lights, the scribble strip).</summary>
public interface IMidiOut : IDisposable
{
    void Send(byte[] msg);
}

/// <summary>Opens MIDI ports by name. On Windows this is the system's own MIDI (winmm); elsewhere there are none.</summary>
public interface IMidiPorts
{
    IReadOnlyList<string> Inputs();
    IReadOnlyList<string> Outputs();
    IMidiIn? OpenIn(string name);
    IMidiOut? OpenOut(string name);
}

/// <summary>Windows MIDI (winmm): every USB fader box shows up here with its own name.</summary>
public sealed class WinMidi : IMidiPorts
{
    public IReadOnlyList<string> Inputs()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var list = new List<string>();
        try
        {
            var n = midiInGetNumDevs();
            for (uint i = 0; i < n; i++)
            {
                var caps = new MidiInCaps();
                if (midiInGetDevCapsW((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<MidiInCaps>()) == 0) list.Add(caps.Name);
            }
        }
        catch (Exception) { }
        return list;
    }

    public IReadOnlyList<string> Outputs()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var list = new List<string>();
        try
        {
            var n = midiOutGetNumDevs();
            for (uint i = 0; i < n; i++)
            {
                var caps = new MidiOutCaps();
                if (midiOutGetDevCapsW((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<MidiOutCaps>()) == 0) list.Add(caps.Name);
            }
        }
        catch (Exception) { }
        return list;
    }

    public IMidiIn? OpenIn(string name)
    {
        var i = Inputs().ToList().IndexOf(name);
        if (i < 0) return null;
        var port = new InPort();
        return port.Open((uint)i) ? port : null;
    }

    public IMidiOut? OpenOut(string name)
    {
        var i = Outputs().ToList().IndexOf(name);
        if (i < 0) return null;
        return midiOutOpen(out var h, (uint)i, IntPtr.Zero, IntPtr.Zero, 0) == 0 ? new OutPort(h) : null;
    }

    private sealed class InPort : IMidiIn
    {
        private IntPtr _h;
        // Kept in a field: the driver calls it, and the garbage collector must not take it away.
        private readonly MidiInProc _proc;
        public event Action<byte[]>? Received;

        public InPort() => _proc = OnMidi;

        public bool Open(uint id)
        {
            if (midiInOpen(out _h, id, _proc, IntPtr.Zero, CallbackFunction) != 0) return false;
            midiInStart(_h);
            return true;
        }

        private void OnMidi(IntPtr h, uint msg, IntPtr inst, IntPtr p1, IntPtr p2)
        {
            if (msg != MimData) return;
            var v = (uint)p1.ToInt64();
            var status = (byte)(v & 0xFF);
            var len = (status & 0xF0) is 0xC0 or 0xD0 ? 2 : 3;
            var bytes = len == 2 ? new[] { status, (byte)((v >> 8) & 0x7F) } : new[] { status, (byte)((v >> 8) & 0x7F), (byte)((v >> 16) & 0x7F) };
            try { Received?.Invoke(bytes); } catch { }
        }

        public void Dispose()
        {
            if (_h == IntPtr.Zero) return;
            try
            {
                midiInStop(_h);
                midiInReset(_h);
                midiInClose(_h);
            }
            catch { }
            _h = IntPtr.Zero;
        }
    }

    private sealed class OutPort : IMidiOut
    {
        private IntPtr _h;
        private readonly BlockingCollection<byte[]> _queue = new(512);
        private readonly Thread _thread;

        public OutPort(IntPtr h)
        {
            _h = h;
            // Sending waits on the driver (system exclusive text especially): never on the caller's thread.
            _thread = new Thread(Run) { IsBackground = true, Name = "NEX-Audio MIDI out" };
            _thread.Start();
        }

        public void Send(byte[] msg)
        {
            if (!_queue.IsAddingCompleted) _queue.TryAdd(msg);
        }

        private void Run()
        {
            try
            {
                foreach (var m in _queue.GetConsumingEnumerable())
                {
                    if (_h == IntPtr.Zero) return;
                    if (m.Length > 0 && m[0] == 0xF0) Long(m);
                    else if (m.Length > 0) midiOutShortMsg(_h, m[0] | (m.Length > 1 ? (uint)m[1] << 8 : 0) | (m.Length > 2 ? (uint)m[2] << 16 : 0));
                }
            }
            catch (Exception) { }
        }

        private void Long(byte[] m)
        {
            var data = Marshal.AllocHGlobal(m.Length);
            var hdr = Marshal.AllocHGlobal(Marshal.SizeOf<MidiHdr>());
            try
            {
                Marshal.Copy(m, 0, data, m.Length);
                Marshal.StructureToPtr(new MidiHdr { Data = data, BufferLength = (uint)m.Length, BytesRecorded = (uint)m.Length }, hdr, false);
                var size = (uint)Marshal.SizeOf<MidiHdr>();
                if (midiOutPrepareHeader(_h, hdr, size) != 0) return;
                midiOutLongMsg(_h, hdr, size);
                for (var i = 0; i < 50 && (Marshal.PtrToStructure<MidiHdr>(hdr).Flags & 1) == 0; i++) Thread.Sleep(1);
                midiOutUnprepareHeader(_h, hdr, size);
            }
            finally
            {
                Marshal.FreeHGlobal(hdr);
                Marshal.FreeHGlobal(data);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(500);
            if (_h == IntPtr.Zero) return;
            try
            {
                midiOutReset(_h);
                midiOutClose(_h);
            }
            catch { }
            _h = IntPtr.Zero;
        }
    }

    private const uint CallbackFunction = 0x30000, MimData = 0x3C3;

    private delegate void MidiInProc(IntPtr h, uint msg, IntPtr inst, IntPtr p1, IntPtr p2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCaps
    {
        public ushort Mid, Pid;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Support;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiOutCaps
    {
        public ushort Mid, Pid;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public ushort Technology, Voices, Notes, ChannelMask;
        public uint Support;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MidiHdr
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public IntPtr Next, Reserved;
        public uint Offset;
        public IntPtr R0, R1, R2, R3;
    }

    [DllImport("winmm.dll")] private static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiInGetDevCapsW(UIntPtr id, ref MidiInCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint midiInOpen(out IntPtr h, uint id, MidiInProc cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiInStart(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiInStop(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiInReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiInClose(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiOutGetDevCapsW(UIntPtr id, ref MidiOutCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint midiOutOpen(out IntPtr h, uint id, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiOutShortMsg(IntPtr h, uint msg);
    [DllImport("winmm.dll")] private static extern uint midiOutLongMsg(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint midiOutPrepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint midiOutUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint midiOutReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiOutClose(IntPtr h);
}
