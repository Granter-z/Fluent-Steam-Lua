namespace SteamLuaManager.Models;

public sealed record SteamLobbyMemberSnapshot(
    ulong SteamId,
    string PersonaName,
    bool IsOwner
);
