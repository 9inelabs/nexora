using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Nexora.Core.Model;
using Nexora.Core.Protocol;

namespace Nexora.Core.Net;

public enum ConnectionStatus
{
    /// <summary>No Live Output PC found yet.</summary>
    Searching,
    Connecting,
    Connected,
    /// <summary>The Live Output PC refused us (for example a wrong pairing code).</summary>
    Rejected,
    /// <summary>Was connected, link lost, retrying.</summary>
    Reconnecting,
}

/// <summary>
/// A terminal's (or the output engine's) link to the Live Output PC.
/// Reconnects by itself every second or two and signs back in without asking.
/// Events are raised on a background thread.
/// </summary>
public sealed class NexoraClient : IAsyncDisposable
{
    private readonly Func<Uri?> _url;
    private readonly Func<Hello> _hello;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _loop;
    private ClientWebSocket? _ws;
    private TaskCompletionSource<LoginResult>? _pendingLogin;
    private IReadOnlyList<SectionKind> _sections = [];
    private bool _everConnected;

    public static TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(1500);
    public static TimeSpan SilenceTimeout { get; set; } = TimeSpan.FromSeconds(4);

    public NexoraClient(Func<Uri?> url, Func<Hello> hello)
    {
        _url = url;
        _hello = hello;
    }

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Searching;
    public string? RejectReason { get; private set; }
    public Uri? CurrentUrl { get; private set; }
    public Welcome? Welcome { get; private set; }
    public PublicInfo? PublicInfo { get; private set; }
    public LiveSnapshot Snapshot { get; private set; } = LiveSnapshot.Empty;
    public UserInfo? User { get; private set; }
    public string? Token { get; private set; }
    public int LatencyMs { get; private set; } = -1;
    public IReadOnlyList<SectionKind> OpenedSections => _sections;

    public event Action? StatusChanged;
    public event Action? PublicInfoChanged;
    public event Action? SnapshotChanged;
    public event Action? UserChanged;
    public event Action<Notice>? NoticeReceived;
    public event Action? LatencyChanged;
    public event Action? LibraryChanged;

    /// <summary>Shared Scripture themes and the list of library files on the Live Output PC.</summary>
    public LibraryInfo? Library { get; private set; }

    public void Start() => _loop ??= Task.Run(RunAsync);

    // ---------- Commands ----------

