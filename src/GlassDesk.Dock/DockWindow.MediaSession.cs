using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Media.Control;

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
}

public partial class DockWindow
{
    private readonly MediaSessionSnapshot _media = new();
    private bool _mediaFetching;
    private GlobalSystemMediaTransportControlsSession? _mediaSession;

    /// <summary>刷新系统媒体会话快照（WinRT SMTC）。无会话/异常一律降级为"当前无播放"，不抛异常。</summary>
    private async Task RefreshMediaSessionAsync()
    {
        if (_mediaFetching) return;
        _mediaFetching = true;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetCurrentSession();
            if (session is null)
            {
                _mediaSession = null;
                SetMediaNone();
                return;
            }
            _mediaSession = session;
            var props = await session.TryGetMediaPropertiesAsync();
            _media.HasSession = true;
            _media.Title = props?.Title ?? "";
            _media.Artist = props?.Artist ?? "";
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
            UpdateMediaButtons();
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
        await RefreshMediaSessionAsync();
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
        await RefreshMediaSessionAsync();
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
        await RefreshMediaSessionAsync();
    }
}
