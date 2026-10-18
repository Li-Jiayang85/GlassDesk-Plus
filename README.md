# GlassDesk Plus

Windows 11 桌面美化：一块会呼吸的毛玻璃程序坞、一个能搜索能整理的启动台、一排各自开关的小组件，以及一个统一设置中心。

**下载安装包**：[Releases](https://github.com/Li-Jiayang85/GlassDesk-Plus/releases) · 只支持 Windows 11 x64（Build 22000 及以上）

> 本仓库是 [everlosttime/glassdesk](https://github.com/everlosttime/glassdesk) 的修改版（Fork），
> 基于其 Apache-2.0 源码快照（1.3.1）。来源与许可信息见 `SOURCE-PROVENANCE.json`、`LICENSE`、`NOTICE`。

## 功能

**程序坞（Dock）**

- 实时毛玻璃面板，大小、外框长度、水平/垂直偏移、透明度都可调
- 四个自带入口——启动台、此电脑、切换桌面图标、设置——**各自独立显示/隐藏**，隐藏后不留空位
- 应用图标可拖动排序，可拖入/拖出分组
- 窗口最大化或全屏时自动让位，还原后自动回来，不会压在窗口上

**启动台（Launcher）**

- 分页网格 + 搜索，支持键盘操作
- 右键多选后可 **批量删除**：勾选角标标记，顶部工具栏「删除」一次清除，不弹确认框
- 拖拽调整顺序、拖入分组

**小组件**

- 七个：时钟、日期、系统监控、天气、音频播放控制、备忘录、待办事项
- **每个都有独立开关**，默认全部关闭，勾哪个显示哪个
- 刷新频率按类型固定：时钟 0.5 秒、其余 1 秒、天气 30 分钟
- 图标并入 Dock 同一行，与其它入口同尺寸同对齐

**其它**

- 统一设置中心：外观、Dock、启动台、快捷操作、性能、小组件、桌面、备份恢复、关于
- 全局快捷键可改键；性能策略在全屏/空闲时自动休眠以省电
- 设置、应用库、布局、图标缓存可一键备份与恢复；卸载保留数据
- 应用内检查更新，更新源指向本仓库的 Releases

## 版本

当前版本 **1.4.11**。每个标签是对应版本的完整源码快照，安装包见 [Releases](https://github.com/Li-Jiayang85/GlassDesk-Plus/releases)。

| 版本 | 主要变化 |
|---|---|
| 1.4.11 | 修发布说明被序列化成哈希表文本 |
| 1.4.10 | Dock 改为非置顶；修遮挡误收起与桌面歌词挤没 Dock |
| 1.4.9 | 最大化/全屏时 Dock 让位；透明度滑杆整段有效 |
| 1.4.8 | 移除日历小组件 |
| 1.4.7 | 时钟 0.5 秒刷新；修输入框字号与框高 |
| 1.4.6 | 修小组件面板文字与文本框裁切 |
| 1.4.5 | 小组件刷新频率固定化 |
| 1.4.4 | 小组件改为各自独立开关 |
| 1.4.3 | 小组件默认关闭 |
| 1.4.2 | Dock 自带图标全部可在设置中显示/隐藏 |
| 1.4.1 | 启动台批量删除、Dock 拖动排序 |
| ≤ 1.3.1 | 上游快照 |

## 与上游的差异

- **更新通道改指向本仓库。** 上游的检查更新会下载并安装上游版本、覆盖本分支的功能；
  本分支改为使用自己的 Releases（资产命名 `GlassDesk-Plus-<版本>-win-x64-Setup.exe`）。
- **Dock 自带入口可全部隐藏**，包括原先固定显示的「设置」。
- **小组件重做**：并入 Dock 同一行、默认关闭、每个独立开关、刷新频率固定、移除日历。
- **启动台批量删除**与 **Dock 拖动排序**。
- **Dock 自动让位**：窗口最大化/全屏时收起，还原后恢复。

其余基础功能（玻璃材质、外观、快捷操作、性能休眠、备份恢复）与上游 1.3.1 快照一致。
本分支沿用同一 Apache-2.0 许可；第三方代码与资源保留各自许可，详见 `NOTICE` 与 `docs/third-party`。

## 环境

- Windows 11 x64；目标 SDK 为 Windows 10.0.26100.0，运行兼容下限 Windows 11 Build 22000
- .NET 10 SDK（`global.json` 固定工具链 10.0.401）
- 高级实时玻璃使用部分非公开 DWM 接口，并按能力提供兼容回退

## 编译和运行

```powershell
dotnet build src/GlassDesk.App/GlassDesk.App.csproj -c Release
dotnet run --project src/GlassDesk.App/GlassDesk.App.csproj -c Release
```

程序数据位于运行目录旁的 `Data`。正式版按用户会话维持单实例，启动开发版前应先从托盘退出已安装版。

```powershell
dotnet test tests/GlassDesk.Core.Tests/GlassDesk.Core.Tests.csproj -c Release
dotnet test tests/GlassDesk.AppServices.Tests/GlassDesk.AppServices.Tests.csproj -c Release
dotnet test tests/GlassDesk.Dock.Tests/GlassDesk.Dock.Tests.csproj -c Release
```

此外还有两个可运行的验收程序（需要真实桌面会话）：

```powershell
# 窗口级验收：启动台、Dock 入口、批量删除、小组件
tests/GlassDesk.LauncherAcceptance/bin/Release/net10.0-windows10.0.26100.0/GlassDesk.LauncherAcceptance.exe --widgets-default
# 更新通道验收（--online 会真实访问 GitHub 并下载校验）
tests/GlassDesk.UpdateAcceptance/bin/Release/net10.0-windows10.0.26100.0/GlassDesk.UpdateAcceptance.exe --online
```

## 打包

```powershell
# 生成自包含安装包（需要 Inno Setup 7 的 ISCC.exe 在 PATH 中）
build/package.ps1 -Version 1.4.9
# 建 release 并上传
build/release.ps1 -Version 1.4.9 -SkipPackage
```

## 许可证

GlassDesk 原创代码采用 Apache License 2.0，允许修改、商用及闭源衍生产品，须遵守版权、许可、修改声明等要求。
完整条款见 `LICENSE`；第三方代码与资源保留自身许可，详见 `NOTICE` 和 `docs/third-party`。

GlassDesk 不打包用户导入应用的图标，项目许可不授予第三方商标权利。
