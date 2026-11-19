using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Contexts;
using Screenbox.Core.MediaSources;
using Screenbox.Core.Playback;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.AccessCache;

namespace Screenbox.Core.Services;

public sealed partial class PlayerService : IPlayerService
{
    private readonly record struct SubtitleAppearanceSnapshot(
        string PreferredLanguageMode,
        string PreferredLanguageCode,
        bool AllowEmbeddedFormatting,
        string FontFamily,
        int TextScale,
        int Margin,
        int Alignment,
        int Color,
        int Opacity,
        int BackgroundColor,
        int BackgroundOpacity,
        int OutlineThickness,
        int OutlineColor,
        int OutlineOpacity,
        bool Shadow,
        int ShadowColor,
        int ShadowAngle,
        int ShadowDistance,
        bool Bold);

    private readonly IVlcDialogService _vlcDialogService;
    private readonly IMediaSourceFactory _mediaSourceFactory;
    private readonly ILogger<PlayerService> _logger;
    private readonly ILogger _vlcLogger;
    private readonly bool _useFal;
    private readonly Dictionary<string, int> _tokenReferences = new();
    private readonly PlayerSettingsContext _playerSettings;
    private SubtitleAppearanceSnapshot? _activeSubtitleSnapshot;

    public PlayerService(
        IVlcDialogService vlcDialogService,
        IMediaSourceFactory mediaSourceFactory,
        PlayerSettingsContext playerSettings,
        ILogger<PlayerService> logger,
        ILogger<LibVLC> vlcLogger)
    {
        _vlcDialogService = vlcDialogService;
        _mediaSourceFactory = mediaSourceFactory;
        _playerSettings = playerSettings;
        _logger = logger;
        _vlcLogger = vlcLogger;

        _useFal = true;

        try
        {
            var tokensToRemove = StorageApplicationPermissions.FutureAccessList.Entries
                .Where(entry => entry.Metadata == "media")
                .Select(entry => entry.Token)
                .ToList();
            foreach (var token in tokensToRemove)
            {
                StorageApplicationPermissions.FutureAccessList.Remove(token);
            }
        }
        catch (Exception)
        {
            _useFal = false;
        }
    }

    public IMediaPlayer Initialize(string[] swapChainOptions)
    {
        _activeSubtitleSnapshot = GetCurrentSubtitleSnapshot();
        LibVLC lib = InitializeLibVlc(swapChainOptions);
        return new VlcMediaPlayer(lib, _playerSettings);
    }

    public bool AreSubtitleSettingsDirty()
    {
        return _activeSubtitleSnapshot is { } active && active != GetCurrentSubtitleSnapshot();
    }

    public IPlaybackItem CreatePlaybackItem(IMediaPlayer player, IMediaSource source, params string[] options)
    {
        if (player is not VlcMediaPlayer vlcMediaPlayer)
            throw new NotSupportedException("Only VlcMediaPlayer is supported");

        var mergedOptions = new List<string>(options.Length + 21);
        mergedOptions.AddRange(options);
        mergedOptions.AddRange(GetSubtitleOptions(perMedia: true));
        if (_playerSettings.IsAudioOnly)
        {
            mergedOptions.Add(":no-video");
        }

        Media media = CreateMedia(vlcMediaPlayer, source, mergedOptions.ToArray());
        return new VlcPlaybackItem(source, media, DisposeMedia, _mediaSourceFactory);
    }

    public void DisposePlayer(IMediaPlayer player)
    {
        if (player is VlcMediaPlayer vlcMediaPlayer)
        {
            vlcMediaPlayer.VlcPlayer.Dispose();
            vlcMediaPlayer.LibVlc.Dispose();
        }
    }

