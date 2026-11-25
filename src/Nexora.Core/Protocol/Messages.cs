using System.Text.Json;
using System.Text.Json.Serialization;
using Nexora.Core.Model;

namespace Nexora.Core.Protocol;

/// <summary>
/// Every message on the church network. Small JSON text frames over a WebSocket:
/// commands travel, never pictures.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
// client -> server
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Login), "login")]
[JsonDerivedType(typeof(Resume), "resume")]
[JsonDerivedType(typeof(Logout), "logout")]
[JsonDerivedType(typeof(OpenSections), "open")]
[JsonDerivedType(typeof(SendContent), "send")]
[JsonDerivedType(typeof(ClearContent), "clear")]
[JsonDerivedType(typeof(RouteCommand), "route")]
[JsonDerivedType(typeof(ScreenCommand), "screen")]
[JsonDerivedType(typeof(SaveUser), "saveUser")]
[JsonDerivedType(typeof(DeleteUser), "deleteUser")]
[JsonDerivedType(typeof(SaveBranding), "saveBranding")]
[JsonDerivedType(typeof(SaveScreens), "saveScreens")]
[JsonDerivedType(typeof(Ping), "ping")]
// server -> client
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(Rejected), "rejected")]
[JsonDerivedType(typeof(PublicInfo), "info")]
[JsonDerivedType(typeof(LoginResult), "loginResult")]
[JsonDerivedType(typeof(SnapshotMessage), "snapshot")]
[JsonDerivedType(typeof(Notice), "notice")]
[JsonDerivedType(typeof(Pong), "pong")]
public abstract record Message;

// ---------- client -> server ----------

public sealed record Hello(string PairingCode, string PcId, string PcName, ClientKind Kind, string AppVersion) : Message;

public sealed record Login(string UserId, string Pin) : Message;

/// <summary>Sent after a reconnect so the operator does not have to sign in again.</summary>
public sealed record Resume(string Token) : Message;

public sealed record Logout : Message;

public sealed record OpenSections(IReadOnlyList<SectionKind> Sections) : Message;

public sealed record SendContent(SectionKind Section, SlideContent Content) : Message;

public sealed record ClearContent(SectionKind Section) : Message;

public enum RouteAction { Pin, TakeDown, Unroute, Take, Forget }

public sealed record RouteCommand(string SourceKey, string ScreenId, RouteAction Action) : Message;

public enum ScreenAction { Logo, Black, ClearText, ClearAll, Normal }

public sealed record ScreenCommand(string ScreenId, ScreenAction Action) : Message;

/// <summary>Create or update a user. Pin is null to keep the current PIN.</summary>
public sealed record SaveUser(UserInfo User, string? Pin) : Message;

public sealed record DeleteUser(string UserId) : Message;

public sealed record SaveBranding(Branding Branding) : Message;

public sealed record SaveScreens(IReadOnlyList<ScreenInfo> Screens) : Message;

public sealed record Ping(long SentTicks) : Message;

// ---------- server -> client ----------

public sealed record Welcome(string ServerId, string ServerName, string ServerVersion, string ConnectionId) : Message;

public sealed record Rejected(string Reason) : Message;

/// <summary>What the sign-in screen needs: branding and the list of names.</summary>
public sealed record PublicInfo(string ServerName, Branding Branding, IReadOnlyList<UserInfo> Users) : Message;

public sealed record LoginResult(bool Ok, string? Error, UserInfo? User, string? Token) : Message;

public sealed record SnapshotMessage(LiveSnapshot Snapshot) : Message;

public sealed record Notice(string Text, bool IsError) : Message;

public sealed record Pong(long SentTicks) : Message;

public static class Wire
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(Message m) => JsonSerializer.Serialize(m, Options);

    public static Message? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<Message>(json, Options); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public const int DefaultPort = 7400;
    public const int DiscoveryPort = 7401;
    public const string Path = "/nexora";
}
