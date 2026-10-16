using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace GlassDesk.Dock;

internal enum MediaPlayerState
{
    None,
    Playing,
    Paused,
    Other,
}

internal sealed class MediaSessionSnapshot
{
    public bool HasSession { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public MediaPlayerState State { get; set; } = MediaPlayerState.None;

    /// <summary>专辑名（SMTC 的 AlbumTitle）。</summary>
    public string Album { get; set; } = "";

    /// <summary>专辑封面。随曲目变化，取到后一直持有；没有封面时为 null。</summary>
    public BitmapImage? Cover { get; set; }
}

public partial class DockWindow
{
    private readonly MediaSessionSnapshot _media = new();
    private bool _mediaFetching;
    private GlobalSystemMediaTransportControlsSession? _mediaSession;

    /// <summary>当前封面所属的曲目键（标题|艺人|专辑），用来避免每次刷新都重取封面。</summary>
    private string _mediaCoverKey = "";

    /// <summary>
    /// 把 SMTC 的封面缩略图解码成可用的位图。
    /// </summary>
    /// <remarks>
    /// 关键点是 <c>CacheOption = OnLoad</c>：缩略图给的是一个**流**，只有在流还开着的时候
    /// 解码完成才能拿到像素；否则流一关闭，图就变成空白。
    /// <c>DecodePixelWidth</c> 按卡片里的显示尺寸解码（卡片只有 72 DIP），避免为一张小图
    /// 解出上千像素的位图白占内存。任何失败都返回 null，卡片会自行隐藏封面区域。
    /// </remarks>
    private static async Task<BitmapImage?> ReadCoverAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var stream = await reference.OpenReadAsync();
            using var reader = stream.AsStreamForRead();
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 144;
            image.StreamSource = reader;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>窗口是否已关闭。关闭后不再碰 UI，也不再往已经停掉的调度器上排队。</summary>
    private bool _windowClosed;

    /// <summary>
    /// 音频按钮的统一入口：把命令包起来，绝不让异常逃进事件处理器。
    /// </summary>
    /// <remarks>
    /// 三个按钮的处理器是 <c>async void</c>（WPF 的 Click 只能这样）。这种处理器里
    /// 逃出的异常没有 Task 承接，会直接抛到调度器上结束进程——而媒体会话随时可能
    /// 因为播放器退出、切歌、设备变化而失效，所以这里是必需的兜底，不是防御性冗余。
    /// </remarks>
    private static async Task RunMediaCommandAsync(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>刷新系统媒体会话快照（WinRT SMTC）。无会话/异常一律降级为"当前无播放"，不抛异常。</summary>
    private async Task RefreshMediaSessionAsync()
    {
        if (_mediaFetching || _windowClosed) return;
        _mediaFetching = true;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_windowClosed) return;
            var session = manager.GetCurrentSession();
            if (session is null)
            {
                _mediaSession = null;
                SetMediaNone();
                return;
            }
            _mediaSession = session;
            var props = await session.TryGetMediaPropertiesAsync();
            if (_windowClosed) return;
            _media.HasSession = true;
            _media.Title = props?.Title ?? "";
            _media.Artist = props?.Artist ?? "";
            _media.Album = props?.AlbumTitle ?? "";

            // 封面只在该曲目第一次出现时取：Thumbnail 打开的是流，每次刷新都重取既慢又浪费。
            // 用标题/艺人/专辑当键，切歌时自然失效。
            var coverKey = $"{_media.Title}|{_media.Artist}|{_media.Album}";
            if (!string.Equals(coverKey, _mediaCoverKey, StringComparison.Ordinal))
            {
                _mediaCoverKey = coverKey;
                _media.Cover = await ReadCoverAsync(props?.Thumbnail);
            }
            _media.State = session.GetPlaybackInfo()?.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlayerState.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlayerState.Paused,
                _ => MediaPlayerState.Other,
            };
        }
        catch (Exception)
        {
            _mediaSession = null;
            SetMediaNone();
        }
        finally
        {
            _mediaFetching = false;
            // UpdateMediaButtons 会碰 UI，而这里在 finally 里：一旦让它抛出，
            // 异常就会穿过本方法到达调用方（定时器 Tick 或 async void 点击），
            // 那就是进程崩溃。所以这一层保护是必需的。
            try
            {
                UpdateMediaButtons();
            }
            catch (Exception)
            {
            }
        }
    }

    private void SetMediaNone()
    {
        _media.HasSession = false;
        _media.Title = "";
        _media.Artist = "";
        _media.State = MediaPlayerState.None;
    }

    private async Task MediaPlayPauseAsync()
    {
        if (_mediaSession is null) return;
        try
        {
            if (_media.State == MediaPlayerState.Playing)
            {
                await _mediaSession.TryPauseAsync();
            }
            else
            {
                await _mediaSession.TryPlayAsync();
            }
        }
        catch (Exception)
        {
        }
        if (!_windowClosed) await RefreshMediaSessionAsync();
    }

    private async Task MediaNextAsync()
    {
        if (_mediaSession is null) return;
        try
        {
            await _mediaSession.TrySkipNextAsync();
        }
        catch (Exception)
        {
        }
        if (!_windowClosed) await RefreshMediaSessionAsync();
    }

    private async Task MediaPreviousAsync()
    {
        if (_mediaSession is null) return;
        try
        {
            await _mediaSession.TrySkipPreviousAsync();
        }
        catch (Exception)
        {
        }
        if (!_windowClosed) await RefreshMediaSessionAsync();
    }
}
