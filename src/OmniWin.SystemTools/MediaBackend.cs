using Windows.Media.Control;

namespace OmniWin.SystemTools;

internal enum MediaPlayback
{
    Playing,
    Paused,
    Stopped,
    Other,
}

internal interface IMediaBackend
{
    /// <summary>State of the current media session, or null when no app has one.</summary>
    Task<MediaPlayback?> GetStateAsync(CancellationToken ct);

    /// <summary>Sends play_pause / next / previous / stop to the current session. False when there is none or it refused.</summary>
    Task<bool> SendAsync(string action, CancellationToken ct);
}

/// <summary>Windows media transport controls (the same sessions the volume flyout shows). No key presses are sent.</summary>
internal sealed class MediaSessionBackend : IMediaBackend
{
    public async Task<MediaPlayback?> GetStateAsync(CancellationToken ct)
    {
        var session = await CurrentAsync(ct).ConfigureAwait(false);
        if (session is null) return null;
        return session.GetPlaybackInfo().PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlayback.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlayback.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlayback.Stopped,
            _ => MediaPlayback.Other,
        };
    }

    public async Task<bool> SendAsync(string action, CancellationToken ct)
    {
        var session = await CurrentAsync(ct).ConfigureAwait(false);
        if (session is null) return false;
        return action switch
        {
            "play_pause" => await session.TryTogglePlayPauseAsync().AsTask(ct).ConfigureAwait(false),
            "next" => await session.TrySkipNextAsync().AsTask(ct).ConfigureAwait(false),
            "previous" => await session.TrySkipPreviousAsync().AsTask(ct).ConfigureAwait(false),
            "stop" => await session.TryStopAsync().AsTask(ct).ConfigureAwait(false),
            _ => false,
        };
    }

    private static async Task<GlobalSystemMediaTransportControlsSession?> CurrentAsync(CancellationToken ct)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct).ConfigureAwait(false);
        return manager.GetCurrentSession();
    }
}