    public async Task<LoginResult> LoginAsync(string userId, string pin, TimeSpan? timeout = null)
    {
        if (Status != ConnectionStatus.Connected)
            return new LoginResult(false, "Not connected to the Live Output PC.", null, null);
        var tcs = new TaskCompletionSource<LoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingLogin = tcs;
        await SendAsync(new Login(userId, pin));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(6)));
        return done == tcs.Task ? tcs.Task.Result : new LoginResult(false, "The Live Output PC did not answer. Try again.", null, null);
    }

    public async Task LogoutAsync()
    {
        _sections = [];
        Token = null;
        User = null;
        UserChanged?.Invoke();
        await SendAsync(new Logout());
    }

    public Task OpenSectionsAsync(IReadOnlyList<SectionKind> sections)
    {
        _sections = sections.ToList();
        return SendAsync(new OpenSections(_sections));
    }

    public Task SendContentAsync(SectionKind section, SlideContent content) => SendAsync(new SendContent(section, content));
    public Task ClearContentAsync(SectionKind section) => SendAsync(new ClearContent(section));
    public Task RouteAsync(string sourceKey, string screenId, RouteAction action) => SendAsync(new RouteCommand(sourceKey, screenId, action));
    public Task ScreenAsync(string screenId, ScreenAction action) => SendAsync(new ScreenCommand(screenId, action));

    /// <summary>Sends a message. Returns false if the link is down (the caller's state is kept and re-synced on reconnect).</summary>
    public async Task<bool> SendAsync(Message message)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return false;
        var bytes = Encoding.UTF8.GetBytes(Wire.Serialize(message));
        await _sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Drops the current link at once and reconnects (for example after the address changed).</summary>
    public void Reconnect()
    {
        try { _ws?.Abort(); } catch { }
    }

    // ---------- Library files over HTTP ----------

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private Uri? HttpBase()
    {
        var u = CurrentUrl;
        if (u is null) return null;
        return new UriBuilder(u) { Scheme = "http", Path = Wire.LibraryPath + "/" }.Uri;
    }

    /// <summary>Fetches a library file ("bibles" or "media") from the Live Output PC into a local file.</summary>
    public async Task<bool> DownloadAsync(string kind, string name, string targetPath, CancellationToken ct = default)
    {
        if (HttpBase() is not { } b) return false;
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(b, $"{kind}/{Uri.EscapeDataString(name)}"));
        req.Headers.Add(Wire.CodeHeader, _hello().PairingCode);
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        var tmp = targetPath + ".part";
        await using (var fs = File.Create(tmp)) await res.Content.CopyToAsync(fs, ct);
        File.Move(tmp, targetPath, overwrite: true);
        return true;
    }

    /// <summary>Sends a file to the Live Output PC's library. Returns the name it is stored under.</summary>
    public async Task<string> UploadAsync(string kind, string name, string sourcePath, CancellationToken ct = default)
    {
        if (HttpBase() is not { } b || Token is null) throw new InvalidOperationException("Not connected to the Live Output PC.");
        await using var fs = File.OpenRead(sourcePath);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(b, $"{kind}/{Uri.EscapeDataString(name)}"))
        {
            Content = new StreamContent(fs),
        };
        req.Headers.Add(Wire.TokenHeader, Token);
        using var res = await Http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(body.Length > 0 ? body : $"The Live Output PC refused the file ({(int)res.StatusCode}).");
        return body.Trim();
    }

    // ---------- Connection loop ----------

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var url = SafeUrl();
            if (url is null)
            {
                SetStatus(_everConnected ? ConnectionStatus.Reconnecting : ConnectionStatus.Searching);
                await DelayAsync(TimeSpan.FromMilliseconds(500));
                continue;
            }

            CurrentUrl = url;
            if (Status != ConnectionStatus.Rejected)
                SetStatus(_everConnected ? ConnectionStatus.Reconnecting : ConnectionStatus.Connecting);

            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(3));
                    await ws.ConnectAsync(url, connectCts.Token);
                }
                _ws = ws;
                await SendAsync(_hello());
                await ReceiveLoopAsync(ws);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Connection refused, timed out or dropped: retry below.
            }
            finally
            {
                _ws = null;
                LatencyMs = -1;
                LatencyChanged?.Invoke();
            }

            if (Status == ConnectionStatus.Connected)
                SetStatus(ConnectionStatus.Reconnecting);
            await DelayAsync(Status == ConnectionStatus.Rejected ? TimeSpan.FromSeconds(3) : RetryDelay);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws)
    {
        var lastHeard = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        // Ping every second; if the Live Output PC is silent for a few seconds (pulled cable), drop and retry.
        var pinger = Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await SendAsync(new Ping(Stopwatch.GetTimestamp()));
                try { await Task.Delay(1000, linked.Token); } catch (OperationCanceledException) { break; }
                if (lastHeard.Elapsed > SilenceTimeout)
                {
                    try { ws.Abort(); } catch { }
                    break;
                }
            }
        });

        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                lastHeard.Restart();
                if (Wire.Deserialize(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)) is { } msg)
                    await HandleAsync(msg);
            }
        }
        finally
        {
            linked.Cancel();
            try { await pinger; } catch { }
        }
    }

    private async Task HandleAsync(Message msg)
    {
        switch (msg)
        {
            case Welcome w:
                Welcome = w;
                RejectReason = null;
                _everConnected = true;
                // Sign back in and reopen the sections after a reconnect, before telling anyone we are back.
                if (Token is not null) await SendAsync(new Resume(Token));
                else SetStatus(ConnectionStatus.Connected);
                break;

            case Rejected rj:
                RejectReason = rj.Reason;
                SetStatus(ConnectionStatus.Rejected);
                try { _ws?.Abort(); } catch { }
                break;

            case PublicInfo pi:
                PublicInfo = pi;
                PublicInfoChanged?.Invoke();
                break;

            case LoginResult lr:
                if (lr.Ok)
                {
                    User = lr.User;
                    Token = lr.Token;
                    if (_sections.Count > 0) await SendAsync(new OpenSections(_sections));
                }
                else if (_pendingLogin is null)
                {
                    // A resume after reconnect failed (for example the Live Output PC restarted): sign in again.
                    User = null;
                    Token = null;
                    _sections = [];
                }
                SetStatus(ConnectionStatus.Connected);
                UserChanged?.Invoke();
                _pendingLogin?.TrySetResult(lr);
                _pendingLogin = null;
                break;

            case SnapshotMessage s:
                Snapshot = s.Snapshot;
                SnapshotChanged?.Invoke();
                break;

            case Notice n:
                NoticeReceived?.Invoke(n);
                break;

            case LibraryInfo li:
                Library = li;
                LibraryChanged?.Invoke();
                break;

            case Pong p:
                LatencyMs = (int)Math.Max(0, Stopwatch.GetElapsedTime(p.SentTicks).TotalMilliseconds);
                LatencyChanged?.Invoke();
                break;
        }
    }

    private Uri? SafeUrl()
    {
        try { return _url(); }
        catch { return null; }
    }

    private void SetStatus(ConnectionStatus s)
    {
        if (Status == s) return;
        Status = s;
        StatusChanged?.Invoke();
    }

    private async Task DelayAsync(TimeSpan t)
    {
        try { await Task.Delay(t, _cts.Token); } catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _ws?.Abort(); } catch { }
        if (_loop is not null)
        {
            try { await _loop; } catch { }
        }
        _cts.Dispose();
    }
}
