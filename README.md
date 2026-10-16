# GlassDesk

Windows 11 x64 桌面美化工具：玻璃 Dock、启动台和统一设置中心。

[下载本分支版本](https://github.com/Li-Jiayang85/GlassDesk-Plus/releases) · [上游源码快照](https://github.com/everlosttime/glassdesk-open-source/tags)

> **本仓库是修改版（Fork）**，基于原作者 [everlosttime/glassdesk](https://github.com/everlosttime/glassdesk) 的 Apache-2.0 源码快照（1.3.1）。原始来源与许可信息见 `SOURCE-PROVENANCE.json`、`LICENSE`、`NOTICE`。

## 本分支的改动

- **移除「检查更新」功能。** 原更新通道指向上游作者的发布仓库，用它更新会下载并安装上游版本、从而覆盖本分支自己的功能。因此本分支删除了检查与安装更新的全部代码（`AppHost.Updates`、`UpdateClient`、`UpdateInstaller`、`UpdateRelease`、`UpdateWindow`、设置页入口），删除了打包用的 `update-source.json`，并删除了相关测试（`UpdateClientTests`、`UpdateInstallerTests`、`UpdateAcceptance` 工程）。「关于」页只保留版本显示。
- **小组件图标并入 Dock 栏同一行。** 不再独占 Dock 底部独立的一行，而是与启动台、此电脑、切换桌面图标、设置等按钮并排显示，尺寸、间距与对齐方式与其他按钮保持一致；点击小组件图标的行为不变，小组件仍可通过设置单独启用/禁用与调节。

除上述改动外，功能与上游 1.3.1 快照保持一致。本分支沿用同一 Apache-2.0 许可；第三方代码与资源仍保留各自许可，详见 `NOTICE` 与 `docs/third-party`。

## 公开版本

- v1.2.2：历史源码快照，包含启动台子图标 DPI 事件引发背景重建的修复。
- v1.3.1：当前源码快照，包含可独立开关的启动台、此电脑、桌面图标显隐入口、此前的外观与后台资源管理改进，以及 Dock 在窗口最大化/还原后刷新玻璃背景的修复。

每个标签是对应版本的独立完整源码快照。旧开发历史、本机用户数据、截图、日志和验收记录不包含在这个仓库中。原始版本来源见 SOURCE-PROVENANCE.json。

## 许可证

GlassDesk 原创代码采用 Apache License 2.0，允许修改、商用及闭源衍生产品，须遵守版权、许可、修改声明等要求。完整条款见 LICENSE；第三方代码与资源保留自身许可，详见 NOTICE 和 docs/third-party。

GlassDesk 不打包用户导入应用的图标，项目许可不授予第三方商标权利。

## 环境

- Windows 11 x64；源项目目标 SDK 为 Windows 10.0.26100.0，运行兼容下限为 Windows 11 Build 22000。
- .NET 10 SDK，global.json 固定工具链版本 10.0.401 并允许 latestFeature 滚动策略。
- 高级实时玻璃使用部分非公开 DWM 接口，并按能力提供兼容回退。各版本的现场验证不代表所有 Windows Build、显卡、HDR、多屏环境均已验证。

## 编译和运行

```powershell
dotnet build src/GlassDesk.App/GlassDesk.App.csproj -c Release
dotnet run --project src/GlassDesk.App/GlassDesk.App.csproj -c Release
```

程序数据位于实际运行目录旁的 Data。源码构建版也使用独立的输出目录数据。正式版按用户会话维持单实例，启动开发版前应先从托盘退出已安装版。

```powershell
dotnet test tests/GlassDesk.Core.Tests/GlassDesk.Core.Tests.csproj -c Release
dotnet test tests/GlassDesk.AppServices.Tests/GlassDesk.AppServices.Tests.csproj -c Release
dotnet test tests/GlassDesk.Dock.Tests/GlassDesk.Dock.Tests.csproj -c Release
```

部分 Windows/WPF 验收测试会创建窗口并检查真实桌面服务，应在可交互的 Windows 会话中运行。

## 从源码生成便携版

```powershell
$version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
$version = @($version | Where-Object { $_ })[0]
dotnet publish src/GlassDesk.App/GlassDesk.App.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true "-p:InformationalVersion=$version" -p:IncludeSourceRevisionInInformationalVersion=false -o artifacts/publish
Copy-Item LICENSE artifacts/publish/LICENSE.txt
Copy-Item NOTICE artifacts/publish/NOTICE.txt
Copy-Item docs/third-party artifacts/publish/third-party -Recurse
```

artifacts/publish 中的 GlassDesk.exe 可直接运行。不要把自己的 Data 目录放入分发包。框架与第三方资源的许可文件应随分发包保留。

## 从源码生成安装包

另安装 Inno Setup 7，并将 ISCC.exe 所在目录加入当前终端的 PATH。先执行上面的便携版发布命令，再执行：

```powershell
$env:GLASSDESK_PACKAGE_STAGE = (Resolve-Path artifacts/publish).Path
ISCC /DAppIdValue=GlassDesk.Independent "/DProductVersion=$version" "/DFileVersion=$version.0" /DWindows11Compatibility=1 "/DPackageBaseName=GlassDesk-$version-win-x64" /Oartifacts packaging/GlassDesk-Capture.iss
```

安装包支持选择目录。安装更新保留设置、应用库、布局和图标缓存，卸载也保留 Data。此源码打包流程不要求私有仓库的旧安装载荷。

## 修改着色器

TransitionGlass.hlsl 和编译后的 TransitionGlass.ps 均在 src/GlassDesk.UI/Effects 中。修改 HLSL 后执行 build/compile-transition-shader.ps1，再重新编译。

历史 GlassDesk.UiAcceptance 工程的目标框架低于正式项目，直接构建整个旧解决方案会报 NU1201。请按上述 App 项目构建；此问题不影响正式程序的源码构建。本次保留历史测试源码，未改写历史产品代码。
