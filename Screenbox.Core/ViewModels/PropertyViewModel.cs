extern alias MediaInfoLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Screenbox.Core.Contexts;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.MediaSources;
using Screenbox.Core.Models;
using Screenbox.Core.Playback;
using Screenbox.Core.Services;
using Windows.ApplicationModel;
using Windows.Storage;

namespace Screenbox.Core.ViewModels;

public sealed partial class PropertyViewModel : ObservableObject
{
    [ObservableProperty]
    public partial IReadOnlyList<MediaMetadata> MediaProperties { get; set; } = Array.Empty<MediaMetadata>();

    [ObservableProperty]
    public partial IReadOnlyList<MediaMetadata> VideoProperties { get; set; } = Array.Empty<MediaMetadata>();

    [ObservableProperty]
    public partial IReadOnlyList<MediaMetadata> AudioProperties { get; set; } = Array.Empty<MediaMetadata>();

    [ObservableProperty]
    public partial IReadOnlyList<MediaMetadata> FileProperties { get; set; } = Array.Empty<MediaMetadata>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFileLocationCommand))]
    public partial bool CanNavigateToFile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    public partial bool IsLoading { get; set; }

    public bool IsContentVisible => !IsLoading;

    private sealed record CachedMediaInfoResult(
        string Location,
        IReadOnlyList<MediaMetadata> MediaProperties,
        IReadOnlyList<MediaMetadata> VideoProperties,
        IReadOnlyList<MediaMetadata> AudioProperties);

    private static CachedMediaInfoResult? _lastInspectedCache;

    private readonly IFilesService _filesService;
    private readonly ISourceMetadataService _sourceMetadataService;
    private readonly IPlaybackMetadataService _playbackMetadataService;
    private readonly PlayerContext _playerContext;
    private readonly IPlayerService _playerService;
    private readonly IPlayQueueBufferManager _bufferManager;
    private StorageFile? _mediaFile;
    private Uri? _mediaUri;
    private string? _mediaFilePath;

    public PropertyViewModel(
        IFilesService filesService,
        ISourceMetadataService sourceMetadataService,
        IPlaybackMetadataService playbackMetadataService,
        PlayerContext playerContext,
        IPlayerService playerService,
        IPlayQueueBufferManager bufferManager)
    {
        _filesService = filesService;
        _sourceMetadataService = sourceMetadataService;
        _playbackMetadataService = playbackMetadataService;
        _playerContext = playerContext;
        _playerService = playerService;
        _bufferManager = bufferManager;
    }

    /// <summary>
    /// Loads media metadata on demand for the specified <paramref name="media"/> item
    /// and refreshes the property collections.
    /// </summary>
    /// <param name="media">The media view model whose properties are being inspected.</param>
    public async Task LoadPropertiesAsync(MediaViewModel media)
    {
        UpdateProperties(media);

        if (_lastInspectedCache is { } cached &&
            string.Equals(cached.Location, media.Location, StringComparison.OrdinalIgnoreCase))
        {
            MediaProperties = cached.MediaProperties;
            VideoProperties = cached.VideoProperties;
            AudioProperties = cached.AudioProperties;
            return;
        }

        // Discard stale cache as soon as a different media item is inspected
        _lastInspectedCache = null;

        IsLoading = true;
        try
        {
            if (!media.DetailsLoaded)
            {
                await media.LoadDetailsAsync(_sourceMetadataService);
            }

            if (_bufferManager.TryGetPlaybackItem(media, out IPlaybackItem? bufferedItem))
            {
                await media.LoadDetailsAsync(_playbackMetadataService, bufferedItem);
            }
            else if (_playerContext.MediaPlayer is { } mediaPlayer)
            {
                if (mediaPlayer.PlaybackItem is { } activeItem && ReferenceEquals(activeItem.Source, media.Source))
                {
                    await media.LoadDetailsAsync(_playbackMetadataService, activeItem);
                }
                else
                {
                    try
                    {
                        using IPlaybackItem tempItem = _playerService.CreatePlaybackItem(
                            mediaPlayer,
                            media.Source,
                            media.Options.Count > 0 ? media.Options.ToArray() : Array.Empty<string>());
                        await media.LoadDetailsAsync(_playbackMetadataService, tempItem);
                    }
                    catch (Exception)
                    {
                        // Ignore playback item creation failures during property inspection
                    }
                }
            }

            UpdateProperties(media);

            if (_mediaFile != null || !string.IsNullOrEmpty(_mediaFilePath))
            {
                await PopulateFromMediaInfoAsync(_mediaFile, _mediaFilePath);
                if (!string.IsNullOrEmpty(media.Location))
                {
                    _lastInspectedCache = new CachedMediaInfoResult(
                        media.Location,
                        MediaProperties,
                        VideoProperties,
                        AudioProperties);
                }
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Populates the property collections with metadata keys and values from the given <paramref name="media"/> item.
    /// </summary>
    public void UpdateProperties(MediaViewModel media)
    {
        List<MediaMetadata> mediaEntries = new();
        List<MediaMetadata> videoEntries = new();
        List<MediaMetadata> audioEntries = new();
        List<MediaMetadata> fileEntries = new();

        switch (media.MediaType)
        {
            case MediaPlaybackType.Video:
                mediaEntries.Add(new MediaMetadata(MediaProperty.Title, string.IsNullOrEmpty(media.MediaInfo.VideoProperties.Title)
                    ? media.Name
                    : media.MediaInfo.VideoProperties.Title));
                mediaEntries.Add(new MediaMetadata(MediaProperty.Subtitle, media.MediaInfo.VideoProperties.Subtitle));
                mediaEntries.Add(new MediaMetadata(MediaProperty.Year, media.MediaInfo.VideoProperties.Year > 0
                    ? media.MediaInfo.VideoProperties.Year.ToString()
                    : string.Empty));
                mediaEntries.Add(new MediaMetadata(MediaProperty.Producers, string.Join("; ", media.MediaInfo.VideoProperties.Producers)));
                mediaEntries.Add(new MediaMetadata(MediaProperty.Writers, string.Join("; ", media.MediaInfo.VideoProperties.Writers)));
                mediaEntries.Add(new MediaMetadata(MediaProperty.Length, Humanizer.ToDuration(media.MediaInfo.VideoProperties.Duration)));

                if (media.MediaInfo.VideoProperties.Width > 0 && media.MediaInfo.VideoProperties.Height > 0)
                {
                    videoEntries.Add(new MediaMetadata(MediaProperty.Resolution, $"{media.MediaInfo.VideoProperties.Width}×{media.MediaInfo.VideoProperties.Height}"));
                }
                if (media.MediaInfo.VideoProperties.Bitrate > 0)
                {
                    videoEntries.Add(new MediaMetadata(MediaProperty.BitRate, $"{media.MediaInfo.VideoProperties.Bitrate / 1000} kbps"));
                }
                if (media.MediaInfo.MusicProperties.Bitrate > 0)
                {
                    audioEntries.Add(new MediaMetadata(MediaProperty.BitRate, $"{media.MediaInfo.MusicProperties.Bitrate / 1000} kbps"));
                }
                break;

            case MediaPlaybackType.Music:
                mediaEntries.Add(new MediaMetadata(MediaProperty.Title, string.IsNullOrEmpty(media.MediaInfo.MusicProperties.Title)
                    ? media.Name
                    : media.MediaInfo.MusicProperties.Title));
                mediaEntries.Add(new MediaMetadata(MediaProperty.ContributingArtists, media.MediaInfo.MusicProperties.Artist));
                mediaEntries.Add(new MediaMetadata(MediaProperty.Album, media.MediaInfo.MusicProperties.Album));
                mediaEntries.Add(new MediaMetadata(MediaProperty.AlbumArtist, media.MediaInfo.MusicProperties.AlbumArtist));
                mediaEntries.Add(new MediaMetadata(MediaProperty.Composers, string.Join("; ", media.MediaInfo.MusicProperties.Composers)));
                mediaEntries.Add(new MediaMetadata(MediaProperty.Genre, string.Join("; ", media.MediaInfo.MusicProperties.Genre)));
                mediaEntries.Add(new MediaMetadata(MediaProperty.Track, media.MediaInfo.MusicProperties.TrackNumber > 0
                    ? media.MediaInfo.MusicProperties.TrackNumber.ToString()
                    : string.Empty));
                mediaEntries.Add(new MediaMetadata(MediaProperty.Year, media.MediaInfo.MusicProperties.Year > 0
                    ? media.MediaInfo.MusicProperties.Year.ToString()
                    : string.Empty));
                mediaEntries.Add(new MediaMetadata(MediaProperty.Length, Humanizer.ToDuration(media.MediaInfo.MusicProperties.Duration)));

                if (media.MediaInfo.MusicProperties.Bitrate > 0)
                {
                    audioEntries.Add(new MediaMetadata(MediaProperty.BitRate, $"{media.MediaInfo.MusicProperties.Bitrate / 1000} kbps"));
                }
                break;
        }

        switch (media.Source)
        {
            case IFileMediaSource fileSource:
                _mediaFile = fileSource.StorageFile;
                _mediaFilePath = fileSource.Path;
                _mediaUri = null;
                CanNavigateToFile = true;
                fileEntries.Add(new MediaMetadata(MediaProperty.FileType, fileSource.FileType));
                if (_mediaFile != null)
                {
                    fileEntries.Add(new MediaMetadata(MediaProperty.ContentType, _mediaFile.ContentType));
                }
                fileEntries.Add(new MediaMetadata(MediaProperty.Size, BytesToHumanReadable((long)media.MediaInfo.Size)));
                fileEntries.Add(new MediaMetadata(MediaProperty.LastModified, media.MediaInfo.DateModified.ToString()));
                break;
            case IUriMediaSource uriSource:
                _mediaFile = null;
                _mediaFilePath = null;
                _mediaUri = uriSource.Uri;
                CanNavigateToFile = uriSource.Uri.IsFile;
                break;
        }

        MediaProperties = mediaEntries;
        VideoProperties = videoEntries;
        AudioProperties = audioEntries;
        FileProperties = fileEntries;
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToFile))]
    private void OpenFileLocation()
    {
        if (_mediaFile != null)
            _filesService.OpenFileLocationAsync(_mediaFile);
        else if (!string.IsNullOrEmpty(_mediaFilePath))
            _filesService.OpenFileLocationAsync(_mediaFilePath);
        else if (_mediaUri != null)
            _filesService.OpenFileLocationAsync(_mediaUri.OriginalString);
    }

    // https://stackoverflow.com/a/11124118
    private static string BytesToHumanReadable(long byteCount)
    {
        long absCount = byteCount < 0 ? -byteCount : byteCount;
        string suffix;
        double readable;
        if (absCount >= 0x1000000000000000) // Exabyte
        {
            suffix = "EB";
            readable = byteCount >> 50;
        }
        else if (absCount >= 0x4000000000000) // Petabyte
        {
            suffix = "PB";
            readable = byteCount >> 40;
        }
        else if (absCount >= 0x10000000000) // Terabyte
        {
            suffix = "TB";
            readable = byteCount >> 30;
        }
        else if (absCount >= 0x40000000) // Gigabyte
        {
            suffix = "GB";
            readable = byteCount >> 20;
        }
        else if (absCount >= 0x100000) // Megabyte
        {
            suffix = "MB";
            readable = byteCount >> 10;
        }
        else if (absCount >= 0x400) // Kilobyte
        {
            suffix = "KB";
            readable = byteCount;
        }
        else
        {
            return byteCount.ToString("0 B"); // Byte
        }

        readable = readable / 1024;
        return readable.ToString("0.## ") + suffix;
    }

    private async Task PopulateFromMediaInfoAsync(StorageFile? storageFile, string? filePath)
    {
        MediaInfoLib::MediaInfo.MediaInfo mi;
        try
        {
            mi = new MediaInfoLib::MediaInfo.MediaInfo();
        }
        catch
        {
            return;
        }

        using (mi)
        {
            bool opened = false;
            bool isPartialCopy = false;
            StorageFile? tempFile = null;

            try
            {
                // 1. Fast path: direct path access (works if broadFileSystemAccess is enabled)
                string? resolvedPath = !string.IsNullOrEmpty(filePath) ? filePath : storageFile?.Path;
                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    opened = mi.Open(resolvedPath) != 0;
                }

                // 2. Sandbox fallback: full copy for files <= 500 MB (when C: has room), or 32 MB header-only chunk for huge files
                if (!opened && storageFile != null)
                {
                    var basicProps = await storageFile.GetBasicPropertiesAsync();
                    const ulong maxFullCopyBytes = 500UL * 1024 * 1024; // 500 MB
                    const int partialChunkBytes = 32 * 1024 * 1024;     // 32 MB header chunk

                    var tempProps = await ApplicationData.Current.TemporaryFolder.GetBasicPropertiesAsync();
                    var retrieved = await tempProps.RetrievePropertiesAsync(new[] { "System.FreeSpace" });
                    ulong freeSpace = retrieved.TryGetValue("System.FreeSpace", out object? val) && val is ulong space ? space : 0;

                    if (basicProps.Size <= maxFullCopyBytes && freeSpace > basicProps.Size * 2)
                    {
                        tempFile = await storageFile.CopyAsync(
                            ApplicationData.Current.TemporaryFolder,
                            $"{Guid.NewGuid()}{storageFile.FileType}",
                            NameCollisionOption.ReplaceExisting);
                        opened = mi.Open(tempFile.Path) != 0;
                    }
                    else if (freeSpace > (ulong)partialChunkBytes * 2)
                    {
                        tempFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                            $"{Guid.NewGuid()}{storageFile.FileType}",
                            CreationCollisionOption.ReplaceExisting);

                        using (Stream src = await storageFile.OpenStreamForReadAsync())
                        using (Stream dst = await tempFile.OpenStreamForWriteAsync())
                        {
                            byte[] buffer = new byte[81920];
                            int totalRead = 0;
                            while (totalRead < partialChunkBytes)
                            {
                                int toRead = Math.Min(buffer.Length, partialChunkBytes - totalRead);
                                int bytesRead = await src.ReadAsync(buffer, 0, toRead);
                                if (bytesRead == 0) break;
                                await dst.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;
                            }
                        }

                        isPartialCopy = true;
                        opened = mi.Open(tempFile.Path) != 0;
                    }
                }

                if (!opened) return;

                // Start fresh so WinRT fallback fields are not mixed with MediaInfo output
                List<MediaMetadata> mediaEntries = new();
                List<MediaMetadata> videoEntries = new();
                List<MediaMetadata> audioEntries = new();

                // --- GENERAL TAGS -> MediaProperties ---
                AddOrReplace(mediaEntries, MediaProperty.CompleteName, resolvedPath);
                if (!isPartialCopy)
                {
                    AddOrReplace(mediaEntries, MediaProperty.Size, mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "FileSize/String"));
                    AddOrReplace(mediaEntries, MediaProperty.Length, mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "Duration/String3"));
                    AddOrReplace(mediaEntries, MediaProperty.OverallBitRate, mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "OverallBitRate/String"));
                }
                AddOrReplace(mediaEntries, MediaProperty.FrameRate, mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "FrameRate/String"));
                AddOrReplace(mediaEntries, MediaProperty.Title, mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "Title"));
                AddOrReplace(mediaEntries, MediaProperty.Performer, mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "Performer"));
                AddOrReplace(mediaEntries, MediaProperty.Genre, mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "Genre"));

                string description = FirstNonEmpty(
                    mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "Description"),
                    mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "Synopsis"),
                    mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "LongDescription"));
                if (!string.IsNullOrEmpty(description))
                {
                    description = description.Replace(" / ", Environment.NewLine);
                }
                AddOrReplace(mediaEntries, MediaProperty.Description, description);

                string recordedDate = FirstNonEmpty(
                    mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "Recorded_Date"),
                    mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "Encoded_Date"));
                AddOrReplace(mediaEntries, MediaProperty.RecordedDate, recordedDate);

                string comment = mi.Get(MediaInfoLib::MediaInfo.StreamKind.General, 0, "Comment");
                if (!string.IsNullOrEmpty(comment))
                {
                    comment = comment.Replace(" / ", Environment.NewLine);
                }
                AddOrReplace(mediaEntries, MediaProperty.Comment, comment);

                // --- VIDEO TAGS -> VideoProperties ---
                if (mi.CountGet(MediaInfoLib::MediaInfo.StreamKind.Video) > 0)
                {
                    AddOrReplace(videoEntries, MediaProperty.Format, mi.Get(MediaInfoLib::MediaInfo.StreamKind.Video, 0, "Format"));
                    AddOrReplace(videoEntries, MediaProperty.Width, mi.Get(MediaInfoLib::MediaInfo.StreamKind.Video, 0, "Width/String"));
                    AddOrReplace(videoEntries, MediaProperty.Height, mi.Get(MediaInfoLib::MediaInfo.StreamKind.Video, 0, "Height/String"));
                    AddOrReplace(videoEntries, MediaProperty.DisplayAspectRatio, mi.Get(MediaInfoLib::MediaInfo.StreamKind.Video, 0, "DisplayAspectRatio/String"));
                }

                // --- AUDIO TAGS -> AudioProperties ---
                if (mi.CountGet(MediaInfoLib::MediaInfo.StreamKind.Audio) > 0)
                {
                    AddOrReplace(audioEntries, MediaProperty.AudioFormat, mi.Get(MediaInfoLib::MediaInfo.StreamKind.Audio, 0, "Format"));

                    string audioBitrate = FirstNonEmpty(
                        mi.Get(MediaInfoLib::MediaInfo.StreamKind.Audio, 0, "BitRate/String"),
                        mi.Get(MediaInfoLib::MediaInfo.StreamKind.Audio, 0, "BitRate_Nominal/String"),
                        mi.Get(MediaInfoLib::MediaInfo.StreamKind.Audio, 0, "BitRate_Maximum/String"));

                    // Only run FFmpeg full-file packet scan if we have the complete file (not a 32 MB partial chunk)
                    if (string.IsNullOrEmpty(audioBitrate) && !isPartialCopy)
                    {
                        string targetPath = tempFile?.Path ?? resolvedPath ?? string.Empty;
                        if (!string.IsNullOrEmpty(targetPath))
                        {
                            audioBitrate = await CalculateAudioBitrateWithFfmpegAsync(targetPath);
                        }
                    }

                    AddOrReplace(audioEntries, MediaProperty.AudioBitRate, audioBitrate);
                    AddOrReplace(audioEntries, MediaProperty.AudioSamplingRate, mi.Get(MediaInfoLib::MediaInfo.StreamKind.Audio, 0, "SamplingRate/String"));
                    AddOrReplace(audioEntries, MediaProperty.AudioBitDepth, mi.Get(MediaInfoLib::MediaInfo.StreamKind.Audio, 0, "BitDepth/String"));
                }

                MediaProperties = mediaEntries;
                VideoProperties = videoEntries;
                AudioProperties = audioEntries;
            }
            catch (Exception)
            {
                // Transient WinRT StorageFile contention (e.g., 0x8000000E E_ILLEGAL_METHOD_CALL)
                // or file-access failure: keep the already-populated WinRT/LibVLC fallback properties.
            }
            finally
            {
                mi.Close();
                if (tempFile != null)
                {
                    try
                    {
                        await tempFile.DeleteAsync();
                    }
                    catch
                    {
                        // Ignore cleanup errors on temporary copy
                    }
                }
            }
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? val in values)
        {
            if (!string.IsNullOrWhiteSpace(val))
                return val.Trim();
        }
        return string.Empty;
    }

    private static void AddOrReplace(List<MediaMetadata> list, MediaProperty key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            list.RemoveAll(x => x.Key == key);
            list.Add(new MediaMetadata(key, value.Trim()));
        }
    }

    private static async Task<string> CalculateAudioBitrateWithFfmpegAsync(string inputPath)
    {
        try
        {
            string ffmpegPath = Path.Combine(Package.Current.InstalledLocation.Path, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                return string.Empty;
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -v info -i \"{inputPath}\" -vn -c:a copy -f null -",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return string.Empty;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var registration = cts.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch
                {
                    // Ignore kill race if process exited concurrently
                }
            });

            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cts.Token);

            if (cts.IsCancellationRequested)
            {
                return string.Empty;
            }

            double totalSeconds = 0;
            var durMatch = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (durMatch.Success &&
                long.TryParse(durMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long hours) &&
                long.TryParse(durMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long minutes) &&
                double.TryParse(durMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                totalSeconds = (hours * 3600d) + (minutes * 60d) + seconds;
            }

            var audioSizeMatch = Regex.Match(
                stderr,
                @"audio:\s*(\d+(?:\.\d+)?)\s*(kB|KiB)",
                RegexOptions.IgnoreCase);

            if (audioSizeMatch.Success &&
                totalSeconds > 0 &&
                double.TryParse(audioSizeMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double audioSize))
            {
                string unit = audioSizeMatch.Groups[2].Value;
                double factor = unit.Equals("KiB", StringComparison.OrdinalIgnoreCase) ? 8.192 : 8.0;
                double kbps = (audioSize * factor) / totalSeconds;
                return $"{Math.Round(kbps)} kbps";
            }
        }
        catch
        {
            // Ignore FFmpeg failures/timeouts and omit bitrate if unavailable
        }

        return string.Empty;
    }
}
