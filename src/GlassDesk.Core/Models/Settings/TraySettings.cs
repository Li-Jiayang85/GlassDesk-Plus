namespace GlassDesk.Core.Models.Settings;

/// <summary>通知区域图标的显示与外观。</summary>
/// <remarks>
/// 图标文件刻意沿用应用库那套「托管自定义图标」：只记录 <c>icons/custom-*</c> 里的文件名，
/// 也就是用户此前给某个应用换过的那张图。好处有三点：不用再引入一套图标复制逻辑；
/// 备份里的自定义图标清单已经把这类文件算作必须带走的数据，托盘选中的那张自然一起走；
/// 数据目录整体搬家时，随设置存的文件名依然指向新目录下的同一张图。
/// </remarks>
public sealed class TraySettings
{
    /// <summary>是否在通知区域显示图标。关掉后托盘菜单也随之不可达，退出入口只剩窗口内的入口。</summary>
    public bool ShowIcon { get; set; } = true;

    /// <summary>
    /// 用户选中的自定义图标文件名（位于数据目录的 <c>icons/</c> 下）。
    /// </summary>
    /// <remarks>
    /// 只存文件名而不是绝对路径，所以这里永远不含目录分隔符。为空表示用内置品牌图标。
    /// 文件缺失时渲染层会回退到内置图标，不会留下一个空白的托盘位置。
    /// </remarks>
    public string? CustomIconFileName { get; set; }
}
