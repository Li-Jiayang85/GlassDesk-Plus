---
AIGC:
    Label: "1"
    ContentProducer: 001191440300708461136T1XGW3
    ProduceID: 009ff05bd225bf70810be5d9de6b8993_80656678c8a711f18b7f525400393706
    ReservedCode1: NK0UC7tx/djnQF9sz4mVJ5nYDhL29kRM3rnq9ZrM98Hygv9nztYWxQ9mMjRQqqlGw22Wxt5xJhjqWofFtdTRnZ3Bnh8jo+sku4QOJ4A7bMx7mLnxFWcZvRvVaZJO9QsHmwBpacLlaLGdeBBw8VyqwXh1hcuWT+FQvihS4H0br+fXEGGCK9wB6rtAmOg=
    ContentPropagator: 001191440300708461136T1XGW3
    PropagateID: 009ff05bd225bf70810be5d9de6b8993_80656678c8a711f18b7f525400393706
    ReservedCode2: NK0UC7tx/djnQF9sz4mVJ5nYDhL29kRM3rnq9ZrM98Hygv9nztYWxQ9mMjRQqqlGw22Wxt5xJhjqWofFtdTRnZ3Bnh8jo+sku4QOJ4A7bMx7mLnxFWcZvRvVaZJO9QsHmwBpacLlaLGdeBBw8VyqwXh1hcuWT+FQvihS4H0br+fXEGGCK9wB6rtAmOg=
---

# GlassDesk v15 开源文档

> GlassDesk 是一款面向 Windows 11 的自定义 Dock 栏美化工具，通过可配置的 Dock 栏替换、启动台、壁纸捕获等功能提升桌面交互体验。

## 版本变更：v15

### 改动摘要

将小组件图标栏从 Dock 栏底部单独的一行，改为与 Dock 栏其他按钮（启动台、此电脑、切换桌面图标、设置等）统一排列在同一行中。

### 具体改动

| 改动项 | 说明 |
|---|---|
| 布局调整 | 小组件图标（天气、音频、备忘录、待办等）不再独占 Dock 栏底部独立一行，而是与已有按钮并排显示在同一水平行上 |
| 视觉一致性 | 小组件图标保持与原有按钮相同的 48×64 尺寸、间距和对齐方式，视觉统一 |
| 交互不变 | 点击小组件图标仍在 Dock 窗口内部展开对应面板，交互逻辑未变 |

### 影响范围

- 仅涉及 `DockWindow.xaml` 和 `DockWindow.xaml.cs` 中的布局与初始化逻辑
- 小组件功能模块（天气、音频、备忘录、待办）的独立业务逻辑未受影响
- 所有小组件仍可通过 WidgetSettings 独立控制启用/禁用、尺寸和刷新频率

## 版本历史

| 版本 | 主要变更 |
|------|----------|
| v11 | 小组件 Popup 浮窗改为 Dock 内部面板；天气支持手动选城市；刷新频率 30 分钟；备忘录/待办可增删改查 |
| v12 | （基础版本） |
| v15 | 小组件图标栏并入 Dock 栏主行，取消独立行 |

## 环境信息

- **操作系统**：Windows 11
- **开发框架**：.NET 10.0 (WPF)
- **SDK 版本**：10.0.401 / 10.0.402
*（内容由AI生成，仅供参考）*
