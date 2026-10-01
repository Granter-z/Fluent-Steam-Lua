using System.Collections.ObjectModel;
using System.Windows.Threading;
using SteamLuaManager.Models;
using Steamworks;

namespace SteamLuaManager.Services;

/// <summary>
/// Steam 原生大厅层。
///
/// 设计边界：
/// 1. 使用真实 Steamworks AppID，不伪造第三方 AppID。
/// 2. Lobby 只负责发现、邀请、成员与会话元数据。
/// 3. 游戏状态同步必须由实际游戏客户端实现；这里不修改第三方游戏进程，也不绕过 DRM/授权。
/// </summary>
public sealed class SteamNativeMultiplayerService : ISteamNativeMultiplayerService
{
    public const string LobbyNameKey = "name";
    public const string GameModeKey = "game_mode";
    public const string ProtocolVersionKey = "protocol";
    public const string JoinableKey = "joinable";
    public const string FeatureKey = "fsl_native_mp";

    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _callbackTimer;

    private CallResult<LobbyCreated_t>? _lobbyCreatedCallResult;
    private CallResult<LobbyEnter_t>? _lobbyEnterCallResult;
    private CallResult<LobbyMatchList_t>? _lobbyMatchListCallResult;

    private Callback<LobbyDataUpdate_t>? _lobbyDataUpdate;
    private Callback<LobbyChatUpdate_t>? _lobbyChatUpdate;
    private Callback<GameLobbyJoinRequested_t>? _gameLobbyJoinRequested;

    private CSteamID _currentLobby = CSteamID.Nil;
    private bool _disposed;

    public SteamNativeMultiplayerService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public bool IsInitialized { get; private set; }

    public uint AppId =>
        IsInitialized
            ? SteamUtils.GetAppID().m_AppId
            : 0;

    public ulong LocalSteamId =>
        IsInitialized
            ? SteamUser.GetSteamID().m_SteamID
            : 0;

    public ulong CurrentLobbyId => _currentLobby.m_SteamID;

    public bool IsLobbyOwner =>
        IsInitialized &&
        _currentLobby.IsValid() &&
        SteamMatchmaking.GetLobbyOwner(_currentLobby) == SteamUser.GetSteamID();

    public event EventHandler<SteamLobbySnapshot>? LobbyCreated;
    public event EventHandler<SteamLobbySnapshot>? LobbyEntered;
    public event EventHandler<IReadOnlyList<SteamLobbySnapshot>>? LobbyListUpdated;
    public event EventHandler<ulong>? LobbyLeft;
    public event EventHandler<ulong>? LobbyJoinRequested;
    public event EventHandler? LobbyMembersChanged;

