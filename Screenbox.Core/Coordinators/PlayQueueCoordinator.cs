using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Enums;
using Screenbox.Core.Factories;
using Screenbox.Core.Helpers;
using Screenbox.Core.MediaSources;
using Screenbox.Core.Messages;
using Screenbox.Core.Models;
using Screenbox.Core.Playback;
using Screenbox.Core.Services;
using Screenbox.Core.ViewModels;
using Windows.Media;
using Windows.Media.Playback;
using Windows.System;

namespace Screenbox.Core.Coordinators;

public sealed partial class PlayQueueCoordinator : ObservableRecipient, IPlayQueueCoordinator,
    IRecipient<PlayMediaMessage>,
    IRecipient<SetQueueMessage>,
    IRecipient<ClearQueueMessage>,
    IRecipient<QueueRequestMessage>,
    IRecipient<QueuePlaybackStateRequestMessage>,
    IRecipient<PropertyChangedMessage<IMediaPlayer?>>,
    IRecipient<UpdateMediaOptionsMessage>
{
    private IMediaPlayer? MediaPlayer => _playerContext.MediaPlayer;

    private readonly PlayQueueContext _context;
    private readonly IQueueService _queueService;
    private readonly IPlaybackControlService _playbackControlService;

    private readonly IFilesService _filesService;
    private readonly GeneralSettingsContext _generalSettings;
    private readonly PlaybackSettingsContext _playbackSettings;
    private readonly ISystemMediaTransportControlsService _transportControlsService;
    private readonly MediaViewModelFactory _mediaFactory;
    private readonly PlayerContext _playerContext;
    private readonly IPlayQueueBufferManager _bufferManager;
    private readonly IPlayerService? _playerService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger<PlayQueueCoordinator> _logger;

    private QueueSession _queueSession;

    private Func<Task>? _delayPlay;
    private MediaViewModel? _pendingSeekItem;
    private TimeSpan _pendingSeekPosition;

    private bool _deferCollectionChanged;

    private CancellationTokenSource? _currentItemCts;
    private Guid _recordedSessionId;
    private RecentItemKind? _recordedOriginKind;
    private QueuePlaybackState? _lastBroadcastState;

    public PlayQueueCoordinator(
        PlayQueueContext context,
        IQueueService queueService,
        IPlaybackControlService playbackControlService,
        IFilesService filesService,
        GeneralSettingsContext generalSettings,
        PlaybackSettingsContext playbackSettings,
        ISystemMediaTransportControlsService transportControlsService,
        MediaViewModelFactory mediaFactory,
        PlayerContext playerContext,
        IPlayQueueBufferManager bufferManager,
        ILogger<PlayQueueCoordinator> logger,
        IPlayerService? playerService = null)
    {
        _context = context;
        _queueService = queueService;
        _playbackControlService = playbackControlService;
        _filesService = filesService;
        _generalSettings = generalSettings;
        _playbackSettings = playbackSettings;
        _transportControlsService = transportControlsService;
        _mediaFactory = mediaFactory;
        _playerContext = playerContext;
        _bufferManager = bufferManager;
        _playerService = playerService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _queueSession = new QueueSession { SessionId = Guid.Empty };

        _context.RepeatMode = playbackSettings.RepeatMode;

        _context.PropertyChanged += OnContextPropertyChanged;

        _context.Items.CollectionChanged += OnCollectionChanged;

        _transportControlsService.TransportControls.ButtonPressed += TransportControlsOnButtonPressed;
        _transportControlsService.TransportControls.AutoRepeatModeChangeRequested += TransportControlsOnAutoRepeatModeChangeRequested;

        if (MediaPlayer is not null)
        {
            MediaPlayer.MediaFailed += OnMediaFailed;
            MediaPlayer.MediaEnded += OnEndReached;
            MediaPlayer.PlaybackStateChanged += OnPlaybackStateChanged;
        }

        Messenger.Register<PlayMediaMessage>(this);
        Messenger.Register<SetQueueMessage>(this);
        Messenger.Register<ClearQueueMessage>(this);
        Messenger.Register<QueueRequestMessage>(this);
        Messenger.Register<QueuePlaybackStateRequestMessage>(this);
        Messenger.Register<PropertyChangedMessage<IMediaPlayer?>>(this);
        Messenger.Register<UpdateMediaOptionsMessage>(this);
    }

    #region Message Handlers

    public async void Receive(PlayMediaMessage message)
    {
        if (MediaPlayer is null)
        {
            _delayPlay = () => ProcessPlayMediaAsync(message);
            return;
        }

        await ProcessPlayMediaAsync(message);
    }

    private async Task ProcessPlayMediaAsync(PlayMediaMessage message)
    {
        if (message.Existing)
        {
            await PlayAsync(message.Value);
        }
        else
        {
            ClearQueue();
            QueueSession playlist = await _queueService.ParseQueueAsync(message.Value);
            ApplyQueueSnapshot(playlist, shouldPlay: true);
        }
    }

    public void Receive(ClearQueueMessage message)
    {
        Clear();
    }

    public void Receive(SetQueueMessage message)
    {
        var playNext = message.Value.CurrentItem;
        bool shouldPlay = message.ShouldPlay && playNext is not null;
        ApplyQueueSnapshot(message.Value, shouldPlay: shouldPlay && MediaPlayer is not null);
        if (shouldPlay && MediaPlayer is null)
        {
            _delayPlay = () =>
            {
                MediaPlayer?.Play();
                return Task.CompletedTask;
            };
        }
    }

    public void Receive(UpdateMediaOptionsMessage message)
    {
        var (media, options) = message;
        media.SetOptions(options);

        if (media != _context.CurrentItem)
        {
            _bufferManager.CleanPlaybackItem(media);
        }
    }

    public void Receive(QueueRequestMessage message)
    {
        message.Reply(new QueueSession(_queueSession));
    }

    public void Receive(QueuePlaybackStateRequestMessage message)
    {
        message.Reply(GetCurrentPlaybackState());
    }

    public async void Receive(PropertyChangedMessage<IMediaPlayer?> message)
    {
        if (message.Sender is not PlayerContext) return;
        if (message.OldValue is { } oldPlayer)
        {
            oldPlayer.MediaFailed -= OnMediaFailed;
            oldPlayer.MediaEnded -= OnEndReached;
            oldPlayer.PlaybackStateChanged -= OnPlaybackStateChanged;

            _bufferManager.Clear();
        }

        if (MediaPlayer is not null)
        {
            MediaPlayer.MediaFailed += OnMediaFailed;
            MediaPlayer.MediaEnded += OnEndReached;
            MediaPlayer.PlaybackStateChanged += OnPlaybackStateChanged;

            if (_context.CurrentItem is not null && MediaPlayer.PlaybackItem is null)
            {
                SetCurrentItem(_context.CurrentItem);
            }

            if (_delayPlay is not null)
            {
                Func<Task> pending = _delayPlay;
                _delayPlay = null;

                await Task.Yield();
                try
                {
                    await pending();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process delayed playback request");
                }
            }
        }
    }

    #endregion

    #region Context Property Change Handlers

    private async void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayQueueContext.CurrentItem):
                await ProcessCurrentItemChangedAsync(_context.CurrentItem);
                break;
            case nameof(PlayQueueContext.ShuffleMode):
                HandleShuffleModeChanged(_context.ShuffleMode);
                break;
            case nameof(PlayQueueContext.RepeatMode):
                HandleRepeatModeChanged(_context.RepeatMode);
                break;
        }
    }

    private void HandleShuffleModeChanged(bool value)
    {
        if (_queueSession.ShuffleMode == value) return;

        QueueSession playlist;
        if (value)
        {
            playlist = _queueService.Shuffle(_queueSession, _context.CurrentIndex);
        }
        else
        {
            if (_queueSession.CanRestoreFromShuffle)
            {
                playlist = _queueService.RestoreFromShuffle(_queueSession);
            }
            else
            {
                playlist = new QueueSession(_queueSession)
                {
                    ShuffleMode = false,
                    ShuffleBackup = null
                };
            }
        }

        ApplyQueueSnapshot(playlist);
    }

    private void HandleRepeatModeChanged(MediaPlaybackAutoRepeatMode value)
    {
        Messenger.Send(new RepeatModeChangedMessage(value));
        _transportControlsService.TransportControls.AutoRepeatMode = value;
        _playbackSettings.RepeatMode = value;
        RaiseCanNavigateChanged();
    }

    #endregion

    #region IPlayQueueCoordinator Implementation

    public bool CanNext()
    {
        return _playbackControlService.CanNext(_queueSession, _context.RepeatMode);
    }

    public bool CanPrevious()
    {
        return _playbackControlService.CanPrevious(_queueSession, _context.RepeatMode);
    }

    public async Task NextAsync()
    {
        var result = await _playbackControlService.GetNextAsync(_queueSession, _context.RepeatMode);
        if (result is not null)
        {
            await ApplyNavigationResultAsync(result);
        }
    }

    public async Task PreviousAsync()
    {
        if (MediaPlayer is null) return;

        if (MediaPlayer.PlaybackState == MediaPlaybackState.Playing &&
            MediaPlayer.Position > TimeSpan.FromSeconds(5))
        {
            MediaPlayer.Position = TimeSpan.Zero;
            return;
        }

        var result = await _playbackControlService.GetPreviousAsync(_queueSession, _context.RepeatMode);
        if (result is not null)
        {
            await ApplyNavigationResultAsync(result);
        }
        else
        {
            MediaPlayer.Position = TimeSpan.Zero;
        }
    }

    public void Clear()
    {
        _pendingSeekItem = null;
        _pendingSeekPosition = TimeSpan.Zero;
        ClearQueue();
        SetCurrentItem(null);
    }

    #endregion

    #region Public Methods

    public void Enqueue(IReadOnlyList<MediaViewModel> items, int insertIndex = -1)
    {
        foreach (var item in items)
        {
            if (insertIndex < 0 || insertIndex >= _context.Items.Count)
            {
                _context.Items.Add(item);
            }
            else
            {
                _context.Items.Insert(insertIndex, item);
                insertIndex++;
            }
        }
    }

    public void Remove(MediaViewModel item)
    {
        if (_context.CurrentItem == item)
        {
            SetCurrentItem(null);
        }

        _bufferManager.CleanPlaybackItem(item);
        _context.Items.Remove(item);
    }

    public void InsertNext(MediaViewModel item)
    {
        _context.Items.Insert(_context.CurrentIndex + 1, new MediaViewModel(item));
    }

    public void ResetCurrentItem()
    {
        var item = _context.CurrentItem;
        TimeSpan resumePos = MediaPlayer?.Position ?? TimeSpan.Zero;
        bool wasPlaying = MediaPlayer?.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Opening;
        bool needsReinit = item is not null && (_playerService?.AreSubtitleSettingsDirty() ?? false);

        SetCurrentItem(null);
        if (item is not null)
        {
            _bufferManager.CleanPlaybackItem(item);
        }

        if (item is not null && resumePos > TimeSpan.Zero)
        {
            _pendingSeekItem = item;
            _pendingSeekPosition = resumePos;
        }
        else
        {
            _pendingSeekItem = null;
            _pendingSeekPosition = TimeSpan.Zero;
        }

        if (needsReinit && item is not null)
        {
            _delayPlay = () =>
            {
                SetCurrentItem(item, shouldPlay: wasPlaying);
                return Task.CompletedTask;
            };
            Messenger.Send(new ReinitializePlayerMessage());
            return;
        }

        SetCurrentItem(item, shouldPlay: wasPlaying);
    }

    #endregion

    #region Private Methods

    private async Task PlayAsync(MediaViewModel vm, bool allowExpand = true)
    {
        if (MediaPlayer is null)
        {
            _delayPlay = () => PlayAsync(vm, allowExpand);
            return;
        }

        if (allowExpand && vm.Source is IExpandableMediaSource expandable)
        {
            await ExpandAndPlayAsync(vm, expandable);
            return;
        }

        SetCurrentItem(vm, shouldPlay: true);
    }

    private bool ReplaceItemInQueue(MediaViewModel target, IReadOnlyList<MediaViewModel> replacements)
    {
        int index = _context.Items.IndexOf(target);
        if (index < 0) return false;

        try
        {
            _deferCollectionChanged = true;
            _context.Items.RemoveAt(index);
            for (int i = 0; i < replacements.Count; i++)
            {
                _context.Items.Insert(index + i, replacements[i]);
            }

            if (_queueSession.ShuffleBackup is { } backup)
            {
                int backupIndex = backup.OriginalItems.IndexOf(target);
                if (backupIndex >= 0)
                {
                    backup.OriginalItems.RemoveAt(backupIndex);
                    for (int i = 0; i < replacements.Count; i++)
                    {
                        backup.OriginalItems.Insert(backupIndex + i, replacements[i]);
                    }
                }
                else
                {
                    _queueSession.ShuffleBackup = null;
                }
            }

            _context.CurrentIndex = _context.CurrentItem is not null
                ? _context.Items.IndexOf(_context.CurrentItem)
                : -1;
            _queueSession = new QueueSession(_context.CurrentIndex, _context.Items, _queueSession);
        }
        finally
        {
            _deferCollectionChanged = false;
        }

        RaiseCanNavigateChanged();
        return true;
    }

    private async Task ExpandAndPlayAsync(MediaViewModel placeholder, IExpandableMediaSource expandable)
    {
        try
        {
            var expandedItems = await expandable.ExpandAsync();
            if (expandedItems.Count == 0)
            {
                _logger.LogInformation("Expandable media source yielded 0 items, playing directly: {Location}", placeholder.Location);
                await PlayAsync(placeholder, allowExpand: false);
                return;
            }

            var newVms = expandedItems.Select(_mediaFactory.Create).ToList();

            bool shouldAutoPlay = _context.CurrentItem == placeholder || _context.CurrentItem is null;

            if (ReplaceItemInQueue(placeholder, newVms))
            {
                if (newVms.Count > 1 && (_queueSession.Origin is null || _queueSession.Origin.TrackItemsIndividually))
                {
                    _queueSession.Origin = QueueOrigin.ForMediaCollection(placeholder.Name, placeholder.Location);
                    _context.ActiveOrigin = _queueSession.Origin;
                    bool isUpgrade = _queueSession.SessionId == _recordedSessionId &&
                                     _recordedOriginKind == RecentItemKind.SingleMedia;
                    _recordedSessionId = _queueSession.SessionId;
                    _recordedOriginKind = RecentItemKind.MediaCollection;

                    MediaViewModel targetItem = shouldAutoPlay && _context.CurrentItem is null
                        ? newVms[0]
                        : (_context.CurrentItem ?? newVms[0]);
                    Messenger.Send(new QueueSessionStartedMessage(
                        new QueueSession(targetItem, _queueSession.Items, _queueSession),
                        isUpgrade));
                }

                if (shouldAutoPlay)
                {
                    await PlayAsync(newVms[0]);
                }
            }
            else
            {
                _logger.LogInformation("Placeholder {Location} is no longer in the queue after expansion; aborting playback.", placeholder.Location);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to expand playlist: {Location}", placeholder.Location);
            Messenger.Send(new NotificationMessage(NotificationLevel.Error, NotificationKind.MediaLoadFailed, message: $"{placeholder.Location}{Environment.NewLine}{ex.Message}"));
        }
    }

    private async Task CheckSubItemsAsync(MediaViewModel vm, IPlaybackItem playbackItem, CancellationToken cancellationToken)
    {
        try
        {
            var subItems = await playbackItem.ExpandSubItemsAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (subItems.Count > 0 && !cancellationToken.IsCancellationRequested && _context.CurrentItem == vm)
            {
                void ReplaceAndPlay()
                {
                    if (_context.CurrentItem != vm) return;

                    var newVms = subItems.Select(_mediaFactory.Create).ToList();
                    if (ReplaceItemInQueue(vm, newVms))
                    {
                        bool shouldUpgradeOrigin = newVms.Count > 1 &&
                                                   (_queueSession.Origin is null || _queueSession.Origin.TrackItemsIndividually);
                        bool isUpgrade = false;
                        if (shouldUpgradeOrigin)
                        {
                            _queueSession.Origin = QueueOrigin.ForMediaCollection(vm.Name, vm.Location);
                            _context.ActiveOrigin = _queueSession.Origin;
                            isUpgrade = _queueSession.SessionId == _recordedSessionId &&
                                        _recordedOriginKind == RecentItemKind.SingleMedia;
                            _recordedSessionId = _queueSession.SessionId;
                            _recordedOriginKind = RecentItemKind.MediaCollection;
                        }

                        SetCurrentItem(newVms[0], shouldPlay: true);

                        if (shouldUpgradeOrigin)
                        {
                            Messenger.Send(new QueueSessionStartedMessage(new QueueSession(_queueSession), isUpgrade));
                        }
                    }
                }

                _dispatcherQueue.TryExecuteOrEnqueue(ReplaceAndPlay);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sub-item parsing completed without additional items for {Location}", vm.Location);
        }
    }

    private void RaiseCanNavigateChanged()
    {
        _transportControlsService.TransportControls.IsNextEnabled = CanNext();
        _transportControlsService.TransportControls.IsPreviousEnabled = CanPrevious();
        Messenger.Send(new QueueCanNavigateChangedMessage());
    }

    private void SetCurrentItem(MediaViewModel? value, bool shouldPlay = false)
    {
        void UpdateState()
        {
            if (value is not null && !ReferenceEquals(value, _pendingSeekItem))
            {
                _pendingSeekItem = null;
                _pendingSeekPosition = TimeSpan.Zero;
            }

            if (value is not null && MediaPlayer is not null && (_playerService?.AreSubtitleSettingsDirty() ?? false))
            {
                _delayPlay = () =>
                {
                    SetCurrentItem(value, shouldPlay);
                    return Task.CompletedTask;
                };
                Messenger.Send(new ReinitializePlayerMessage());
                return;
            }

            if (MediaPlayer is not null)
            {
                MediaPlayer.PlaybackItem = value is not null
                    ? _bufferManager.GetOrCreatePlaybackItem(MediaPlayer, value)
                    : null;
            }

            if (_context.CurrentItem is not null)
            {
                _context.CurrentItem.IsMediaActive = false;
                _context.CurrentItem.IsPlaying = false;
            }

            if (value is not null)
            {
                value.IsMediaActive = true;
                _context.CurrentIndex = _context.Items.IndexOf(value);
                _queueSession.CurrentIndex = _context.CurrentIndex;

                if (_queueSession.Origin is null)
                {
                    _queueSession.Origin = QueueOrigin.ForSingleMedia(value);
                    _context.ActiveOrigin = _queueSession.Origin;
                }
            }
            else
            {
                _context.CurrentIndex = -1;
                _queueSession.CurrentIndex = -1;
                if (_context.Items.Count == 0)
                {
                    _context.ActiveSessionId = Guid.Empty;
                    _context.ActiveOrigin = null;
                }
            }

            _context.CurrentItem = value;
            _context.IsPlaying = MediaPlayer?.PlaybackState == MediaPlaybackState.Playing && value is not null;
            if (value is not null)
            {
                value.IsPlaying = _context.IsPlaying;
            }

            BroadcastPlaybackState();

            if (shouldPlay && value is not null)
            {
                MediaPlayer?.Play();
            }
        }

        _dispatcherQueue.TryExecuteOrEnqueue(UpdateState);
    }

    private async Task ProcessCurrentItemChangedAsync(MediaViewModel? value)
    {
        _currentItemCts?.Cancel();
        _currentItemCts?.Dispose();
        _currentItemCts = null;

        void PropagateCurrentItemChanged()
        {
            Messenger.Send(new QueueCurrentItemChangedMessage(value, _queueSession.NeighboringFilesQuery));
            RaiseCanNavigateChanged();
        }

        _dispatcherQueue.TryExecuteOrEnqueue(PropagateCurrentItemChanged);

        if (value is null)
        {
            _transportControlsService.ClosePlayback();
            return;
        }

        var cts = new CancellationTokenSource();
        _currentItemCts = cts;
        var token = cts.Token;

        try
        {
            _logger.LogInformation("Play queue current item changed. {HasCurrentItem} {MediaType}",
                true,
                value.MediaType.ToString());

            if (_queueSession.Origin is null || _queueSession.Origin.TrackItemsIndividually)
            {
                _dispatcherQueue.TryExecuteOrEnqueue(() =>
                {
                    if (_context.CurrentItem != value) return;
                    _recordedSessionId = _queueSession.SessionId;
                    _recordedOriginKind = RecentItemKind.SingleMedia;
                    var singleQueue = new QueueSession(value)
                    {
                        SessionId = _queueSession.SessionId,
                        Origin = QueueOrigin.ForSingleMedia(value)
                    };
                    Messenger.Send(new QueueSessionStartedMessage(singleQueue, IsUpgrade: false));
                });
            }

            if (value.Source is IUriMediaSource && MediaPlayer?.PlaybackItem is { } currentPlaybackItem)
            {
                await CheckSubItemsAsync(value, currentPlaybackItem, token);
            }

            if (token.IsCancellationRequested || _context.CurrentItem != value) return;

            _bufferManager.UpdateBuffer(MediaPlayer, _queueSession, _context.RepeatMode);
            await Task.WhenAll(
                _generalSettings.ShowRecent ? _filesService.AddToRecentAsync(value.Source, token) : Task.CompletedTask,
                _transportControlsService.UpdateTransportControlsDisplayAsync(value)
            );
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete post-processing for current item: {Location}", value.Location);
        }
    }

    private void ApplyQueueSnapshot(QueueSession playlist, bool shouldPlay = false)
    {
        void UpdateQueue()
        {
            try
            {
                _deferCollectionChanged = true;
                if (!playlist.IsEmpty)
                {
                    playlist.Origin ??= playlist.Items.Count > 1
                        ? QueueOrigin.ForMediaCollection(playlist.Items[0].Name)
                        : QueueOrigin.ForSingleMedia(playlist.Items[0]);
                }

                _queueSession = playlist;
                _context.ActiveSessionId = playlist.IsEmpty ? Guid.Empty : playlist.SessionId;
                _context.ActiveOrigin = playlist.Origin;

                if (!playlist.IsEmpty && playlist.Origin is { TrackItemsIndividually: false } &&
                    (playlist.SessionId != _recordedSessionId || _recordedOriginKind != playlist.Origin.Kind))
                {
                    bool isUpgrade = playlist.SessionId == _recordedSessionId &&
                                     _recordedOriginKind == RecentItemKind.SingleMedia;
                    _recordedSessionId = playlist.SessionId;
                    _recordedOriginKind = playlist.Origin.Kind;
                    Messenger.Send(new QueueSessionStartedMessage(new QueueSession(playlist), isUpgrade));
                }
                _context.ShuffleMode = playlist.ShuffleMode;

                if (_context.Items.Count < 200 && playlist.Items.Count < 200)
                {
                    _context.Items.SyncItems(playlist.Items);
                }
                else if (!_context.Items.SequenceEqual(playlist.Items))
                {
                    _context.Items.Clear();
                    foreach (var item in playlist.Items)
                    {
                        _context.Items.Add(item);
                    }
                }

                bool currentItemUnchanged = ReferenceEquals(_context.CurrentItem, playlist.CurrentItem);
                SetCurrentItem(playlist.CurrentItem, shouldPlay);

                if (currentItemUnchanged && !playlist.IsEmpty && playlist.CurrentItem is { } activeItem &&
                    (playlist.Origin is null || playlist.Origin.TrackItemsIndividually) &&
                    playlist.SessionId != _recordedSessionId)
                {
                    _recordedSessionId = playlist.SessionId;
                    _recordedOriginKind = RecentItemKind.SingleMedia;
                    var singleQueue = new QueueSession(activeItem)
                    {
                        SessionId = playlist.SessionId,
                        Origin = QueueOrigin.ForSingleMedia(activeItem)
                    };
                    Messenger.Send(new QueueSessionStartedMessage(singleQueue, IsUpgrade: false));
                }
            }
            finally
            {
                _deferCollectionChanged = false;
            }
        }

        _dispatcherQueue.TryExecuteOrEnqueue(UpdateQueue);
    }

    private void ClearQueue()
    {
        void Clear()
        {
            _bufferManager.Clear();

            try
            {
                _deferCollectionChanged = true;
                _context.Items.Clear();
                _queueSession = new QueueSession { SessionId = Guid.Empty };
                _context.ActiveSessionId = Guid.Empty;
                _context.ActiveOrigin = null;
                _context.IsPlaying = false;
                _recordedSessionId = Guid.Empty;
                _recordedOriginKind = null;
                _context.ShuffleMode = false;
                BroadcastPlaybackState();
            }
            finally
            {
                _deferCollectionChanged = false;
            }
        }

        _dispatcherQueue.TryExecuteOrEnqueue(Clear);
    }

    private async Task ApplyNavigationResultAsync(PlaybackNavigationResult result)
    {
        if (result.UpdatedQueue is not null)
        {
            ApplyQueueSnapshot(result.UpdatedQueue, shouldPlay: true);
        }
        else
        {
            await PlayAsync(result.NextItem);
        }
    }

    #endregion

    #region Playback State

    public QueuePlaybackState GetCurrentPlaybackState()
    {
        return new QueuePlaybackState(
            _context.ActiveSessionId,
            _context.ActiveOrigin,
            _context.CurrentItem,
            _context.IsPlaying);
    }

    private void BroadcastPlaybackState()
    {
        var state = GetCurrentPlaybackState();
        if (state != _lastBroadcastState)
        {
            _lastBroadcastState = state;
            Messenger.Send(new QueuePlaybackStateChangedMessage(state));
        }
    }

    #endregion

    #region Event Handlers

    private void OnMediaFailed(IMediaPlayer sender, EventArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _pendingSeekItem = null;
            _pendingSeekPosition = TimeSpan.Zero;
            _context.IsPlaying = false;
            if (_context.CurrentItem is not null)
            {
                _context.CurrentItem.IsPlaying = false;
                _context.CurrentItem.IsAvailable = false;
            }

            BroadcastPlaybackState();
        });
    }

    private void OnPlaybackStateChanged(IMediaPlayer sender, object args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            MediaPlaybackState state = sender.PlaybackState;
            bool isPlaying = state == MediaPlaybackState.Playing;
            _context.IsPlaying = isPlaying;

            if (_context.CurrentItem is not null)
            {
                if (isPlaying)
                {
                    _context.CurrentItem.IsAvailable = true;
                }

                _context.CurrentItem.IsPlaying = isPlaying;

                if (_pendingSeekItem is not null &&
                    ReferenceEquals(_context.CurrentItem, _pendingSeekItem) &&
                    state is MediaPlaybackState.Playing or MediaPlaybackState.Paused)
                {
                    TimeSpan targetPos = _pendingSeekPosition;
                    _pendingSeekItem = null;
                    _pendingSeekPosition = TimeSpan.Zero;

                    if (targetPos > TimeSpan.Zero)
                    {
                        sender.Position = targetPos;
                    }
                }
            }

            BroadcastPlaybackState();
        });
    }

    private async void OnEndReached(IMediaPlayer sender, object? args)
    {
        await Task.Run(async () =>
        {
            try
            {
                var result = _playbackControlService.HandleMediaEnded(_queueSession, _context.RepeatMode);
                if (result is not null)
                {
                    await ApplyNavigationResultAsync(result);
                }
                else if (_context.RepeatMode == MediaPlaybackAutoRepeatMode.Track)
                {
                    sender.Position = TimeSpan.Zero;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to advance playlist on media ended");
            }
        });
    }

    private void TransportControlsOnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                switch (args.Button)
                {
                    case SystemMediaTransportControlsButton.Next:
                        await NextAsync();
                        break;
                    case SystemMediaTransportControlsButton.Previous:
                        await PreviousAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle transport controls button {Button}", args.Button);
            }
        });
    }

    private void TransportControlsOnAutoRepeatModeChangeRequested(SystemMediaTransportControls sender, AutoRepeatModeChangeRequestedEventArgs args)
    {
        void Update()
        {
            _context.RepeatMode = args.RequestedAutoRepeatMode;
        }

        _dispatcherQueue.TryExecuteOrEnqueue(Update);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_deferCollectionChanged) return;

        if (_context.Items.Count == 0)
        {
            _context.CurrentIndex = -1;
            _queueSession = new QueueSession { SessionId = Guid.Empty };
            _context.ActiveSessionId = Guid.Empty;
            _context.ActiveOrigin = null;
            _recordedSessionId = Guid.Empty;
            _recordedOriginKind = null;
            RaiseCanNavigateChanged();
            BroadcastPlaybackState();
            return;
        }

        _context.CurrentIndex = _context.CurrentItem is not null
            ? _context.Items.IndexOf(_context.CurrentItem)
            : -1;
        _queueSession = new QueueSession(_context.CurrentIndex, _context.Items, _queueSession);
        if (_queueSession.SessionId == Guid.Empty || _context.ActiveSessionId == Guid.Empty)
        {
            if (_queueSession.SessionId == Guid.Empty)
            {
                _queueSession.SessionId = Guid.NewGuid();
            }

            _queueSession.Origin ??= _context.Items.Count > 1
                ? QueueOrigin.ForMediaCollection(_context.Items[0].Name)
                : QueueOrigin.ForSingleMedia(_context.Items[0]);
            _context.ActiveSessionId = _queueSession.SessionId;
            _context.ActiveOrigin = _queueSession.Origin;
            BroadcastPlaybackState();
        }

        RaiseCanNavigateChanged();

        _queueService.UpdateShuffleBackup(_queueSession, e);
    }

    #endregion
}
