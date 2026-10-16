using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassDesk.UI.Theming;

namespace GlassDesk.Launcher;

internal sealed class LauncherAppTileButton : Button
{
    /// <summary>该磁贴在启动台多选中是否被勾选。</summary>
    /// <remarks>
    /// 做成依赖属性而不是去模板里找那个角标：磁贴刚 <c>new</c> 出来时模板还没应用，
    /// 按名字查模板子元素会静默失败。绑定则会在模板应用时自动接上。
    /// </remarks>
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(LauncherAppTileButton), new PropertyMetadata(false));

    private readonly ScaleTransform _iconScale = new(1, 1);
    private readonly FrameworkElement _iconHost;

    public LauncherAppTileButton(FrameworkElement iconHost)
    {
        _iconHost = iconHost;
        iconHost.RenderTransformOrigin = new Point(.5, .5);
        iconHost.RenderTransform = _iconScale;
        Unloaded += (_, _) => ResetIconScale();
    }

    /// <summary>多选勾选状态；由 <c>LauncherWindow</c> 写入，模板绑定读取。</summary>
    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_iconHost is null) return;
        if (e.Property == IsVisibleProperty && e.NewValue is false)
            ResetIconScale();
        else if (e.Property == IsPressedProperty)
        {
            if (ThemeService.Current?.AnimationsEnabled == false)
            {
                ResetIconScale();
                return;
            }
            var motion = new DoubleAnimation(IsPressed ? .97 : 1,
                TimeSpan.FromMilliseconds(IsPressed ? 60 : 180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            if (!IsPressed)
                motion.Completed += (_, _) => { if (!IsPressed) ResetIconScale(); };
            _iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
            _iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
        }
    }

    private void ResetIconScale()
    {
        _iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _iconScale.ScaleX = _iconScale.ScaleY = 1;
    }
}
