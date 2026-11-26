using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Nexora.Audio.Engine;

namespace Nexora.Audio.Ipc;

public static class EngineNames
{
    /// <summary>Unique per data folder, so two copies of NEXORA on one PC (for testing) keep apart.</summary>
    public static string For(string dataRoot) =>
        "nexora-audio-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot.ToLowerInvariant())))[..12].ToLowerInvariant();
}

/// <summary>
/// The engine's side of the link: any number of NEX-Audio screens on this PC can connect over a
/// local pipe. Nothing leaves the PC. If every screen closes, the sound keeps running.
/// </summary>
public sealed class EngineServer : IDisposable
{
    private readonly AudioEngine _engine;
    private readonly string _pipe;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Conn> _conns = [];
    private readonly Timer _meters, _status;
    private readonly float[] _in = new float[64], _out = new float[64];
    private readonly bool[] _inClip = new bool[64], _outClip = new bool[64];
    private int _statusDirty = 1;

    public EngineServer(AudioEngine engine, string pipeName, Action<string>? log = null)
    {
        _engine = engine;
        _pipe = pipeName;
        _log = log ?? (_ => { });
        _engine.Changed += () => Interlocked.Exchange(ref _statusDirty, 1);
        _ = Task.Run(AcceptLoop);
        _meters = new Timer(_ => SendMeters(), null, 33, 33);
        _status = new Timer(_ => SendStatus(false), null, 100, 100);
    }

    /// <summary>Someone pressed Shut down engine.</summary>
    public event Action? QuitRequested;

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var c = new Conn(pipe);
                lock (_conns) _conns.Add(c);
                _ = Task.Run(() => ReadLoop(c));
                await c.SendAsync(new StatusMsg(_engine.Status));
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _log("Audio link: " + ex.Message);
                await Task.Delay(500);
            }
        }
    }

    private async Task ReadLoop(Conn c)
    {
        try
        {
            using var reader = new StreamReader(c.Pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (!_stop.IsCancellationRequested && await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                if (AudioMessage.FromLine(line) is not { } msg) continue;
                try
                {
                    await Handle(c, msg);
                }
                catch (Exception ex)
                {
                    await c.SendAsync(new NoticeMsg(ex.Message, true));
                }
                Interlocked.Exchange(ref _statusDirty, 1);
            }
        }
        catch (Exception) { }
        finally
        {
            lock (_conns) _conns.Remove(c);
            c.Dispose();
        }
    }

    private async Task Handle(Conn c, AudioMessage msg)
    {
        switch (msg)
        {
            case OpenDriverMsg o:
                await _engine.OpenAsync(o.Driver, o.BufferSize);
                break;
            case CloseDriverMsg:
                await _engine.CloseAsync();
                break;
            case SetPatchMsg p:
                _engine.SetPatch(p.Patch);
                break;
            case LatencyTestMsg l:
                _engine.StartLatencyTest(l.Output, l.Input);
                break;
            case DriverPanelMsg:
                await _engine.ShowDriverPanelAsync();
                break;
            case RefreshDriversMsg:
                _engine.RefreshDrivers();
                break;
            case ResetClipsMsg:
                _engine.ResetClips();
                break;
            case QuitMsg:
                await _engine.CloseAsync();
                QuitRequested?.Invoke();
                break;
        }
    }

    private void SendMeters()
    {
        Conn[] conns;
        lock (_conns) conns = [.. _conns];
        if (conns.Length == 0) return;
        if (!_engine.TakeMeters(_in, _out, _inClip, _outClip)) return;
        var st = _engine.Status;
        var msg = new MetersMsg(_in[..st.Inputs], _out[..st.Outputs], _inClip[..st.Inputs], _outClip[..st.Outputs]);
        foreach (var c in conns) _ = c.SendAsync(msg);
    }

    private int _quietTicks;

    private void SendStatus(bool force)
    {
        // Every change at once (at most ten a second), and once a second anyway.
        var dirty = Interlocked.Exchange(ref _statusDirty, 0) == 1;
        if (!dirty && !force && ++_quietTicks < 10) return;
        _quietTicks = 0;
        Conn[] conns;
        lock (_conns) conns = [.. _conns];
        if (conns.Length == 0) return;
        var msg = new StatusMsg(_engine.Status);
        foreach (var c in conns) _ = c.SendAsync(msg);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _meters.Dispose();
        _status.Dispose();
        lock (_conns)
        {
            foreach (var c in _conns) c.Dispose();
            _conns.Clear();
        }
    }

    private sealed class Conn(Stream pipe) : IDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        public Stream Pipe { get; } = pipe;

        public async Task SendAsync(AudioMessage msg)
        {
            // A slow screen skips meter frames rather than holding the engine up.
            if (msg is MetersMsg && _write.CurrentCount == 0) return;
            await _write.WaitAsync();
            try
            {
                var bytes = Encoding.UTF8.GetBytes(msg.ToLine() + "\n");
                await Pipe.WriteAsync(bytes);
                await Pipe.FlushAsync();
            }
            catch (Exception) { }
            finally
            {
                _write.Release();
            }
        }

        public void Dispose()
        {
            try { Pipe.Dispose(); }
            catch (Exception) { }
        }
    }
}

/// <summary>The NEX-Audio screen's side of the link. Reconnects by itself if the engine restarts.</summary>
public sealed class EngineClient : IDisposable
{
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private NamedPipeClientStream? _conn;

    public EngineClient(string pipeName)
    {
        _pipe = pipeName;
        _ = Task.Run(Loop);
    }

    public bool Connected { get; private set; }
    public event Action<EngineStatus>? Status;
    public event Action<MetersMsg>? Meters;
    public event Action<NoticeMsg>? Notice;
    public event Action<bool>? ConnectionChanged;

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(1000, _stop.Token);
                _conn = pipe;
                SetConnected(true);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, leaveOpen: true);
                while (!_stop.IsCancellationRequested && await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    switch (AudioMessage.FromLine(line))
                    {
                        case StatusMsg s: Status?.Invoke(s.Status); break;
                        case MetersMsg m: Meters?.Invoke(m); break;
                        case NoticeMsg n: Notice?.Invoke(n); break;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) { }
            finally
            {
                _conn = null;
                SetConnected(false);
                await pipe.DisposeAsync();
            }
            try { await Task.Delay(500, _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SetConnected(bool value)
    {
        if (Connected == value) return;
        Connected = value;
        ConnectionChanged?.Invoke(value);
    }

    public async Task<bool> SendAsync(AudioMessage msg)
    {
        var c = _conn;
        if (c is null) return false;
        await _write.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(msg.ToLine() + "\n");
            await c.WriteAsync(bytes);
            await c.FlushAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _conn?.Dispose(); }
        catch (Exception) { }
    }
}
