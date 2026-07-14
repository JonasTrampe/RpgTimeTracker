using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using RpgTimeTracker.Models;
using RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Network;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Network;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Models.Theming;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Theming;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;
using Serilog;

namespace RpgTimeTracker.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IPlayerDisplayContext
{
    // ==================== Event medium (timer/alarm/interval trigger image/video) ====================

    private void TriggerEventMedia(TriggerMediaConfig config)
    {
        if (!config.HasMedia || string.IsNullOrEmpty(config.Path) || !File.Exists(config.Path)) return;

        _ = SendTriggerMediaAsync(config);
    }

    private async Task SendTriggerMediaAsync(TriggerMediaConfig config)
    {
        var path = config.Path!;
        Log.Information(
            "Event medium triggered: {Path} (Loop={Loop}, Fullscreen={Fullscreen}, PauseClock={PauseClock})",
            path, config.Loop, config.Fullscreen, config.PauseClockDuringVideo);
        RecordSessionEvent(string.Format(LocalizationService.Get("MainWindowViewModel.Events.EventMediumTriggered"), config.FileName));

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Event medium could not be read ({Path})", path);
            MediaErrorMessage = string.Format(LocalizationService.Get("MainWindowViewModel.Errors.EventMediumCouldNotBeSent"), ex.Message);
            return;
        }

        var fileName = string.IsNullOrWhiteSpace(config.FileName) ? Path.GetFileName(path) : config.FileName;
        MediaTypeHelper.TryGetKind(path, out _, out var mimeType);
        var header = new MediaHeaderDto
        {
            MediaId = Guid.NewGuid().ToString("N"),
            Kind = config.Kind.ToString(),
            FileName = fileName,
            MimeType = mimeType,
            Loop = config.Loop
        };

        if (config.Kind == MediaKind.Audio)
        {
            // Sounds are decoupled from the "current medium" slot (see the SendSoundAsync comment) -
            // no SetCurrentMediaStatus/BeginVideoTracking, otherwise a sound trigger would
            // replace a currently shown image/video or cut off its end tracking. Instead
            // bound to this event via _triggerSoundMediaIds, so StopMediaIfTriggeredBy also ends it
            // when the triggering timer/alarm/interval is reset.
            header.Layer = MediaHeaderDto.LayerSound;
            _triggerSoundMediaIds[config] = header.MediaId;
            await SendSoundAsync(header, bytes, path, false);
        }
        else
        {
            SetCurrentMediaStatus(config.Kind, path, fileName, false);
            _activeTriggerMediaSource = config;

            await _playerServer.PublishMediaAsync(header, bytes);
            BeginVideoTracking(header, path, config.PauseClockDuringVideo);
        }

