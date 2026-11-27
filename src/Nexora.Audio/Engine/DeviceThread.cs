using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Nexora.Audio.Engine;

/// <summary>
/// The one thread that opens, starts, stops and closes the sound driver. ASIO drivers expect
/// every call from the same thread, and on Windows that thread also passes on the driver's
/// window messages. Audio itself runs on the driver's own thread, not here.
/// </summary>
internal sealed class DeviceThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly Action _tick;
    private readonly TimeSpan _tickEvery;
    private volatile bool _done;

    public DeviceThread(Action tick, TimeSpan tickEvery)
    {
        _tick = tick;
        _tickEvery = tickEvery;
        _thread = new Thread(Loop) { IsBackground = true, Name = "NEX-Audio driver" };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task Run(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    private void Loop()
    {
        var nextTick = DateTime.UtcNow + _tickEvery;
        while (!_done)
        {
            if (OperatingSystem.IsWindows()) PumpMessages();
            if (_queue.TryTake(out var action, 15)) action();
            if (DateTime.UtcNow >= nextTick)
            {
                nextTick = DateTime.UtcNow + _tickEvery;
                try { _tick(); }
                catch (Exception) { }
            }
        }
    }

    public void Dispose()
    {
        _done = true;
        _thread.Join(2000);
    }

    private static void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);
}
