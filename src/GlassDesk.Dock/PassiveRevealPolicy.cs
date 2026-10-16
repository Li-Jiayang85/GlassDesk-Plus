namespace GlassDesk.Dock;
public static class PassiveRevealPolicy
{
    public static bool CanReveal(bool inHotzone,bool edgeEnabled,bool fullscreen,bool suppressFullscreen,bool covered,bool blockCovered)
        => inHotzone && edgeEnabled && !(fullscreen && suppressFullscreen) && !(covered && blockCovered);

    /// <summary>
    /// 遮挡或全屏期间是否允许鼠标移到 Dock 原位置唤出。
    /// </summary>
    /// <remarks>
    /// **一律不允许。** 原先鼠标碰到屏幕下边缘就能把程序坞拉出来，在全屏游戏/视频里
    /// 会直接盖在画面上；用户明确要求去掉这个行为，所以这里恒为 false。
    /// 参数保留是为了不改动调用方与既有测试的签名。
    /// </remarks>
    public static bool CanRevealDuringSuppression(bool pointerOverDock, bool revealLocked)
    {
        _ = pointerOverDock;
        _ = revealLocked;
        return false;
    }
}
