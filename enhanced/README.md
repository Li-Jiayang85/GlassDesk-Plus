# GlassDesk Plus

Windows 11 桌面美化工具 **GlassDesk 1.4.4** 的增强版，由 **喜羊羊（远程电脑技术支持）** 维护与发布。

原始程序：**GlassDesk**，原作者 [everlosttime](https://github.com/everlosttime/glassdesk)（Apache-2.0 许可证）。

GitHub：<https://github.com/Li-Jiayang85>

## 下载安装

从 [Releases](https://github.com/Li-Jiayang85/GlassDesk-Plus/releases) 下载最新安装包：

**`GlassDesk-1.4.4-Plus-2.0.0-win-x64-Setup.exe`**

双击运行即可，安装包会自动完成：

1. 安装原版 GlassDesk 1.4.4（若尚未安装）
2. 部署增强组件到 `%LOCALAPPDATA%\GlassDeskPlusPatch\`
3. 注册启动钩子并广播，让新开的程序立即生效
4. 启动 GlassDesk

> 静默安装（无人值守）：`GlassDesk-1.4.4-Plus-2.0.0-win-x64-Setup.exe --silent`

## 功能

| 功能 | 说明 |
|---|---|
| 桌面信息栏位置 | 顶部居中（默认）/ 左上 / 右上 / 左下 / 右下；设置中与「启用桌面信息栏」同组 |
| 控制中心跟随 | 展开的控制中心横向与信息栏中线对齐，紧贴信息栏上方或下方 |
| 启动台滚轮翻页 | 滚轮下滚下一页、上滚上一页，120ms 防抖 |
| 搜索时排除采集 | 搜索框有文字时临时把启动台排除在采集外，修掉玻璃层的上一页残影 |
| 界面精简 | 隐藏原版的赞助页、关于页赞助区、检查更新与网盘下载入口 |
| 关于与致谢 | 关于页显示原作者、维护者、抖音账号、版本与 GitHub 主页 |
| 赞助支持 | 关于页显示微信 / 支付宝收款码 |
| 检查更新 | 关于页「检查更新」：比对 GitHub 最新 Release，下载安装包并静默升级 |

## 更新

设置 → 关于 → **检查更新**：

1. 读取 GlassDesk-Plus 仓库最新的 Release（tag `vX.Y.Z`）
2. 若有新版本，下载其中的安装包
3. 运行安装包静默升级（自动关闭并重启 GlassDesk）

发布新版本时：把 tag 设为 `vX.Y.Z`，Release 附带安装包 `GlassDesk-*-Setup.exe` 即可。

> 网络受限（DNS 污染）时，检查更新会自动改用 GitHub 已知 IP 直连。

## 作者与致谢

- 原始程序：**GlassDesk**，作者 [everlosttime](https://github.com/everlosttime/glassdesk)，Apache-2.0
- 维护与发布：**喜羊羊（远程电脑技术支持）**
- 抖音账号：**喜羊羊（远程电脑技术支持）**
- GitHub：<https://github.com/Li-Jiayang85>

## 赞助

软件免费；如果它帮到了你，可以自愿赞助，金额随意。

| 微信 | 支付宝 |
|---|---|
| ![微信](assets/wechat.jpg) | ![支付宝](assets/alipay.jpg) |

## 技术说明

- 增强组件通过 .NET 启动钩子（`DOTNET_STARTUP_HOOKS`）加载，**不修改安装目录里的任何文件**。
- 钩子只对 `GlassDesk.exe` 生效，其他 .NET 程序加载后立即返回，不做任何事。
- 依赖原版设置窗口的控件名/文案（`SettingsPageList`、`SettingsScroll`、`SearchBox`、`NextPage` 等）。
  原版升级到改动这些结构的版本时，部分功能可能失效，`patch.log` 会记录匹配结果。
- 卸载：删除 `%LOCALAPPDATA%\GlassDeskPlusPatch`，并清除用户环境变量 `DOTNET_STARTUP_HOOKS`。

## 许可

原版 GlassDesk 遵循 Apache-2.0；本增强部分的代码见 `src/` 与 `setup/`，同样以 Apache-2.0 发布。
