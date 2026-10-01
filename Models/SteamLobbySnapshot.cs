namespace SteamLuaManager.Models;

public sealed record SteamLobbySnapshot(
    ulong LobbyId,
    string Name,
    string GameMode,
    string ProtocolVersion,
    int MemberCount,
    int MaxMembers,
    ulong OwnerSteamId
);
