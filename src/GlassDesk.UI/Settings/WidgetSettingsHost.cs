using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.UI.Settings;

/// <summary>
/// 小组件设置的进程级唯一事实来源与变更通知口。
/// </summary>
/// <remarks>
/// 仿 <see cref="DockSettingsHost"/> 的先例：生效中的小组件设置只有一个，
/// Dock 窗口与设置界面必须看到同一份。设置保存后通过 <see cref="Update"/> 推给
/// Dock，Dock 不得自行轮询设置文件。
/// </remarks>
public sealed class WidgetSettingsHost
{
    private readonly Dispatcher _dispatcher;

    public WidgetSettingsHost(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <summary>本进程唯一的小组件设置宿主。</summary>
    public static WidgetSettingsHost? Current { get; private set; }

    /// <summary>创建并安装进程级实例。</summary>
    public static WidgetSettingsHost Initialize(Dispatcher dispatcher)
    {
        Current = new WidgetSettingsHost(dispatcher);
        return Current;
    }

    /// <summary>小组件总开关。</summary>
    public bool Enabled { get; private set; } = true;

    /// <summary>当前生效的各小组件设置快照。只在 <see cref="Update"/> 时整体替换。</summary>
    public IReadOnlyList<WidgetSettings> Items { get; private set; } = [];

    /// <summary>设置变化后在 UI 线程上触发。</summary>
    public event EventHandler? Changed;

    /// <summary>采用新的设置并通知每个监听者。</summary>
    public void Update(bool enabled, IReadOnlyList<WidgetSettings> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Enabled = enabled;
        Items = items;
        NotifyChanged();
    }

    /// <summary>设置对象被就地修改后调用：不替换引用，只补一次通知。</summary>
    public void Refresh() => NotifyChanged();

    private void NotifyChanged()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.BeginInvoke(NotifyChanged);
        }
    }
}
