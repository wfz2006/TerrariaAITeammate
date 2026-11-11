# Terraria AI Teammate · 小星

小星是泰拉瑞亚的单人游戏队友。它可以跟随、攻击、拾取物品和使用装备，也可以连接语言模型理解自然语言指令、规划多步任务。此版本供协作者实测和反馈。

## 安装

此测试版面向 Terraria 1.4.4 和 tModLoader 2026.09。在 tModLoader 中打开本地模组文件夹，将发行页下载的 `TerrariaAITeammate.tmod` 放入其中，启用模组并重新加载。[GitHub Releases](https://github.com/wfz2006/TerrariaAITeammate/releases)

## 使用

- 进入单人世界后，在聊天框输入 `/ai spawn` 召唤小星。按 **O**、点击右下角的小星图标，或输入 `/ai panel` 打开面板。
- `/ai follow`、`/ai come`、`/ai stop`、`/ai attack`、`/ai pickup` 和 `/ai equip <物品>` 等基础指令支持离线使用。面板里可以查看背包和装备。
- 在设置中填写兼容的 API 地址、模型和自己的 API Key，即可在游戏聊天框用「小星，」或 `@Xiaoxing` 开头向小星提问或布置任务。默认使用点名模式。

## 测试反馈

1. 在小星面板打开「测试记录」，或输入 `/ai diagnostics`。点「开始记录」后，才会自动收集数据，停止记录或退出世界即可结束。
2. 遇到问题时点「标记 Bug」，或在游戏聊天框输入 `/ai bug <描述>` 添加最多 500 字的复现说明。手填说明随报告导出，其中的当前配置密钥会被遮盖。
3. 随时点「导出 JSON」。文件保存在 tModLoader 存档目录下的 `AITeammate/Diagnostics/Exports`。关闭自动记录后仍可标记和导出。请手动把 JSON 和复现步骤发给维护者。

自动记录包含任务动作与结果、寻路错误码、模型用量与缓存命中、错误类别，以及 Bug 发生时的相对位置、血量等状态。本机保留两个滚动日志，每个最多 2 MiB。

## 从源码构建

安装 .NET 8 SDK 和 tModLoader，然后用 `tModLoaderPath` 指向包含 `tMLMod.targets` 的安装目录。

```text
dotnet build AITeammate.csproj -c Release -p:BuildMod=false -p:tModLoaderPath="<tModLoader 安装目录>"
```

也可以把源码放到 tModLoader 的 `ModSources/TerrariaAITeammate`，使用游戏内的模组源码菜单打包。

## English quick start

Xiaoxing is an AI teammate for single-player Terraria. Follow, combat, item pickup, and equipment commands work offline. Connect an LLM in the settings for natural-language tasks.

1. Download `TerrariaAITeammate.tmod` from the release page. Place it in your local Mods folder in tModLoader 2026.09 for Terraria 1.4.4, then enable and reload the mod.
2. Type `/ai spawn` in a single-player world. Press **O** or type `/ai panel` to open Xiaoxing's panel. Start a chat message with `@Xiaoxing` to address your teammate.
3. Open **Test diagnostics** or type `/ai diagnostics` to start or stop local recording, mark a bug, or export JSON. Use `/ai bug <description>` to include a note. Share the exported JSON and reproduction steps with the maintainer.
