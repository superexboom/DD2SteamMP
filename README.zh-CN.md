# DD2 Steam MP

[English README](README.md)

DD2 Steam MP 是一个用于《Darkest Dungeon II》的实验性 Steam 大厅和 Steam Networking 联机主机。它在一个原本没有联机设计的游戏上，追加合作控制、投票、镜像 HUD 和自定义 PvP/debug 战斗流程。

本项目不隶属于 Red Hook Studios。它是研究和调试性质的 Mod，不是商业级完整联机产品。

## 当前范围

- Steam 好友房间创建、邀请覆盖层、房间成员、版本检查。
- Steam Networking 消息传输。
- Doorstop 启动入口：先加载 DD2SteamMP，再链式启动 BepInEx，以兼容常规 BepInEx 插件。
- 游戏内控制面板、房间状态、玩家分配、主客机 UI 中英切换。
- 按分配槽位转发英雄战斗输入，并支持 PvP 敌方阵营控制。
- 路线、故事、战利品、旅馆选择、巢穴继续、弹窗、祭坛/忏悔和部分 run 交互的投票/同步。
- 客机镜像 HUD，包括战斗、地图、背包/run 状态、商店/loadout 和自定义 debug-demo 设置。
- 自定义 debug-demo/PvP 战斗设置，包括英雄预设、怪物预设、连战、火炬/忏悔选项和部分竞技场修正。

## 重要限制

- 客机不会运行完全同步的 DD2 原生 Unity 场景。当前实际目标是远程控制加镜像 UI，而不是确定性场景复制。
- 主机仍然是游戏状态权威端。
- 所有玩家都需要兼容版本的 DD2SteamMP。Mod 会检查房间版本。
- 这是 Doorstop host，不是普通 `BepInEx/plugins` DLL。
- 游戏更新可能破坏内部 hook 和 UI 适配器。

## 控制

主要操作都在游戏内 UI 中提供。历史调试快捷键如下：

| 按键 | 操作 |
| --- | --- |
| `F6` | 镜像 HUD / 客机游玩 UI |
| `F7` | 主机/控制面板 |
| `F8` | 输出房间状态 |
| `F9` | 创建好友房间 |
| `F10` | 打开 Steam 邀请覆盖层 |
| `F11` | 离开房间 |

现在大多数面板已经提供按钮，不再需要依赖 command file 手输。

## Release 包结构

公开 release 包刻意保持为纯 DLL：

```text
Darkest Dungeon II/
└─ DD2SteamMP/
   ├─ DD2SteamMultiplayerDoorstop.dll
   ├─ DD2SteamMultiplayerHost.dll
   └─ DD2DebugDemoCore.dll
```

该包不包含 PowerShell 安装脚本，仍需要手动配置 Doorstop。

## 手动安装

1. 为普通 Unity/Mono 版《Darkest Dungeon II》安装 BepInEx 5。
2. 将 release zip 里的 `DD2SteamMP` 文件夹复制到游戏目录。
3. 备份 `doorstop_config.ini`。
4. 将 Doorstop 的目标程序集改成：

```ini
target_assembly=DD2SteamMP\DD2SteamMultiplayerDoorstop.dll
```

5. 保持 Doorstop 启用。
6. 通过 Steam 启动游戏。
7. 启动后检查 `DD2SteamMP/doorstop_host.log`。

DD2SteamMP 的 Doorstop 入口会启动联机 host，然后链式启动原本的 BepInEx preloader，因此已有 BepInEx 插件仍可加载。

## 环境要求

- Steam 版《Darkest Dungeon II》
- Steam 正在运行，且与启动游戏的账号一致
- 已安装 BepInEx 5.x / Doorstop
- Unity/Mono 版 BepInEx，不是 IL2CPP 版
- 每台玩家机器安装兼容版本的 DD2SteamMP
- 能够构建 `net48` 的 .NET SDK 或构建工具
- 来自 `Darkest Dungeon II_Data/Managed` 的本地游戏程序集

## 构建

1. 将 `Directory.Build.props.example` 复制为 `Directory.Build.props`。
2. 设置 `BepInExDir` 和 `ManagedDir`。
3. 构建主项目：

```powershell
dotnet build .\DD2SteamMultiplayerHost\DD2SteamMultiplayerHost.csproj -c Release
dotnet build .\DD2SteamMultiplayerDoorstop\DD2SteamMultiplayerDoorstop.csproj -c Release
```

Host 项目会同时构建 `DD2DebugDemoCore`。

## 源码结构

- `DD2SteamMultiplayerDoorstop`：Doorstop 入口、BepInEx 链式加载和早期运行时补丁。
- `DD2SteamMultiplayerHost`：Steam lobby/networking、联机会话逻辑、UI、适配器、镜像 HUD、PvP/debug-demo 控制。
- `DD2DebugDemoCore`：可复用的 debug-demo 角色/loadout/装备辅助逻辑。
- `DD2SteamMultiplayerRuntime`：较早的 runtime adapter 源码，保留用于参考和兼容工作。

仓库排除了游戏程序集、反编译游戏源码、导出资源、本地安装路径和构建产物。

## 兼容性说明

- 本 Mod 和 BepInEx 使用同一个 Doorstop 入口位置，所以必须正确链式加载 BepInEx。
- 如果其他加载器也替换了 `target_assembly`，需要手动合并。
- 联机行为是逐步测试出来的。DD2 新版本发布后，建议先通过日志和基础流程重新验证。
