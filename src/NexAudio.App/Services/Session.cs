using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexora.Core.Config;
using Nexora.Core.Model;
using Nexora.Core.Net;
using Nexora.Core.Protocol;

namespace NexAudio.App.Services;

/// <summary>
/// Who is at the desk. NEX-Audio signs in with the church's NEXORA users and PINs, through the Live Output
/// PC. Each user who signs in here once can sign in again when the Live Output PC cannot be reached
/// (their PIN is kept here only as a salted hash), so the desk never depends on the network.
/// </summary>
public sealed partial class Session : ObservableObject, IAsyncDisposable
{
    private DiscoveryListener? _discovery;
    private NexoraClient? _client;
    private List<CachedUser> _cache = LoadCache();

    public ThisPcConfig Config { get; private set; } = ThisPcConfig.Load();

    [ObservableProperty] private UserInfo? _user;
    [ObservableProperty] private bool _offline;
    [ObservableProperty] private ConnectionStatus _status = ConnectionStatus.Searching;
    [ObservableProperty] private string _linkText = "Looking for the Live Output PC…";
    [ObservableProperty] private Branding? _branding;
    [ObservableProperty] private IReadOnlyList<UserInfo> _users = [];

    public bool IsConnected => Status == ConnectionStatus.Connected;
    public NexoraClient? Client => _client;
    public bool HasPairingCode => Config.PairingCode.Length > 0;

    /// <summary>Starts looking for the Live Output PC. Takes NEXORA's settings on this PC the first time, if there are any.</summary>
    public void Start(string version)
    {
        if (Config.Role == PcRole.NotSet) TakeNexoraSettings();
        _discovery ??= new DiscoveryListener();
        _client = new NexoraClient(ResolveUrl, () => new Hello(Config.PairingCode, Config.PcId, Config.PcName + " · NEX-Audio", ClientKind.Terminal, version));
        _client.StatusChanged += () => OnUi(UpdateStatus);
        _client.PublicInfoChanged += () => OnUi(UpdateUsers);
        _client.UserChanged += () => OnUi(() =>
        {
            // Signed out from the Live Output PC (for example a user removed): leave the desk too.
            if (_client?.User is null && User is not null && !Offline && IsConnected) User = null;
        });
        _client.Start();
        UpdateUsers();
    }

    private void TakeNexoraSettings()
    {
        Config.Role = PcRole.Terminal;
        if (DeskPaths.NexoraConfig() is { } n)
        {
            Config.PairingCode = n.PairingCode;
            Config.Port = n.Port;
            Config.ServerId = n.ServerId;
            Config.ManualAddress = n.Role == PcRole.LiveOutput ? "127.0.0.1" : n.ManualAddress;
            Config.PcName = n.PcName;
        }
        Save();
    }

    /// <summary>The pairing code from NEXORA's Settings on the Live Output PC, and an address if discovery cannot find it.</summary>
    public void SetConnection(string pairingCode, string? address)
    {
        Config.PairingCode = pairingCode.Trim();
        Config.ManualAddress = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        Config.ServerId = null;
        Save();
        _client?.Reconnect();
    }

    private void Save()
    {
        try { Config.Save(); }
        catch (Exception ex) { AppLog.Write("NEX-Audio settings: " + ex.Message); }
    }

    private Uri? ResolveUrl()
    {
        if (ThisPcConfig.ParseAddress(Config.ManualAddress, Config.Port) is { } manual) return manual;
        if (_discovery is null) return null;
        if (_discovery.Find(Config.ServerId) is { } known) return known.Url;
        var all = _discovery.Servers;
        return Config.ServerId is null && all.Count == 1 ? all[0].Url : null;
    }

    private void UpdateStatus()
    {
        if (_client is not { } c) return;
        Status = c.Status;
        OnPropertyChanged(nameof(IsConnected));
        LinkText = c.Status switch
        {
            ConnectionStatus.Connected => $"Connected to {c.Welcome?.ServerName ?? "the Live Output PC"}",
            ConnectionStatus.Rejected => c.RejectReason ?? "The Live Output PC turned this PC away. Check the pairing code.",
            ConnectionStatus.Connecting => "Connecting to the Live Output PC…",
            ConnectionStatus.Reconnecting => "Lost the Live Output PC. Trying again…",
            _ => HasPairingCode ? "Looking for the Live Output PC…" : "Not set up: enter the pairing code below.",
        };
        if (c.Status == ConnectionStatus.Connected && c.Welcome is { } w && Config.ServerId != w.ServerId && Config.ManualAddress is null)
        {
            Config.ServerId = w.ServerId;
            Save();
        }
        UpdateUsers();
    }

    private void UpdateUsers()
    {
        if (_client?.PublicInfo is { } info && IsConnected)
        {
            Users = info.Users;
            Branding = info.Branding;
        }
        else Users = _cache.Select(c => c.User).ToList();
    }

    /// <summary>Checks the PIN with the Live Output PC, or, when it cannot be reached, with this PC's copy.</summary>
    public async Task<string?> SignInAsync(UserInfo who, string pin)
    {
        if (IsConnected && _client is { } c)
        {
            var r = await c.LoginAsync(who.Id, pin);
            if (!r.Ok || r.User is null) return r.Error ?? "That PIN is not right.";
            Remember(r.User, pin);
            Offline = false;
            User = r.User;
            return null;
        }
        var cached = _cache.FirstOrDefault(x => x.User.Id == who.Id);
        if (cached is null) return "The Live Output PC cannot be reached, and this user has not signed in on this PC before.";
        if (!cached.Matches(pin)) return "That PIN is not right.";
        Offline = true;
        User = cached.User;
        return null;
    }

    public async Task SignOutAsync()
    {
        User = null;
        Offline = false;
        if (IsConnected && _client is { } c)
        {
            try { await c.LogoutAsync(); }
            catch (Exception) { }
        }
    }

    private void Remember(UserInfo u, string pin)
    {
        _cache.RemoveAll(x => x.User.Id == u.Id);
        _cache.Add(CachedUser.Make(u, pin));
        try
        {
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(_cache, Wire.Options));
        }
        catch (Exception ex) { AppLog.Write("NEX-Audio users: " + ex.Message); }
    }

    private static string CacheFile => Path.Combine(AppPaths.LocalDir, "users.json");

    private static List<CachedUser> LoadCache()
    {
        try
        {
            if (File.Exists(CacheFile)) return JsonSerializer.Deserialize<List<CachedUser>>(File.ReadAllText(CacheFile), Wire.Options) ?? [];
        }
        catch (Exception) { }
        return [];
    }

    private static void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_discovery is not null) await _discovery.DisposeAsync();
    }
}

/// <summary>A user who signed in on this PC, with a salted hash of their PIN for signing in offline.</summary>
public sealed record CachedUser(UserInfo User, string Salt, string Hash)
{
    public static CachedUser Make(UserInfo u, string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new CachedUser(u, Convert.ToBase64String(salt), Convert.ToBase64String(Derive(pin, salt)));
    }

    public bool Matches(string pin)
    {
        try
        {
            var want = Convert.FromBase64String(Hash);
            return CryptographicOperations.FixedTimeEquals(Derive(pin, Convert.FromBase64String(Salt)), want);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string pin, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(pin, salt, 100_000, HashAlgorithmName.SHA256, 32);
}