    private SubtitleAppearanceSnapshot GetCurrentSubtitleSnapshot() =>
        new(
            _playerSettings.SubtitlePreferredLanguageMode,
            _playerSettings.SubtitlePreferredLanguageCode.Trim(),
            _playerSettings.SubtitleAllowEmbeddedFormatting,
            _playerSettings.SubtitleFontFamily,
            Math.Clamp(_playerSettings.SubtitleTextScale, 10, 500),
            Math.Clamp(_playerSettings.SubtitleMargin, 0, 500),
            Math.Clamp(_playerSettings.SubtitleAlignment, 0, 2),
            _playerSettings.SubtitleColor & 0x00FFFFFF,
            Math.Clamp(_playerSettings.SubtitleOpacity, 0, 255),
            _playerSettings.SubtitleBackgroundColor & 0x00FFFFFF,
            Math.Clamp(_playerSettings.SubtitleBackgroundOpacity, 0, 255),
            Math.Clamp(_playerSettings.SubtitleOutlineThickness, 0, 6),
            _playerSettings.SubtitleOutlineColor & 0x00FFFFFF,
            Math.Clamp(_playerSettings.SubtitleOutlineOpacity, 0, 255),
            _playerSettings.SubtitleShadow,
            _playerSettings.SubtitleShadowColor & 0x00FFFFFF,
            Math.Clamp(_playerSettings.SubtitleShadowAngle, 0, 360),
            Math.Clamp(_playerSettings.SubtitleShadowDistance, 0, 20),
            _playerSettings.SubtitleBold);

    private IEnumerable<string> GetSubtitleOptions(bool perMedia)
    {
        string prefix = perMedia ? ":" : "--";

        yield return _playerSettings.SubtitleAllowEmbeddedFormatting
            ? $"{prefix}subsdec-formatted"
            : $"{prefix}no-subsdec-formatted";

        int textScale = Math.Clamp(_playerSettings.SubtitleTextScale, 10, 500);
        int subMargin = Math.Clamp(_playerSettings.SubtitleMargin, 0, 500);
        int subAlign = Math.Clamp(_playerSettings.SubtitleAlignment, 0, 2);
        int textColor = _playerSettings.SubtitleColor & 0x00FFFFFF;
        int textOpacity = Math.Clamp(_playerSettings.SubtitleOpacity, 0, 255);
        int bgColor = _playerSettings.SubtitleBackgroundColor & 0x00FFFFFF;
        int bgOpacity = Math.Clamp(_playerSettings.SubtitleBackgroundOpacity, 0, 255);
        int outlineThickness = Math.Clamp(_playerSettings.SubtitleOutlineThickness, 0, 6);
        int outlineColor = _playerSettings.SubtitleOutlineColor & 0x00FFFFFF;
        int outlineOpacity = outlineThickness > 0 ? Math.Clamp(_playerSettings.SubtitleOutlineOpacity, 0, 255) : 0;
        int shadowOpacity = _playerSettings.SubtitleShadow ? 180 : 0;
        int shadowColor = _playerSettings.SubtitleShadowColor & 0x00FFFFFF;
        int shadowAngle = Math.Clamp(_playerSettings.SubtitleShadowAngle, 0, 360);
        double shadowDistance = Math.Clamp(_playerSettings.SubtitleShadowDistance, 0, 20) / 100.0;

        yield return $"{prefix}freetype-font={_playerSettings.SubtitleFontFamily}";
        yield return $"{prefix}sub-text-scale={textScale}";
        yield return $"{prefix}sub-margin={subMargin}";
        yield return $"{prefix}subsdec-align={subAlign}";
        yield return $"{prefix}freetype-color={textColor}";
        yield return $"{prefix}freetype-opacity={textOpacity}";
        yield return $"{prefix}freetype-background-color={bgColor}";
        yield return $"{prefix}freetype-background-opacity={bgOpacity}";
        yield return $"{prefix}freetype-outline-thickness={outlineThickness}";
        yield return $"{prefix}freetype-outline-color={outlineColor}";
        yield return $"{prefix}freetype-outline-opacity={outlineOpacity}";
        yield return $"{prefix}freetype-shadow-opacity={shadowOpacity}";
        yield return $"{prefix}freetype-shadow-color={shadowColor}";
        yield return $"{prefix}freetype-shadow-angle={shadowAngle}";
        yield return string.Create(CultureInfo.InvariantCulture, $"{prefix}freetype-shadow-distance={shadowDistance:0.00}");
        yield return _playerSettings.SubtitleBold
            ? $"{prefix}freetype-bold"
            : $"{prefix}no-freetype-bold";
    }

