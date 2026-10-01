# Steam Native Multiplayer

Fluent Steam Lua 的合法 Steam 原生联机基础模块。

## 功能

- SteamAPI 初始化与生命周期管理
- Steam Lobby 创建、搜索、加入、离开
- Steam 原生邀请 Overlay
- Steam 邀请接受后的 LobbyJoinRequested 回调
- Lobby 名称、游戏模式、协议版本等元数据
- Lobby 成员快照
- Lobby 成员变化通知
- 与现有 WPF DI 容器集成

## 重要边界

这个模块只实现 Steamworks 的会话发现/邀请层，不修改第三方游戏客户端，也不绕过 Steam DRM、授权或 AppID。

要获得真正的游戏内多人同步，目标游戏本身必须支持 Steamworks Multiplayer，或者你拥有该游戏的合法源码/SDK 集成权。在这种情况下，可在 Lobby 建立后继续使用 Steam Networking Sockets/Messages 实现游戏状态同步。

## 使用

在 DI 中已经注册：

```csharp
services.AddSingleton<ISteamNativeMultiplayerService, SteamNativeMultiplayerService>();
```

在合法的 Steamworks AppID 环境下初始化：

```var multiplayer = ServiceProvider.GetRequiredService<ISteamNativeMultiplayerService>();

if (multiplayer.Initialize(expectedAppId: YOUR_STEAM_APP_ID))
{
    multiplayer.CreateLobby(4, "我的大厅", "coop", "1");
}
```

搜索：

```multiplayer.RequestLobbyList(gameMode: "coop", protocolVersion: "1");
```

加入：

```multiplayer.JoinLobby(lobbyId);
```

邀请：

```multiplayer.OpenNativeInviteOverlay();
multiplayer.InviteUserToLobby(friendSteamId);
```

程序退出时：

```multiplayer.Dispose();
```

## 开发环境

项目目标框架为 .NET 8 WPF。

当前使用 Steamworks.NET 2024.8.0 作为 NuGet 引用；Steamworks.NET 是 Valve Steamworks API 的 C# wrapper。生产发布前应根据你的 Steamworks SDK/AppID 发布要求固定版本并准备对应的 Steam API runtime 文件。

## 下一层

如果这是你自己的 Steam 游戏/有正式 SDK 集成权限的项目，建议下一步增加：

1. Steam Networking Sockets P2P/Relay 连接管理
2. 连接状态机与重连
3. Host/Client 权威状态同步
4. Lobby → Networking Identity 的绑定
5. 游戏内邀请 Join Game
6. NAT/Relay 状态显示
7. Ping/丢包/连接质量统计
