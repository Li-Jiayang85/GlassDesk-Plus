# 自包含发布与就地更新安装副本

本文记录一次真实踩坑：**只把框架依赖（FDD）产物覆盖到自包含安装目录，会让程序启动时报
「You must install or update .NET to run this application」。**

## 症状

覆盖后双击 `%LOCALAPPDATA%\Programs\GlassDesk\GlassDesk.exe`，出现一个标题为 `GlassDesk.exe`
的对话框：

> You must install or update .NET to run this application.

进程会短暂存在但不写 `Data\logs\startup.log`，也不会创建 Dock / 启动台窗口。

## 原因

| | 安装副本（正确） | 我误覆盖的那份（错误） |
|---|---|---|
| 产物来源 | `dotnet publish --self-contained true` | `bin\Release\net10.0-windows10.0.26100.0\` |
| `runtimeconfig.json` | `includedFrameworks`（自带运行时） | `frameworks`（要求框架依赖） |
| 运行时版本 | 自带 10.0.12 | 要求 `Microsoft.NETCore.App 10.0.0` |

自带运行时的应用不需要机器上装 .NET；换成框架依赖的配置后，它反过来去找
`Microsoft.NETCore.App 10.0.0` 这个框架，找不到就在托管代码运行之前弹框退出——
连日志都来不及写。

`deps.json` 同理：自包含的清单记录的是应用目录里的全部程序集，FDD 的清单不是。

## 正确的就地更新

```powershell
# 1) 生成自包含产物
dotnet publish src\GlassDesk.App\GlassDesk.App.csproj -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=false -o <输出目录>

# 2) 关闭正在运行的实例（占着文件锁与单实例互斥体）
Get-Process GlassDesk -ErrorAction SilentlyContinue | Stop-Process -Force

# 3) 覆盖安装目录，Data\ 不要动
$dst = "$env:LOCALAPPDATA\Programs\GlassDesk"
Copy-Item "<输出目录>\*" $dst -Recurse -Force
```

要点：

- **不要混用两套运行时文件。** 自包含产物里 `coreclr.dll`、`hostfxr.dll`、
  `PresentationFramework.dll` 等都是运行时自己的，整套覆盖才自洽；只换应用程序集、
  留下旧运行时或反之，都可能出现难查的加载失败。
- **`Data\` 必须保留**：`apps.json`、`settings.json`、`dock-layout.json`、
  `launcher-layout.json` 与 `icons\` 都在里面。
- **验证**：启动后 `Data\logs\startup.log` 应新增一条带时间戳的「GlassDesk 启动」记录。
- 覆盖前先结束进程。若 `.glassdesk-data-*.lease` 残留，启动时会提示数据暂不可用，
  那是运行租约文件，正常退出会自行清理。

## 封装安装包（Inno Setup）

安装包用 `packaging/GlassDesk-Capture.iss` 编译，先备好自包含暂存目录：

```powershell
$v = '1.0.5'
$stage = 'D:\glassdesk\_tools\sd-publish'
dotnet publish src\GlassDesk.App\GlassDesk.App.csproj -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=false -o $stage

$env:GLASSDESK_PACKAGE_STAGE = $stage
& 'C:\Program Files\Inno Setup 7\ISCC.exe' `
    "/DAppIdValue=GlassDesk.Independent" `
    "/DProductVersion=$v" "/DFileVersion=$v.0" "/DPackageBaseName=GlassDesk-$v" `
    'packaging\GlassDesk-Capture.iss'
```

产物：`packaging/Output/GlassDesk-<版本>-Setup.exe`（约 51 MB，LZMA2）。

`.iss` 的几个硬约束：

- `AppId` 必须是 `GlassDesk.Independent`，否则脚本自身 `#error`。升级安装依赖它，
  `DefaultDirName={localappdata}\Programs\GlassDesk`、`PrivilegesRequired=lowest`。
- `GLASSDESK_PACKAGE_STAGE` 必须指向自包含产物目录；`[Files]` 用
  `Excludes: "Data\*"` 保证**不动用户数据**。
- 暂存目录必须含 `GlassDesk.ico`（`[Icons]` 与 `[Files]` 都引用它）。
- 编译期告警 `[UninstallRun] section entries without a RunOnceId` 是既有问题，
  不影响产物；如需消除，给两行 `[UninstallRun]` 各加 `RunOnceId` 即可。

### 只能在普通用户会话里验证安装

自动化会话（受限令牌）里跑安装程序会在最后阶段失败：

```
尝试在目标目录创建文件时出错：…\mscorrc.dll  拒绝访问。
Message box (Abort/Retry/Ignore) → User chose Abort → Rolling back changes.
```

原因是安装程序的子进程拿不到往 `%LOCALAPPDATA%\Programs\GlassDesk` 写文件的权限；
`%TEMP%` 里解开引导程序那一步也会同样报「无法创建目录 …错误 5: 拒绝访问」。
**这不是安装包缺陷**：同一份产物在普通桌面会话里可正常安装。判断依据是
日志里出现 `Dest filename: …\Programs\GlassDesk\…` 与「拒绝访问」，
而不是 `.iss` 的编译错误或版本不匹配。

所以：**封装可以在任何地方做，验收安装必须在用户的普通会话里做。**

## 版本号提醒

`Directory.Build.props` 里的 `Version` / `FileVersion` 决定文件属性与更新检查比较的版本。
自包含产物出现「新运行时、旧程序集时间戳」是正常的：未改动的项目不会重新编译，
`dotnet publish` 只是把它们复制过去。