    private Media CreateMedia(VlcMediaPlayer player, IMediaSource source, params string[] options)
    {
        return source switch
        {
            IFileMediaSource fileSource when fileSource.StorageFile is not null =>
                CreateMedia(player, fileSource.StorageFile, options),
            IFileMediaSource fileSource =>
                CreateMedia(player, fileSource.Path, options),
            IUriMediaSource uriSource =>
                CreateMedia(player, uriSource.Uri, options),
            _ => throw new ArgumentOutOfRangeException(nameof(source), $"Unsupported media source type: {source.GetType().Name}")
        };
    }

    private Media CreateMedia(VlcMediaPlayer player, string str, params string[] options)
    {
        if (Uri.TryCreate(str, UriKind.Absolute, out var uri))
        {
            return CreateMedia(player, uri, options);
        }

        return new Media(player.LibVlc, str, FromType.FromPath, options);
    }

    [DynamicWindowsRuntimeCast(typeof(StorageFile))]
    private Media CreateMedia(VlcMediaPlayer player, IStorageFile file, params string[] options)
    {
        if (file is StorageFile storageFile
            && storageFile.Provider.Id.Equals("network", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(storageFile.Path)
            && !_useFal
            && Uri.TryCreate(storageFile.Path, UriKind.Absolute, out var uri))
        {
            return CreateMedia(player, uri, options);
        }

        string token = IncrementRefCount(file);
        string mrl = "winrt://" + token;
        return new Media(player.LibVlc, mrl, FromType.FromLocation, options);
    }

    private Media CreateMedia(VlcMediaPlayer player, Uri uri, params string[] options)
    {
        return new Media(player.LibVlc, uri, options);
    }

    private void DisposeMedia(Media media)
    {
        string mrl = media.Mrl;
        if (mrl.StartsWith("winrt://"))
        {
            string token = mrl.Substring(8);
            try
            {
                DecrementRefCount(token);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to release a playback access token.");
            }
        }

        media.Dispose();
    }

    private string IncrementRefCount(IStorageFile file)
    {
        string token = _useFal
            ? StorageApplicationPermissions.FutureAccessList.Add(file, "media")
            : SharedStorageAccessManager.AddFile(file);

        lock (_tokenReferences)
        {
            if (_tokenReferences.TryGetValue(token, out int refCount))
                _tokenReferences[token] = refCount + 1;
            else
                _tokenReferences[token] = 1;
        }

        return token;
    }

    private void DecrementRefCount(string token)
    {
        lock (_tokenReferences)
        {
            if (_tokenReferences.TryGetValue(token, out int refCount) && refCount > 1)
                _tokenReferences[token] = refCount - 1;
            else
            {
                _tokenReferences.Remove(token);

                if (_useFal)
                {
                    StorageApplicationPermissions.FutureAccessList.Remove(token);
                }
                else
                {
                    SharedStorageAccessManager.RemoveFile(token);
                }
            }
        }
    }

    private LibVLC InitializeLibVlc(string[] swapChainOptions)
    {
        List<string> options = new(swapChainOptions.Length + 20)
        {
#if DEBUG
            "--verbose=3",
#else
            "--verbose=0",
#endif
            "--aout=winstore",
            "--no-osd"
        };
        options.AddRange(swapChainOptions);
        options.AddRange(GetSubtitleOptions(perMedia: false));
#if DEBUG
        LibVLC libVlc = new(true, false, options.ToArray());
#else
        LibVLC libVlc = new(false, false, options.ToArray());
#endif
        libVlc.Log += OnLibVlcLog;
        _vlcDialogService.SetVlcDialogHandlers(libVlc);
        return libVlc;
    }

    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        LogVlcMessage(_vlcLogger, e.Module ?? "", e.Level, e.Message);
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "{Module}: {VlcLogLevel}: {Message}")]
    private static partial void LogVlcMessage(ILogger logger, string module, LibVLCSharp.Shared.LogLevel vlcLogLevel, string message);
}
