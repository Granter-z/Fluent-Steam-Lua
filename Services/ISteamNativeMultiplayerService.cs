using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

/// <summary>
/// 合法 Steam 原生联机能力：Steam Lobby / 邀请 / 成员与大厅元数据。
/// 实际游戏的数据同步与权威状态机应由游戏本体使用 Steam Networking Sockets/Messages 完成。
/// </summary>
public interface ISteamNativeMultiplayerService : IDisposable
{
    bool IsInitialized { get; }
    uint AppId { get; }
    ulong LocalSteamId { get; }
    ulong CurrentLobbyId { get; }

    event EventHandler<SteamLobbySnapshot>? LobbyCreated;
    event EventHandler<SteamLobbySnapshot>? LobbyEntered;
    event EventHandler<IReadOnlyList<SteamLobbySnapshot>>? LobbyListUpdated;
    event EventHandler<ulong>? LobbyLeft;
    event EventHandler<ulong>? LobbyJoinRequested;
    event EventHandler? LobbyMembersChanged;

    bool Initialize(uint? expectedAppId = null);
    void RunCallbacks();

    SteamAPICallHandle CreateLobby(
        int maxMembers,
        string lobbyName,
        string gameMode = "default",
        string protocolVersion = "1");

    SteamAPICallHandle RequestLobbyList(
        string? gameMode = null,
        string protocolVersion = "1");

    SteamAPICallHandle JoinLobby(ulong lobbyId);

    void LeaveLobby();

    bool InviteUserToLobby(ulong steamId);

    bool OpenNativeInviteOverlay();

    bool SetLobbyData(string key, string value);

    string GetLobbyData(string key);

    IReadOnlyList<SteamLobbyMemberSnapshot> GetCurrentMembers();

    bool IsLobbyOwner { get; }
}

/// <summary>
/// SteamAPICall_t 的轻量只读句柄，避免 UI 层直接依赖 Steamworks 类型。
/// </summary>
public readonly record struct SteamAPICallHandle(ulong Value)
{
    public bool IsValid => Value != 0;

    public static implicit operator SteamAPICallHandle(Steamworks.SteamAPICall_t value)
        => new(value.m_SteamAPICall);
}
