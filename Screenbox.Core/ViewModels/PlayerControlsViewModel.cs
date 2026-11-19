using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Screenbox.Core.Contexts;
using Screenbox.Core.Coordinators;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Playback;
using Screenbox.Core.Services;
using Windows.Foundation;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;

namespace Screenbox.Core.ViewModels;

public sealed partial class PlayerControlsViewModel : ObservableRecipient,
    IRecipient<SettingChangedMessage<PlayerSettingsContext, bool>>,
    IRecipient<PropertyChangedMessage<IMediaPlayer?>>,
    IRecipient<TogglePlayPauseMessage>,
    IRecipient<TrackNavigationMessage>,
    IRecipient<PropertyChangedMessage<PlayerVisibilityState>>,
    IRecipient<PropertyChangedMessage<WindowViewMode>>,
    IRecipient<QueueCanNavigateChangedMessage>
{
    public PlayQueueContext PlayQueue { get; }

    public bool ShouldBeAdaptive => !IsCompact && SystemInformation.IsDesktop;

    public bool HasSingleSubtitleTrackCount => MediaPlayer?.PlaybackItem is { SubtitleTracks.Count: 1 };

    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial bool IsFullscreen { get; set; }
    [ObservableProperty] public partial string? TitleName { get; set; }
    [ObservableProperty] public partial bool IsAdvancedModeActive { get; set; }
    [ObservableProperty] public partial bool IsMinimal { get; set; }
    [ObservableProperty] public partial bool PlayerShowChapters { get; set; }
    [ObservableProperty] public partial bool IsDisplayingRemainingTime { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldBeAdaptive))]
    public partial bool IsCompact { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleAudioOnly))]
    [NotifyCanExecuteChangedFor(nameof(SaveSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAudioOnlyCommand))]
    public partial bool HasVideo { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    public partial bool HasActiveItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleAudioOnly))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAudioOnlyCommand))]
    public partial bool IsAudioOnly { get; set; }

    public bool CanToggleAudioOnly =>
        IsAudioOnly || (MediaPlayer?.PlaybackItem is { VideoTracks.Count: > 0 });

    private int _lastVideoTrackIndex;

    private IMediaPlayer? MediaPlayer => _playerContext.MediaPlayer;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly IWindowService _windowService;
    private readonly PlayerSettingsContext _playerSettings;
    private readonly PlayerContext _playerContext;
    private readonly IFilesService _filesService;
    private readonly IPlayQueueCoordinator _coordinator;
    private readonly IPlayQueueBufferManager _bufferManager;

    private Size _aspectRatio;

    public PlayerControlsViewModel(
        PlayQueueContext playQueue,
        IFilesService filesService,
        IPlayQueueCoordinator coordinator,
        IPlayQueueBufferManager bufferManager,
        PlayerSettingsContext playerSettings,
        IWindowService windowService,
        PlayerContext playerContext)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _windowService = windowService;
        _playerSettings = playerSettings;
        _playerContext = playerContext;
        _filesService = filesService;
        _coordinator = coordinator;
        _bufferManager = bufferManager;
        IsAudioOnly = playerSettings.IsAudioOnly;
        IsAdvancedModeActive = playerSettings.AdvancedMode;
        IsMinimal = true;
        IsDisplayingRemainingTime = playerSettings.ShowRemainingTime;
        PlayerShowChapters = playerSettings.ShowChapters;
        PlayQueue = playQueue;
        PlayQueue.PropertyChanged += PlayQueueOnPropertyChanged;
        PlayQueue.Items.CollectionChanged += PlayQueueOnCollectionChanged;

        if (MediaPlayer != null)
        {
            MediaPlayer.PlaybackStateChanged += OnPlaybackStateChanged;
            MediaPlayer.NaturalVideoSizeChanged += OnNaturalVideoSizeChanged;
        }

        Messenger.Register<SettingChangedMessage<PlayerSettingsContext, bool>>(this);
        Messenger.Register<PropertyChangedMessage<IMediaPlayer?>>(this);
        Messenger.Register<TogglePlayPauseMessage>(this);
        Messenger.Register<TrackNavigationMessage>(this);
        Messenger.Register<PropertyChangedMessage<PlayerVisibilityState>>(this);
        Messenger.Register<PropertyChangedMessage<WindowViewMode>>(this);
        Messenger.Register<QueueCanNavigateChangedMessage>(this);
    }

    public void Receive(SettingChangedMessage<PlayerSettingsContext, bool> message)
    {
        if (message.PropertyName == nameof(PlayerSettingsContext.ShowChapters))
        {
            PlayerShowChapters = message.NewValue;
        }
        else if (message.PropertyName == nameof(PlayerSettingsContext.AdvancedMode))
        {
            IsAdvancedModeActive = message.NewValue;
        }
    }

    public void Receive(PropertyChangedMessage<IMediaPlayer?> message)
    {
        if (message.Sender is not PlayerContext) return;
        if (message.OldValue is { } oldPlayer)
        {
            oldPlayer.PlaybackStateChanged -= OnPlaybackStateChanged;
            oldPlayer.NaturalVideoSizeChanged -= OnNaturalVideoSizeChanged;
        }

        if (MediaPlayer != null)
        {
            MediaPlayer.PlaybackStateChanged += OnPlaybackStateChanged;
            MediaPlayer.NaturalVideoSizeChanged += OnNaturalVideoSizeChanged;
        }
    }

    public void Receive(TogglePlayPauseMessage message)
    {
        if (!HasActiveItem || MediaPlayer is null)
            return;

        PlayPause();

        if (message.ShowBadge)
        {
            Messenger.Send(
                new PlayerOsdUpdateMessage(
                    IsPlaying ? PlaybackCommandKind.Pause : PlaybackCommandKind.Play)
                .ShowBadge());
        }
    }

    public async void Receive(TrackNavigationMessage message)
    {
        if (MediaPlayer is null)
            return;

        switch (message.Direction)
        {
            case TrackNavigationDirection.Next when CanGoNext():
                await _coordinator.NextAsync();
                break;
            case TrackNavigationDirection.Previous when CanGoPrevious():
                await _coordinator.PreviousAsync();
                break;
        }
    }

    public void Receive(PropertyChangedMessage<PlayerVisibilityState> message)
    {
        IsMinimal = message.NewValue != PlayerVisibilityState.Visible;
    }

    public void HandlePlaybackStateToggleKey()
    {
        if (!HasActiveItem)
            return;

        Messenger.Send(new TogglePlayPauseMessage(showBadge: true));
    }

    public void HandleTrackNavigationKey(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        if (!HasActiveItem || MediaPlayer is null)
            return;

        switch (key)
        {
            case VirtualKey.PageUp:
            case VirtualKey.P when modifiers is VirtualKeyModifiers.Shift:
                Messenger.Send(new TrackNavigationMessage(TrackNavigationDirection.Previous));
                Messenger.Send(new PlayerOsdUpdateMessage(PlaybackCommandKind.Previous).ShowBadge());
                break;
            case VirtualKey.PageDown:
            case VirtualKey.N when modifiers is VirtualKeyModifiers.Shift:
                Messenger.Send(new TrackNavigationMessage(TrackNavigationDirection.Next));
                Messenger.Send(new PlayerOsdUpdateMessage(PlaybackCommandKind.Next).ShowBadge());
                break;
        }
    }

    public void HandleSubtitleToggleKey(VirtualKeyModifiers modifiers)
    {
        if (MediaPlayer?.PlaybackItem is null)
            return;

        ISubtitleTrackList tracks = MediaPlayer.PlaybackItem.SubtitleTracks;
        int count = tracks.Count;
        if (count == 0 || (count > 1 && modifiers == VirtualKeyModifiers.None))
            return;

        int oldIndex = tracks.SelectedIndex;

        tracks.SelectedIndex = modifiers switch
        {
            VirtualKeyModifiers.None when count == 1
                => oldIndex >= 0 ? -1 : 0,
            VirtualKeyModifiers.Control
                => oldIndex == count - 1 ? -1 : ++oldIndex,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift
                => oldIndex == -1 ? count - 1 : --oldIndex,
            _ => oldIndex,
        };

        string? label = tracks.SelectedIndex == -1
            ? null
            : tracks[tracks.SelectedIndex].Label;

        Messenger.Send(
            new PlayerOsdUpdateMessage(
                label is null ? PlaybackCommandKind.SubtitleOff : PlaybackCommandKind.Subtitle,
                value: label)
            .ShowMessage());
    }

    partial void OnIsDisplayingRemainingTimeChanged(bool value)
    {
        _playerSettings.ShowRemainingTime = value;
    }

    private void PlayQueueOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
    }

    private void PlayQueueOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayQueueContext.CurrentItem):
                _lastVideoTrackIndex = 0;
                HasActiveItem = PlayQueue.CurrentItem is not null;
                OnPropertyChanged(nameof(CanToggleAudioOnly));
                ToggleAudioOnlyCommand.NotifyCanExecuteChanged();
                NextCommand.NotifyCanExecuteChanged();
                PreviousCommand.NotifyCanExecuteChanged();
                Messenger.Send(new ResetPlaybackSessionMessage());
                break;
            case nameof(PlayQueueContext.CurrentIndex):
            case nameof(PlayQueueContext.RepeatMode):
            case nameof(PlayQueueContext.ShuffleMode):
                NextCommand.NotifyCanExecuteChanged();
                PreviousCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    private void OnNaturalVideoSizeChanged(IMediaPlayer sender, object? args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsAudioOnly && MediaPlayer?.NaturalVideoHeight > 0)
            {
                ApplyAudioOnlyTrackState(true);
            }

            HasVideo = !IsAudioOnly && MediaPlayer?.NaturalVideoHeight > 0;
            OnPropertyChanged(nameof(CanToggleAudioOnly));
            ToggleAudioOnlyCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnPlaybackStateChanged(IMediaPlayer sender, object? args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            IsPlaying = sender.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Opening;
        });
    }

    public void Receive(PropertyChangedMessage<WindowViewMode> message)
    {
        if (message.Sender is not WindowContext) return;
        switch (message.NewValue)
        {
            case WindowViewMode.Default:
                IsFullscreen = false;
                IsCompact = false;
                break;
            case WindowViewMode.Compact:
                IsCompact = true;
                IsFullscreen = false;
                break;
            case WindowViewMode.FullScreen:
                IsFullscreen = true;
                IsCompact = false;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public void Receive(QueueCanNavigateChangedMessage message)
    {
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task Next() => await _coordinator.NextAsync();

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task Previous() => await _coordinator.PreviousAsync();

    private bool CanGoNext() => _coordinator.CanNext();

    private bool CanGoPrevious() => _coordinator.CanPrevious();

    [RelayCommand]
    private void ResetMediaPlayback()
    {
        if (MediaPlayer is null) return;
        TimeSpan pos = MediaPlayer.Position;
        _coordinator.ResetCurrentItem();
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (MediaPlayer is null) return;
            MediaPlayer.Play();
            MediaPlayer.Position = pos;
        });
    }

    [RelayCommand]
    private void SetAspectRatio(string aspect)
    {
        switch (aspect)
        {
            case "Fit":
                _aspectRatio = new Size(0, 0);
                break;
            case "Fill":
                _aspectRatio = new Size(double.NaN, double.NaN);
                break;
            default:
                string[] values = aspect.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (values.Length != 2) return;
                if (!double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double width)) return;
                if (!double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double height)) return;
                _aspectRatio = new Size(width, height);
                break;
        }

        Messenger.Send(new ChangeAspectRatioMessage(_aspectRatio));
        Messenger.Send(new PlayerOsdUpdateMessage(PlaybackCommandKind.AspectRatio, value: aspect).ShowMessage());
    }

    [RelayCommand]
    private async Task ToggleCompactLayoutAsync()
    {
        if (IsCompact)
        {
            await _windowService.TryExitCompactLayoutAsync();
        }
        else if (!IsAudioOnly && MediaPlayer?.NaturalVideoHeight > 0)
        {
            double aspectRatio = MediaPlayer.NaturalVideoWidth / (double)MediaPlayer.NaturalVideoHeight;
            await _windowService.TryEnterCompactLayoutAsync(new Size(240 * aspectRatio, 240));
        }
        else
        {
            await _windowService.TryEnterCompactLayoutAsync(new Size(240, 240));
        }
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        if (IsCompact) return;
        if (IsFullscreen)
        {
            _windowService.ExitFullScreen();
        }
        else
        {
            _windowService.TryEnterFullScreen();
        }
    }

    [RelayCommand(CanExecute = nameof(HasActiveItem))]
    private void PlayPause()
    {
        if (IsPlaying)
        {
            MediaPlayer?.Pause();
        }
        else
        {
            MediaPlayer?.Play();
        }
    }

    [RelayCommand(CanExecute = nameof(HasVideo))]
    private async Task SaveSnapshotAsync()
    {
        if (MediaPlayer?.PlaybackState is not (MediaPlaybackState.Paused or MediaPlaybackState.Playing)) return;
        try
        {
            StorageFile file = await SaveSnapshotInternalAsync(MediaPlayer);
            Messenger.Send(new NotificationMessage(
                NotificationLevel.Success,
                NotificationKind.FrameSaved,
                actionContent: file.Name,
                actionCommand: new RelayCommand(() => _filesService.OpenFileLocationAsync(file))));
        }
        catch (UnauthorizedAccessException)
        {
            Messenger.Send(new NotificationMessage(NotificationLevel.Error, NotificationKind.PicturesLibraryAccessDenied));
        }
        catch (Exception e)
        {
            Messenger.Send(new NotificationMessage(NotificationLevel.Error, NotificationKind.FrameSaveFailed, message: e.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleAudioOnly))]
    private void ToggleAudioOnly()
    {
        IsAudioOnly = !IsAudioOnly;
    }

    partial void OnIsAudioOnlyChanged(bool value)
    {
        _playerSettings.IsAudioOnly = value;

        // Clear buffered items so upcoming queue items are created with or without :no-video.
        foreach (var buffered in _bufferManager.BufferedItems)
        {
            if (buffered != PlayQueue.CurrentItem)
            {
                _bufferManager.CleanPlaybackItem(buffered);
            }
        }

        if (!value && MediaPlayer?.PlaybackItem is { VideoTracks.Count: 0 } && PlayQueue.CurrentItem is not null)
        {
            // Current item was opened with :no-video; reload it at the current position to restore video tracks.
            ResetMediaPlayback();
        }
        else
        {
            ApplyAudioOnlyTrackState(value);
        }

        HasVideo = !value && MediaPlayer?.NaturalVideoHeight > 0;
        Messenger.Send(new AudioOnlyModeChangedMessage(value));
        Messenger.Send(new PlayerOsdUpdateMessage(default, value: value ? "Audio only: On" : "Audio only: Off").ShowMessage());
    }

    private void ApplyAudioOnlyTrackState(bool audioOnly)
    {
        if (MediaPlayer?.PlaybackItem is not { VideoTracks.Count: > 0 } item)
            return;

        if (audioOnly)
        {
            if (item.VideoTracks.SelectedIndex >= 0)
            {
                _lastVideoTrackIndex = item.VideoTracks.SelectedIndex;
            }

            if (item.VideoTracks.SelectedIndex != -1)
            {
                item.VideoTracks.SelectedIndex = -1;
            }
        }
        else if (item.VideoTracks.SelectedIndex == -1)
        {
            int targetIndex = _lastVideoTrackIndex >= 0 && _lastVideoTrackIndex < item.VideoTracks.Count
                ? _lastVideoTrackIndex
                : 0;
            item.VideoTracks.SelectedIndex = targetIndex;
        }
    }

    private async Task<StorageFile> SaveSnapshotInternalAsync(IMediaPlayer mediaPlayer)
    {
        StorageFolder tempFolder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync(
            $"snapshot_{DateTimeOffset.Now.Ticks}",
            CreationCollisionOption.FailIfExists);

        try
        {
            if (!mediaPlayer.TryTakeSnapshot(tempFolder.Path))
                throw new Exception("Media player failed to save snapshot");

            StorageFile file = (await tempFolder.GetFilesAsync())[0];
            StorageFolder destFolder = await _filesService.GetFrameCaptureFolderAsync(_playerSettings.FrameCaptureFolderToken);
            return await file.CopyAsync(destFolder, $"Screenbox_{DateTimeOffset.Now:yyyyMMdd_HHmmss}{file.FileType}",
                NameCollisionOption.GenerateUniqueName);
        }
        finally
        {
            await tempFolder.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
    }
}
