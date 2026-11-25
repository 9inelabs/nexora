using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexora.Core.Config;
using Nexora.Core.Model;
using Nexora.Core.Net;
using Nexora.Core.Protocol;
using Nexora.Server;

namespace Nexora.App.Services;

/// <summary>
/// Everything this PC runs: on the Live Output PC the built-in server, the output engine
/// and a client to itself; on a terminal, discovery and a client to the Live Output PC.
/// </summary>
public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0")
        .Split('+')[0];

    public ThisPcConfig Config { get; private set; } = ThisPcConfig.Load();
    public LibraryStore? Store { get; private set; }
    public LiveServer? Server { get; private set; }
    public DiscoveryListener? Discovery { get; private set; }
    public NexoraClient? Client { get; private set; }
    public OutputEngineHost? Output { get; private set; }

    /// <summary>UI-thread view of the link, the live picture and the signed-in user.</summary>
    public LiveModel Live { get; } = new();

    public bool IsLiveOutputPc => Config.Role == PcRole.LiveOutput;

    public void ReloadConfig() => Config = ThisPcConfig.Load();

    public void EnsureDiscovery()
    {
        Discovery ??= new DiscoveryListener();
    }

    public async Task StartAsync()
    {
        await StopAsync(stopOutput: false);
        EnsureDiscovery();

        if (Config.Role == PcRole.LiveOutput)
        {
            Store = new LibraryStore(AppPaths.LibraryFile);
            Server = new LiveServer(Store, new LiveServerOptions
            {
                Port = Config.Port,
                PairingCode = () => Config.PairingCode,
                ServerName = () => Config.PcName,
                Version = Version,
                LogsDir = AppPaths.LogsDir,
            });
            await Server.StartAsync();
            Output ??= new OutputEngineHost();
            Output.Start();
        }

        Client = new NexoraClient(ResolveUrl, () => new Hello(Config.PairingCode, Config.PcId, Config.PcName, ClientKind.Terminal, Version));
        Live.Attach(Client);
        Client.Start();
    }

    public async Task StopAsync(bool stopOutput)
    {
        Live.Detach();
        if (Client is not null) await Client.DisposeAsync();
        Client = null;
        if (Server is not null) await Server.DisposeAsync();
        Server = null;
        Store?.Dispose();
        Store = null;
        if (stopOutput && Output is not null)
        {
            Output.StopEngine();
            Output.Dispose();
            Output = null;
        }
    }

    private Uri? ResolveUrl()
    {
        if (Config.Role == PcRole.LiveOutput)
            return new Uri($"ws://127.0.0.1:{Config.Port}{Wire.Path}");

        if (ThisPcConfig.ParseAddress(Config.ManualAddress, Config.Port) is { } manual) return manual;
        if (Discovery is null) return null;
        if (Discovery.Find(Config.ServerId) is { } known) return known.Url;
        // Not paired with a particular Live Output PC yet: use the only one on the network.
        var all = Discovery.Servers;
        return Config.ServerId is null && all.Count == 1 ? all[0].Url : null;
    }

    /// <summary>Remembers which Live Output PC this terminal paired with.</summary>
    public void RememberServer(string serverId)
    {
        if (Config.Role != PcRole.Terminal || Config.ServerId == serverId || Config.ManualAddress is not null) return;
        Config.ServerId = serverId;
        try { Config.Save(); } catch { }
    }

    public static void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }
}

/// <summary>Bindable state shared by every screen of the app. Updated on the UI thread.</summary>
public sealed partial class LiveModel : ObservableObject
{
    private NexoraClient? _client;

    [ObservableProperty] private ConnectionStatus _status = ConnectionStatus.Searching;
    [ObservableProperty] private string? _rejectReason;
    [ObservableProperty] private int _latencyMs = -1;
    [ObservableProperty] private string? _serverName;
    [ObservableProperty] private string? _serverVersion;
    [ObservableProperty] private PublicInfo? _info;
    [ObservableProperty] private LiveSnapshot _snapshot = LiveSnapshot.Empty;
    [ObservableProperty] private UserInfo? _user;

    public event Action<Notice>? Notice;

    public NexoraClient? Client => _client;
    public Branding Branding => Info?.Branding ?? Branding.Default;
    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool VersionMismatch => ServerVersion is not null && ServerVersion != AppServices.Version;

    public string LinkText => Status switch
    {
        ConnectionStatus.Connected => $"{ServerName}{(LatencyMs >= 0 ? $" · {LatencyMs} ms" : "")}",
        ConnectionStatus.Searching => "Looking for the Live Output PC…",
        ConnectionStatus.Connecting => "Connecting…",
        ConnectionStatus.Reconnecting => "Connection lost · reconnecting…",
        ConnectionStatus.Rejected => RejectReason ?? "Refused by the Live Output PC",
        _ => "",
    };

    partial void OnStatusChanged(ConnectionStatus value) => RaiseLink();
    partial void OnLatencyMsChanged(int value) => OnPropertyChanged(nameof(LinkText));
    partial void OnServerNameChanged(string? value) => OnPropertyChanged(nameof(LinkText));
    partial void OnRejectReasonChanged(string? value) => OnPropertyChanged(nameof(LinkText));
    partial void OnInfoChanged(PublicInfo? value) => OnPropertyChanged(nameof(Branding));
    partial void OnServerVersionChanged(string? value) => OnPropertyChanged(nameof(VersionMismatch));

    private void RaiseLink()
    {
        OnPropertyChanged(nameof(LinkText));
        OnPropertyChanged(nameof(IsConnected));
    }

    public void Attach(NexoraClient c)
    {
        _client = c;
        c.StatusChanged += OnClientStatus;
        c.PublicInfoChanged += OnClientInfo;
        c.SnapshotChanged += OnClientSnapshot;
        c.UserChanged += OnClientUser;
        c.LatencyChanged += OnClientLatency;
        c.NoticeReceived += OnClientNotice;
    }

    public void Detach()
    {
        if (_client is not { } c) return;
        c.StatusChanged -= OnClientStatus;
        c.PublicInfoChanged -= OnClientInfo;
        c.SnapshotChanged -= OnClientSnapshot;
        c.UserChanged -= OnClientUser;
        c.LatencyChanged -= OnClientLatency;
        c.NoticeReceived -= OnClientNotice;
        _client = null;
        AppServices.OnUi(() =>
        {
            Status = ConnectionStatus.Searching;
            User = null;
            Snapshot = LiveSnapshot.Empty;
        });
    }

    private void OnClientStatus() => AppServices.OnUi(() =>
    {
        if (_client is not { } c) return;
        RejectReason = c.RejectReason;
        ServerName = c.Welcome?.ServerName;
        ServerVersion = c.Welcome?.ServerVersion;
        Status = c.Status;
        if (c.Status == ConnectionStatus.Connected && c.Welcome is { } w) AppServices.Instance.RememberServer(w.ServerId);
    });

    private void OnClientInfo() => AppServices.OnUi(() => Info = _client?.PublicInfo);
    private void OnClientSnapshot() => AppServices.OnUi(() => { if (_client is not null) Snapshot = _client.Snapshot; });
    private void OnClientUser() => AppServices.OnUi(() => User = _client?.User);
    private void OnClientLatency() => AppServices.OnUi(() => LatencyMs = _client?.LatencyMs ?? -1);
    private void OnClientNotice(Notice n) => AppServices.OnUi(() => Notice?.Invoke(n));
}