    public bool Initialize(uint? expectedAppId = null)
    {
        ThrowIfDisposed();

        if (IsInitialized)
        {
            if (expectedAppId.HasValue && AppId != expectedAppId.Value)
                throw new InvalidOperationException(
                    $"Steam AppID 不匹配：当前={AppId}，期望={expectedAppId.Value}。");
            return true;
        }

        if (!SteamAPI.Init())
            return false;

        var actualAppId = SteamUtils.GetAppID().m_AppId;
        if (actualAppId == 0)
        {
            SteamAPI.Shutdown();
            return false;
        }

        if (expectedAppId.HasValue && actualAppId != expectedAppId.Value)
        {
            SteamAPI.Shutdown();
            throw new InvalidOperationException(
                $"Steam AppID 不匹配：当前={actualAppId}，期望={expectedAppId.Value}。");
        }

        _lobbyCreatedCallResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        _lobbyEnterCallResult = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
        _lobbyMatchListCallResult = CallResult<LobbyMatchList_t>.Create(OnLobbyMatchList);

        _lobbyDataUpdate = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);
        _lobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
        _gameLobbyJoinRequested =
            Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);

        _callbackTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Background,
            (_, _) => SteamAPI.RunCallbacks(),
            _dispatcher);

        _callbackTimer.Start();
        IsInitialized = true;
        return true;
    }

    public void RunCallbacks()
    {
        if (!IsInitialized || _disposed)
            return;

        SteamAPI.RunCallbacks();
    }

    public SteamAPICallHandle CreateLobby(
        int maxMembers,
        string lobbyName,
        string gameMode = "default",
        string protocolVersion = "1")
    {
        EnsureInitialized();

        if (maxMembers is < 2 or > 250)
            throw new ArgumentOutOfRangeException(nameof(maxMembers), "Steam Lobby 成员数必须在 2~250 之间。");

        if (string.IsNullOrWhiteSpace(lobbyName))
            lobbyName = $"{SteamFriends.GetPersonaName()}'s Lobby";

        var call = SteamMatchmaking.CreateLobby(
            ELobbyType.k_ELobbyTypePublic,
            maxMembers);

        _lobbyCreatedCallResult!.Set(call);

        // 元数据在 LobbyCreated_t 成功回调后写入，确保 Lobby ID 已经有效。
        _pendingLobbyName = lobbyName.Trim();
        _pendingGameMode = string.IsNullOrWhiteSpace(gameMode) ? "default" : gameMode.Trim();
        _pendingProtocolVersion = string.IsNullOrWhiteSpace(protocolVersion) ? "1" : protocolVersion.Trim();

        return call;
    }

    public SteamAPICallHandle RequestLobbyList(
        string? gameMode = null,
        string protocolVersion = "1")
    {
        EnsureInitialized();

        SteamMatchmaking.AddRequestLobbyListDistanceFilter(
            ELobbyDistanceFilter.k_ELobbyDistanceFilterDefault);

        SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);
        SteamMatchmaking.AddRequestLobbyListStringFilter(
            FeatureKey,
            "1",
            ELobbyComparison.k_ELobbyComparisonEqual);

        if (!string.IsNullOrWhiteSpace(protocolVersion))
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                ProtocolVersionKey,
                protocolVersion,
                ELobbyComparison.k_ELobbyComparisonEqual);
        }

        if (!string.IsNullOrWhiteSpace(gameMode))
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                GameModeKey,
                gameMode,
                ELobbyComparison.k_ELobbyComparisonEqual);
        }

        var call = SteamMatchmaking.RequestLobbyList();
        _lobbyMatchListCallResult!.Set(call);
        return call;
    }

    public SteamAPICallHandle JoinLobby(ulong lobbyId)
    {
        EnsureInitialized();

        var id = new CSteamID(lobbyId);
        if (!id.IsValid())
            throw new ArgumentException("无效的 Steam Lobby ID。", nameof(lobbyId));

        var call = SteamMatchmaking.JoinLobby(id);
        _lobbyEnterCallResult!.Set(call);
        return call;
    }

    public void LeaveLobby()
    {
        if (!IsInitialized || !_currentLobby.IsValid())
            return;

        var lobbyId = _currentLobby.m_SteamID;
        SteamMatchmaking.LeaveLobby(_currentLobby);
        _currentLobby = CSteamID.Nil;
        LobbyLeft?.Invoke(this, lobbyId);
    }

    public bool InviteUserToLobby(ulong steamId)
    {
        EnsureInitialized();

        if (!_currentLobby.IsValid())
            return false;

        var user = new CSteamID(steamId);
        return user.IsValid() &&
               SteamMatchmaking.InviteUserToLobby(_currentLobby, user);
    }

    public bool OpenNativeInviteOverlay()
    {
        EnsureInitialized();

        if (!_currentLobby.IsValid())
            return false;

        SteamFriends.ActivateGameOverlayInviteDialog(_currentLobby);
        return true;
    }

    public bool SetLobbyData(string key, string value)
    {
        EnsureInitialized();

        if (!_currentLobby.IsValid())
            return false;

        ValidateLobbyKey(key);
        return SteamMatchmaking.SetLobbyData(_currentLobby, key, value ?? string.Empty);
    }

    public string GetLobbyData(string key)
    {
        EnsureInitialized();

        if (!_currentLobby.IsValid())
            return string.Empty;

        ValidateLobbyKey(key);
        return SteamMatchmaking.GetLobbyData(_currentLobby, key) ?? string.Empty;
    }

    public IReadOnlyList<SteamLobbyMemberSnapshot> GetCurrentMembers()
    {
        EnsureInitialized();

        if (!_currentLobby.IsValid())
            return Array.Empty<SteamLobbyMemberSnapshot>();

        var owner = SteamMatchmaking.GetLobbyOwner(_currentLobby);
        var count = SteamMatchmaking.GetNumLobbyMembers(_currentLobby);
        var result = new List<SteamLobbyMemberSnapshot>(count);

        for (var i = 0; i < count; i++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(_currentLobby, i);
            if (!member.IsValid())
                continue;

            result.Add(new SteamLobbyMemberSnapshot(
                member.m_SteamID,
                SteamFriends.GetFriendPersonaName(member),
                member == owner));
        }

        return new ReadOnlyCollection<SteamLobbyMemberSnapshot>(result);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _callbackTimer?.Stop();
        _callbackTimer = null;

        if (IsInitialized)
        {
            try
            {
                LeaveLobby();

                _lobbyCreatedCallResult?.Dispose();
                _lobbyEnterCallResult?.Dispose();
                _lobbyMatchListCallResult?.Dispose();

                _lobbyDataUpdate?.Dispose();
                _lobbyChatUpdate?.Dispose();
                _gameLobbyJoinRequested?.Dispose();
            }
            finally
            {
                SteamAPI.Shutdown();
                IsInitialized = false;
            }
        }
    }

    private string _pendingLobbyName = string.Empty;
    private string _pendingGameMode = "default";
    private string _pendingProtocolVersion = "1";

    private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
    {
        if (ioFailure || result.m_eResult != EResult.k_EResultOK)
        {
            LobbyCreated?.Invoke(this,
                new SteamLobbySnapshot(
                    0,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    0,
                    0));
            return;
        }

        _currentLobby = new CSteamID(result.m_ulSteamIDLobby);

        SteamMatchmaking.SetLobbyData(_currentLobby, FeatureKey, "1");
        SteamMatchmaking.SetLobbyData(_currentLobby, LobbyNameKey, _pendingLobbyName);
        SteamMatchmaking.SetLobbyData(_currentLobby, GameModeKey, _pendingGameMode);
        SteamMatchmaking.SetLobbyData(_currentLobby, ProtocolVersionKey, _pendingProtocolVersion);
        SteamMatchmaking.SetLobbyData(_currentLobby, JoinableKey, "1");
        SteamMatchmaking.SetLobbyJoinable(_currentLobby, true);

        PublishLobbySnapshot(LobbyCreated, _currentLobby);
    }

    private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
    {
        var lobby = new CSteamID(result.m_ulSteamIDLobby);

        if (ioFailure ||
            result.m_EChatRoomEnterResponse !=
            EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        {
            LobbyEntered?.Invoke(this,
                new SteamLobbySnapshot(
                    lobby.m_SteamID,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    0,
                    0));
            return;
        }

        _currentLobby = lobby;
        PublishLobbySnapshot(LobbyEntered, _currentLobby);
    }

    private void OnLobbyMatchList(LobbyMatchList_t result, bool ioFailure)
    {
        if (ioFailure)
        {
            LobbyListUpdated?.Invoke(
                this,
                Array.Empty<SteamLobbySnapshot>());
            return;
        }

        var lobbies = new List<SteamLobbySnapshot>((int)result.m_nLobbiesMatching);
        for (var i = 0; i < result.m_nLobbiesMatching; i++)
        {
            var lobby = SteamMatchmaking.GetLobbyByIndex(i);
            if (!lobby.IsValid())
                continue;

            lobbies.Add(BuildSnapshot(lobby));
        }

        LobbyListUpdated?.Invoke(this, lobbies);
    }

    private void OnLobbyDataUpdate(LobbyDataUpdate_t callback)
    {
        if (!_currentLobby.IsValid())
            return;

        var changedLobby = new CSteamID(callback.m_ulSteamIDLobby);
        if (changedLobby != _currentLobby)
            return;

        LobbyMembersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLobbyChatUpdate(LobbyChatUpdate_t callback)
    {
        if (!_currentLobby.IsValid())
            return;

        var changedLobby = new CSteamID(callback.m_ulSteamIDLobby);
        if (changedLobby != _currentLobby)
            return;

        LobbyMembersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t callback)
    {
        var lobby = callback.m_steamIDLobby;
        LobbyJoinRequested?.Invoke(this, lobby.m_SteamID);

        // Steam 原生邀请被接受时，直接走标准 JoinLobby 流程。
        JoinLobby(lobby.m_SteamID);
    }

    private SteamLobbySnapshot BuildSnapshot(CSteamID lobby)
    {
        var owner = SteamMatchmaking.GetLobbyOwner(lobby);

        return new SteamLobbySnapshot(
            lobby.m_SteamID,
            SteamMatchmaking.GetLobbyData(lobby, LobbyNameKey) ?? string.Empty,
            SteamMatchmaking.GetLobbyData(lobby, GameModeKey) ?? string.Empty,
            SteamMatchmaking.GetLobbyData(lobby, ProtocolVersionKey) ?? string.Empty,
            SteamMatchmaking.GetNumLobbyMembers(lobby),
            SteamMatchmaking.GetLobbyMemberLimit(lobby),
            owner.m_SteamID);
    }

    private void PublishLobbySnapshot(
        EventHandler<SteamLobbySnapshot>? handler,
        CSteamID lobby)
    {
        handler?.Invoke(this, BuildSnapshot(lobby));
    }

    private static void ValidateLobbyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Lobby metadata key 不能为空。", nameof(key));

        if (key.Length > Constants.k_nMaxLobbyKeyLength)
            throw new ArgumentException(
                $"Lobby metadata key 不能超过 {Constants.k_nMaxLobbyKeyLength} 个字符。",
                nameof(key));
    }

    private void EnsureInitialized()
    {
        ThrowIfDisposed();

        if (!IsInitialized)
            throw new InvalidOperationException(
                "Steam Native Multiplayer 尚未初始化。请先调用 Initialize()。");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