        if (config.Fullscreen) SetDisplayFullscreen(true);
    }

    /// <summary>
    ///     Central send path for EVERY sound (ad-hoc, library, event trigger, item sound): sends it
    ///     over the network, optionally previews it locally at the GM, and tracks it in the "currently
    ///     playing" panel. localPath/deleteLocalAfterPlayback only control the local GM preview (see
    ///     PlayLocalSoundIfNeeded); the network send always runs independently of that.
    /// </summary>
    private async Task SendSoundAsync(MediaHeaderDto header, byte[] bytes, string localPath,
        bool deleteLocalAfterPlayback)
    {
        await _playerServer.PublishMediaAsync(header, bytes);
        PlayLocalSoundIfNeeded(header, localPath, deleteLocalAfterPlayback);
        AddActiveSound(header, bytes, localPath);
    }

    private void AddActiveSound(MediaHeaderDto header, byte[] bytes, string localPath)
    {
        var sourceItem = FindSoundLibraryItemByPath(localPath);
        var entry = new ActiveSoundViewModel(
            header.MediaId,
            header.FileName,
            sourceItem?.Icon ?? VisualItemHelper.IconTimer,
            header.Loop,
            header.Volume,
            sound => StopSound(sound.MediaId),
            (sound, volume) =>
            {
                if (_activeLocalSoundPlayers.TryGetValue(sound.MediaId, out var localPlayer))
                    localPlayer.Volume = volume;
                _ = _playerServer.PublishSetSoundVolumeAsync(sound.MediaId, volume);
            },
            sourceItem);

        ActivePlayingSounds.Add(entry);
        _activeSoundData[header.MediaId] = (header, bytes, DateTime.UtcNow, null);
        OnPropertyChanged(nameof(HasNoActivePlayingSounds));

        _ = UpdateActiveSoundDurationAsync(header.MediaId, localPath);
    }

    /// <summary>Resolves this sound's duration (async VLC parse, same helper used for the video
    ///     fallback-timeout estimate) and records it in _activeSoundData - needed to decide
    ///     whether a resend (re-enabling Sound routing for a client) is worth an estimated seek
    ///     (see ResendActiveSoundsToClient/SoundSeekThresholdMs). A no-op if the sound already
    ///     ended (and was removed) before the parse resolves.</summary>
    private async Task UpdateActiveSoundDurationAsync(string mediaId, string localPath)
    {
        var duration = await TryGetMediaDurationAsync(localPath);
        if (duration is null) return;
        if (!_activeSoundData.TryGetValue(mediaId, out var data)) return;

        _activeSoundData[mediaId] = (data.Header, data.FileBytes, data.StartedAtUtc, (long)duration.Value.TotalMilliseconds);
    }

    /// <summary>
    ///     Removes a sound from the panel + all event/item associations - the common point
    ///     for every way a sound can end (natural end, manual stop, "stop all").
    /// </summary>
    private void RemoveActiveSound(string mediaId)
    {
        for (var i = ActivePlayingSounds.Count - 1; i >= 0; i--)
            if (ActivePlayingSounds[i].MediaId == mediaId)
            {
                ActivePlayingSounds[i].Dispose();
                ActivePlayingSounds.RemoveAt(i);
            }

        _activeSoundData.Remove(mediaId);

        OnPropertyChanged(nameof(HasNoActivePlayingSounds));

        foreach (var key in _triggerSoundMediaIds.Where(kv => kv.Value == mediaId).Select(kv => kv.Key).ToList())
            _triggerSoundMediaIds.Remove(key);
        foreach (var key in _itemSoundMediaIds.Where(kv => kv.Value == mediaId).Select(kv => kv.Key).ToList())
            _itemSoundMediaIds.Remove(key);
    }

    private MediaLibraryItemViewModel? FindMediaLibraryItemByPath(string localPath)
    {
        return MediaLibrary.FirstOrDefault(item =>
            string.Equals(item.LocalPath, localPath, StringComparison.OrdinalIgnoreCase));
    }

    private SoundLibraryItemViewModel? FindSoundLibraryItemByPath(string localPath)
    {
        return SoundLibrary.FirstOrDefault(item =>
            string.Equals(item.LocalPath, localPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     GM-side stop of a single sound: ends it on all player clients
    ///     (media.stopSound) AND any local GM preview, then cleans up the panel.
    /// </summary>
    private void StopSound(string mediaId)
    {
        _ = _playerServer.PublishStopSoundAsync(mediaId);

        if (_activeLocalSoundPlayers.Remove(mediaId, out var player))
        {
            player.Stop();
            player.Dispose();
            _localSoundRepeatsRemaining.Remove(mediaId);
            if (_localSoundCleanupPaths.Remove(mediaId, out var path)) DeleteFileQuietly(path);
        }

        RemoveActiveSound(mediaId);
    }

    [RelayCommand]
    private void StopAllSounds()
    {
        foreach (var sound in ActivePlayingSounds.ToList()) StopSound(sound.MediaId);
    }

    /// <summary>Stops every currently-active sound on exactly one client window (its Sound
    ///     routing was just turned off) - unlike StopSound, this must NOT touch the Host's own
    ///     local preview or the ActivePlayingSounds panel, since the sound keeps playing for
    ///     every other still-enabled window.</summary>
    private void StopAllSoundsForClient(string remoteEndpoint)
    {
        foreach (var sound in ActivePlayingSounds)
            _ = _playerServer.PublishStopSoundToClientAsync(sound.MediaId, remoteEndpoint);
    }

    /// <summary>Resends every currently-active sound to exactly one client window (its Sound
    ///     routing was just turned back on) - the mirror image of StopAllSoundsForClient, using
    ///     the header+bytes cached in _activeSoundData since this service doesn't keep the file
    ///     bytes around after the original send. Sounds longer than SoundSeekThresholdMs get an
    ///     estimated mid-playback seek (elapsed wall-clock time since the sound started, wrapped
    ///     into its own trimmed duration so a looping/repeating sound seeks within its current
    ///     cycle rather than some multiple of it) - short one-off effects just restart from 0,
    ///     since seeking into them isn't meaningful and the duration may not even be known yet
    ///     (see UpdateActiveSoundDurationAsync).</summary>
    private void ResendActiveSoundsToClient(string remoteEndpoint)
    {
        var thresholdMs = SoundSeekThresholdMs;
        foreach (var (header, bytes, startedAtUtc, durationMs) in _activeSoundData.Values)
        {
            var toSend = header;
            if (durationMs is { } duration && duration > thresholdMs)
            {
                var effectiveDurationMs = (header.TrimEndMs > 0 ? header.TrimEndMs : duration) - header.TrimStartMs;
                if (effectiveDurationMs > 0)
                {
                    var elapsedMs = Math.Max(0, (long)(DateTime.UtcNow - startedAtUtc).TotalMilliseconds);
                    var positionMs = header.TrimStartMs + elapsedMs % effectiveDurationMs;
                    toSend = header.CloneWithSeek(positionMs);
                }
            }

            _ = _playerServer.PublishMediaToClientAsync(toSend, bytes, remoteEndpoint);
        }
    }

    /// <summary>Stops the Host's own local sound preview players only (see OnPlaySoundLocallyChanged) -
    ///     unlike StopSound, this must NOT touch remote clients or the ActivePlayingSounds panel,
    ///     since the sound keeps playing for every connected player regardless.</summary>
    private void StopLocalSoundPreviewOnly()
    {
        foreach (var (mediaId, player) in _activeLocalSoundPlayers.ToList())
        {
            player.Stop();
            player.Dispose();
            _localSoundRepeatsRemaining.Remove(mediaId);
            if (_localSoundCleanupPaths.Remove(mediaId, out var path)) DeleteFileQuietly(path);
        }

        _activeLocalSoundPlayers.Clear();
    }

    private void PlayLocalSoundIfNeeded(MediaHeaderDto header, string localPath, bool deleteAfterPlayback)
    {
        // Plays locally whenever the player window is open, same as local media/map preview -
        // regardless of connected clients, so the GM hears what players hear - unless the GM
        // has turned off Sound for their own "Host (local)" window (see PlaySoundLocally).
        if (!IsPlayerWindowOpen || !PlaySoundLocally)
        {
            if (deleteAfterPlayback) DeleteFileQuietly(localPath);
            return;
        }

        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null)
        {
            if (deleteAfterPlayback) DeleteFileQuietly(localPath);
            return;
        }

        try
        {
            var player = new MediaPlayer(libVlc);
            var mediaId = header.MediaId;
            player.EndReached += (_, _) => Dispatcher.UIThread.Post(() => OnLocalSoundEnded(mediaId));

            _activeLocalSoundPlayers[mediaId] = player;
            _localSoundRepeatsRemaining[mediaId] = header.Loop ? -1 : Math.Max(1, header.RepeatCount);
            if (deleteAfterPlayback) _localSoundCleanupPaths[mediaId] = localPath;

            using var media = new Media(libVlc, localPath);
            VlcMediaService.ApplySoundTrim(media, header.TrimStartMs, header.TrimEndMs);
            player.Play(media);
            player.Volume = Math.Clamp(header.Volume, 0, 100);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sound could not be played locally: {Path}", localPath);
            if (deleteAfterPlayback) DeleteFileQuietly(localPath);
        }
    }

    private void OnLocalSoundEnded(string mediaId)
    {
        if (!_activeLocalSoundPlayers.TryGetValue(mediaId, out var player)) return;

        var remaining = _localSoundRepeatsRemaining.GetValueOrDefault(mediaId, 0);
        if (remaining < 0 || remaining > 1)
        {
            if (remaining > 0) _localSoundRepeatsRemaining[mediaId] = remaining - 1;
            player.Stop();
            player.Play();
            return;
        }

        _activeLocalSoundPlayers.Remove(mediaId);
        _localSoundRepeatsRemaining.Remove(mediaId);
        player.Stop();
        player.Dispose();

        if (_localSoundCleanupPaths.Remove(mediaId, out var path)) DeleteFileQuietly(path);

        // Only relevant if the sound was NOT also running remotely (otherwise the
        // ClientReportedPlaybackEnded report already removes the panel entry) - RemoveActiveSound
        // is idempotent, so a duplicate call doesn't hurt.
        RemoveActiveSound(mediaId);
    }

    /// <summary>
    ///     When the last player client loses connection (or the server stops), all
    ///     purely remotely running sounds become unreachable - the client does stop them itself
    ///     (see ClientMainWindowViewModel.StopAllSounds), but the GM panel must reflect that.
    /// </summary>
    private void ClearRemoteOnlyActiveSounds()
    {
        foreach (var sound in ActivePlayingSounds.Where(s => !_activeLocalSoundPlayers.ContainsKey(s.MediaId)).ToList())
            RemoveActiveSound(sound.MediaId);
    }

    /// <summary>
    ///     Closes the currently shown media display, but ONLY if it was triggered by exactly this
    ///     timer/alarm/interval - e.g. when a timer whose expiry just showed an
    ///     image/video is reset. A medium shown via ad-hoc sending or from
    ///     the library remains unaffected by this.
    /// </summary>
    private void StopMediaIfTriggeredBy(TriggerMediaConfig config)
    {
        if (ReferenceEquals(_activeTriggerMediaSource, config))
        {
            ClearMediaCommand.Execute(null);
            ResumeGalleryAfterEventMedia();
        }

        if (_triggerSoundMediaIds.Remove(config, out var mediaId)) StopSound(mediaId);
    }

    /// <summary>
    ///     After closing an event medium (timer/alarm/interval reset, see above):
    ///     automatically shows the most recently active gallery item again instead of leaving the
    ///     screen empty - event media only temporarily interrupt the gallery (taking precedence), see
    ///     design-decisions.md. A no-op if the item has since been removed from the gallery.
    /// </summary>
    private void ResumeGalleryAfterEventMedia()
    {
        if (_currentGalleryMediaId is null) return;

        var item = SentMediaItems.FirstOrDefault(i => i.MediaId == _currentGalleryMediaId);
        if (item is null) return;

        SetCurrentMediaStatus(item.Kind, item.LocalPath, item.Name, false);
        _ = _playerServer.PublishHighlightAsync(item.MediaId);
    }

    private void BeginVideoTracking(MediaHeaderDto header, string localPath, bool pauseClockUntilEnd)
    {
        CancelPendingVideoTracking();

        // VIDEO ONLY: sounds are handled completely independently of the image/video "current
        // medium" slot (see PlayLocalSoundIfNeeded) - otherwise a sound ending here would
        // incorrectly trigger ClearMediaCommand and close a currently shown image/video.
        if (header.Kind != MediaHeaderDto.MediaKindVideo || header.Loop) return;

        _pendingVideoMediaId = header.MediaId;
        _pendingVideoPauseClock = pauseClockUntilEnd;

        if (pauseClockUntilEnd && _clock.IsRunning)
        {
            _clock.Pause();
            IsClockRunning = _clock.IsRunning;
            _ = _playerServer.PublishClockStoppedAsync();
        }

        var cts = new CancellationTokenSource();
        _pendingVideoFallbackCts = cts;
        _ = RunFallbackTimeoutAsync(header.MediaId, localPath, cts.Token);
    }

    private void CancelPendingVideoTracking()
    {
        _pendingVideoFallbackCts?.Cancel();
        _pendingVideoFallbackCts?.Dispose();
        _pendingVideoFallbackCts = null;
        _pendingVideoMediaId = null;
    }

    /// <summary>
    ///     Safety net: if no client sends a real playbackEnded report, don't stay paused/open
    ///     forever.
    /// </summary>
    private async Task RunFallbackTimeoutAsync(string mediaId, string localPath, CancellationToken token)
    {
        var duration = await TryGetMediaDurationAsync(localPath) ?? TimeSpan.FromMinutes(10);
        // Buffer beyond the estimated duration so that in the normal case the real
        // client report wins first (it also accounts for network/buffering time).
        var fallback = duration + TimeSpan.FromSeconds(15);

        try
        {
            await Task.Delay(fallback, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ResolvePendingVideo(mediaId);
    }

    private void UpdateConnectedClients(IReadOnlyList<ConnectedClientInfo> clients)
    {
        ConnectedClientItems.Clear();
        foreach (var info in clients)
            ConnectedClientItems.Add(new ConnectedClientItemViewModel(info, DisconnectClientRequested,
                OnClientMusicEnabledChanged, OnClientSoundEnabledChanged, OnClientImageEnabledChanged,
                OnClientVideoEnabledChanged, OnClientMapEnabledChanged));
        OnPropertyChanged(nameof(HasNoConnectedClients));
    }

    private void DisconnectClientRequested(ConnectedClientItemViewModel item)
    {
        _playerServer.DisconnectClient(item.RemoteEndpoint);
    }

    private void OnClientMusicEnabledChanged(ConnectedClientItemViewModel item, bool enabled)
    {
        _playerServer.SetClientMusicEnabled(item.RemoteEndpoint, enabled);
    }

    private void OnClientSoundEnabledChanged(ConnectedClientItemViewModel item, bool enabled)
    {
        _playerServer.SetClientSoundEnabled(item.RemoteEndpoint, enabled);
    }

    private void OnClientImageEnabledChanged(ConnectedClientItemViewModel item, bool enabled)
    {
        _playerServer.SetClientImageEnabled(item.RemoteEndpoint, enabled);
    }

    private void OnClientVideoEnabledChanged(ConnectedClientItemViewModel item, bool enabled)
    {
        _playerServer.SetClientVideoEnabled(item.RemoteEndpoint, enabled);
    }

    private void OnClientMapEnabledChanged(ConnectedClientItemViewModel item, bool enabled)
    {
        _playerServer.SetClientMapEnabled(item.RemoteEndpoint, enabled);
    }

    private void ResolvePendingVideo(string mediaId)
    {
        if (_pendingVideoMediaId != mediaId) return;

        var pauseClock = _pendingVideoPauseClock;
        CancelPendingVideoTracking();

        if (pauseClock && !_clock.IsRunning)
        {
            _clock.Start();
            IsClockRunning = _clock.IsRunning;
            _ = _playerServer.PublishClockStartedAsync();
        }

        ClearMediaCommand.Execute(null);
    }

    private static async Task<TimeSpan?> TryGetMediaDurationAsync(string path)
    {
        if (!VlcMediaService.TryGetLibVlc(out var libVlc) || libVlc is null) return null;

        try
        {
            using var media = new Media(libVlc, path);
            var parsed = await media.Parse();
            return parsed == MediaParsedStatus.Done && media.Duration > 0
                ? TimeSpan.FromMilliseconds(media.Duration)
                : null;
        }
        catch
        {
            return null;
        }
    }

    // TimelineItems wraps every timer/alarm/interval 1:1 (see AddTimelineItem) and
    // already cascades RefreshBlinkState() to the respective wrapped VM - a separate
    // iteration over Timers/Alarms/IntervalEvents would update every object twice.
    private void RefreshBlinkStates()
    {
        foreach (var item in TimelineItems) item.RefreshBlinkState();
    }

    private static string FormatGameTime(DateTime time)
    {
        return time.ToString("dddd, dd.MM.yyyy — HH:mm:ss", LocalizationService.Culture);
    }

}
