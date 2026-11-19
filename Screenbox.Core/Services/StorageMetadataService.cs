using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.MediaSources;
using Screenbox.Core.Models;
using Screenbox.Core.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using IMediaSource = Screenbox.Core.MediaSources.IMediaSource;

namespace Screenbox.Core.Services;

/// <summary>
/// Fast source metadata service backed by Windows Storage file properties (WinRT),
/// with LibVLC fallback when container duration is unavailable via WinRT.
/// </summary>
public sealed class StorageMetadataService : ISourceMetadataService
{
    private readonly PlayerContext? _playerContext;
    private readonly IPlayerService? _playerService;
    private readonly IPlaybackMetadataService? _playbackMetadataService;
    private readonly IMediaSourceFactory? _mediaSourceFactory;
    private readonly ILogger<StorageMetadataService> _logger;

    public StorageMetadataService(ILogger<StorageMetadataService> logger)
        : this(null, null, null, null, logger)
    {
    }

    [ActivatorUtilitiesConstructor]
    public StorageMetadataService(
        PlayerContext? playerContext,
        IPlayerService? playerService,
        IPlaybackMetadataService? playbackMetadataService,
        IMediaSourceFactory? mediaSourceFactory,
        ILogger<StorageMetadataService> logger)
    {
        _playerContext = playerContext;
        _playerService = playerService;
        _playbackMetadataService = playbackMetadataService;
        _mediaSourceFactory = mediaSourceFactory;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves Windows Storage properties from the specified <paramref name="file"/>.
    /// </summary>
    public async Task<MediaInfo?> GetMediaInfoAsync(
        StorageFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        MediaPlaybackType mediaType = FilesHelpers.GetMediaTypeForFile(file);
        if (!file.IsAvailable)
        {
            return new MediaInfo(mediaType);
        }

        MediaInfo? info = null;
        try
        {
            BasicProperties basicProperties = await file.GetBasicPropertiesAsync();
            cancellationToken.ThrowIfCancellationRequested();

            switch (mediaType)
            {
                case MediaPlaybackType.Video:
                    VideoProperties videoProperties = await file.Properties.GetVideoPropertiesAsync();
                    info = new MediaInfo(basicProperties, videoProperties);
                    break;

                case MediaPlaybackType.Music:
                    MusicProperties musicProperties = await file.Properties.GetMusicPropertiesAsync();
                    info = new MediaInfo(basicProperties, musicProperties);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e) when (IsExpectedStoragePropertiesHResult(e.HResult))
        {
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load media metadata for file type '{FileType}'.", file.FileType);
        }

        info ??= new MediaInfo(mediaType);

        if (HasMissingDuration(info) && _mediaSourceFactory is not null)
        {
            IMediaSource source = _mediaSourceFactory.Create(file);
            MediaInfo? vlcInfo = await TryParseWithLibVlcAsync(source, cancellationToken);
            if (vlcInfo is not null)
            {
                vlcInfo.MediaType = mediaType;
                info = info.MergeSelective(vlcInfo);
            }
        }

        return info;
    }

    /// <summary>
    /// Retrieves Windows Storage properties from the specified <paramref name="fileSource"/>.
    /// </summary>
    public async Task<MediaInfo?> GetMediaInfoAsync(
        IFileMediaSource fileSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSource);

        if (await fileSource.GetStorageFileAsync() is not { } file)
        {
            return null;
        }

        return await GetMediaInfoAsync(file, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<MediaInfo?> GetMediaInfoAsync(
        IMediaSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source is IFileMediaSource fileSource)
        {
            return await GetMediaInfoAsync(fileSource, cancellationToken);
        }

        return null;
    }

    private static bool HasMissingDuration(MediaInfo info) =>
        info.MediaType switch
        {
            MediaPlaybackType.Video => info.VideoProperties.Duration <= TimeSpan.Zero,
            MediaPlaybackType.Music => info.MusicProperties.Duration <= TimeSpan.Zero,
            _ => false
        };

    private async Task<MediaInfo?> TryParseWithLibVlcAsync(IMediaSource source, CancellationToken cancellationToken)
    {
        IMediaPlayer? player = _playerContext?.MediaPlayer;
        if (player is null || _playerService is null || _playbackMetadataService is null)
        {
            return null;
        }

        IPlaybackItem? item = null;
        try
        {
            item = _playerService.CreatePlaybackItem(player, source, ":no-video");
            return await _playbackMetadataService.GetMediaInfoAsync(item, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LibVLC metadata fallback failed for '{Location}'.", source.Location);
            return null;
        }
        finally
        {
            if (item is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static bool IsExpectedStoragePropertiesHResult(int hresult)
    {
        const int RPC_S_SERVER_UNAVAILABLE = unchecked((int)0x800706BA);
        const int E_ILLEGAL_METHOD_CALL = unchecked((int)0x8000000E);
        const int ERROR_NOT_FOUND = unchecked((int)0x80070490);
        return hresult == RPC_S_SERVER_UNAVAILABLE
               || hresult == E_ILLEGAL_METHOD_CALL
               || hresult == ERROR_NOT_FOUND;
    }
}
