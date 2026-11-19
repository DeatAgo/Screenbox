using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Screenbox.Core.Contexts;
using Screenbox.Core.Enums;
using Screenbox.Core.Factories;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml.Data;

namespace Screenbox.Core.ViewModels;

public sealed partial class PlaylistDetailsPageViewModel : ObservableRecipient
{
    public event EventHandler? SubtextChanged;

    public SelectionViewModel Selection { get; }

    [ObservableProperty]
    public partial PlaylistViewModel? Source { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveItemUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveItemDownCommand))]
    public partial MediaViewModel? ContextMedia { get; set; }

    [ObservableProperty]
    public partial MediaViewModel? FirstItem { get; set; }

    [ObservableProperty]
    public partial bool HasItems { get; set; }

    private readonly IFilesService _filesService;
    private readonly ISourceMetadataService _sourceMetadataService;
    private readonly IPlaylistService _playlistService;
    private readonly IQueueService _queueService;
    private readonly PlaylistsContext _playlistsContext;
    private readonly MediaViewModelFactory _mediaFactory;
    private readonly DispatcherQueueTimer _commandRefreshTimer;
    private readonly HashSet<MediaViewModel> _subscribedItems = new();

    public PlaylistDetailsPageViewModel(
        IFilesService filesService,
        ISourceMetadataService sourceMetadataService,
        IPlaylistService playlistService,
        IQueueService queueService,
        PlaylistsContext playlistsContext,
        MediaViewModelFactory mediaFactory,
        SelectionViewModel selection)
    {
        _filesService = filesService;
        _sourceMetadataService = sourceMetadataService;
        _playlistService = playlistService;
        _queueService = queueService;
        _playlistsContext = playlistsContext;
        _mediaFactory = mediaFactory;
        Selection = selection;
        _commandRefreshTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();

        ((INotifyCollectionChanged)Selection.SelectedRanges).CollectionChanged += Selection_SelectedRangesChanged;
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (Source is not null)
        {
            Source.Items.CollectionChanged -= SourceItems_CollectionChanged;
            UnsubscribeAllItems();
        }

        Selection.IsSelectionModeActive = false;

        Source = parameter switch
        {
            NavigationMetadata { Parameter: PlaylistViewModel source } => source,
            PlaylistViewModel source => source,
            _ => throw new ArgumentException("Navigation parameter is not a playlist")
        };

        if (Source is not null)
        {
            Selection.SetItemsSource(Source.Items);
            Source.Items.CollectionChanged += SourceItems_CollectionChanged;
            SyncItemSubscriptions(Source.Items);
            _ = EnsureItemsMetadataLoadedAsync(Source.Items);
        }

        HasItems = Source?.Items.Count > 0;
        FirstItem = Source?.Items.FirstOrDefault();
        SubtextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Selection_SelectedRangesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PlaySelectedNextCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveSelectedItemUpCommand.NotifyCanExecuteChanged();
        MoveSelectedItemDownCommand.NotifyCanExecuteChanged();
    }

    private void SourceItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Source is not null)
        {
            SyncItemSubscriptions(Source.Items);
            _ = EnsureItemsMetadataLoadedAsync(Source.Items);
        }

        HasItems = Source?.Items.Count > 0;
        if (!HasItems)
        {
            Selection.IsSelectionModeActive = false;
        }

        FirstItem = Source?.Items.FirstOrDefault();
        SubtextChanged?.Invoke(this, EventArgs.Empty);

        _commandRefreshTimer.Debounce(() =>
        {
            PlayCommand.NotifyCanExecuteChanged();
            ShuffleAndPlayCommand.NotifyCanExecuteChanged();
            MoveItemUpCommand.NotifyCanExecuteChanged();
            MoveItemDownCommand.NotifyCanExecuteChanged();
            MoveSelectedItemUpCommand.NotifyCanExecuteChanged();
            MoveSelectedItemDownCommand.NotifyCanExecuteChanged();
        }, TimeSpan.FromMilliseconds(50));
    }

    private void SyncItemSubscriptions(IEnumerable<MediaViewModel> items)
    {
        var currentSet = new HashSet<MediaViewModel>(items);

        foreach (MediaViewModel removed in _subscribedItems.Where(m => !currentSet.Contains(m)).ToList())
        {
            removed.PropertyChanged -= Item_PropertyChanged;
            _subscribedItems.Remove(removed);
        }

        foreach (MediaViewModel added in currentSet)
        {
            if (_subscribedItems.Add(added))
            {
                added.PropertyChanged += Item_PropertyChanged;
            }
        }
    }

    private void UnsubscribeAllItems()
    {
        foreach (MediaViewModel item in _subscribedItems)
        {
            item.PropertyChanged -= Item_PropertyChanged;
        }

        _subscribedItems.Clear();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaViewModel.Duration) or nameof(MediaViewModel.MediaInfo))
        {
            SubtextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task EnsureItemsMetadataLoadedAsync(IEnumerable<MediaViewModel> items)
    {
        List<MediaViewModel> missingDurationItems = items
            .Where(m => m.Duration == TimeSpan.Zero && m.IsAvailable)
            .ToList();

        if (missingDurationItems.Count == 0) return;

        await Task.WhenAll(missingDurationItems.Select(m => m.LoadDetailsAsync(_sourceMetadataService)));
        SubtextChanged?.Invoke(this, EventArgs.Empty);

        if (Source is not null)
        {
            await Source.SaveAsync();
        }
    }

    private static bool NotNull(MediaViewModel? item) => item != null;

    private static bool NotEmpty(PlaylistViewModel? playlist) => playlist?.ItemsCount > 0;

    private bool HasSelection() => Selection.SelectedRanges.Count > 0;

    private bool IsSelectionSingle() =>
        Selection.SelectedRanges.Count == 1 && Selection.SelectedRanges[0] is { Length: 1 };

    private bool IsSelectedItemNotFirst() =>
        Source is not null &&
        IsSelectionSingle() &&
        Source.Items.Count > 0 &&
        Selection.SelectedRanges[0].FirstIndex > 0;

    private bool IsSelectedItemNotLast() =>
        Source is not null &&
        IsSelectionSingle() &&
        Source.Items.Count > 0 &&
        Selection.SelectedRanges[0].LastIndex < Source.Items.Count - 1;

    private bool CanMoveItemUp(MediaViewModel? item) =>
        Source is not null && item is not null && Source.Items.IndexOf(item) > 0;

    private bool CanMoveItemDown(MediaViewModel? item)
    {
        if (Source is null || item is null) return false;
        int index = Source.Items.IndexOf(item);
        return index >= 0 && index < Source.Items.Count - 1;
    }

    [RelayCommand(CanExecute = nameof(IsSelectedItemNotFirst))]
    private async Task MoveSelectedItemUpAsync()
    {
        if (Source is null || !IsSelectedItemNotFirst()) return;
        int oldIndex = Selection.SelectedRanges[0].FirstIndex;
        MediaViewModel item = Source.Items[oldIndex];

        // Insert first, remove old, then reselect to trigger ListView animation while keeping selection.
        int newIndex = oldIndex - 1;
        Source.Items.Insert(newIndex, item);
        Source.Items.RemoveAt(oldIndex + 1);
        Selection.SelectRange(new ItemIndexRange(newIndex, 1));
        await Source.SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(IsSelectedItemNotLast))]
    private async Task MoveSelectedItemDownAsync()
    {
        if (Source is null || !IsSelectedItemNotLast()) return;
        int oldIndex = Selection.SelectedRanges[0].FirstIndex;
        MediaViewModel item = Source.Items[oldIndex];

        // Insert first, reselect, then remove old to trigger ListView animation while keeping selection.
        int newIndex = oldIndex + 2;
        Source.Items.Insert(newIndex, item);
        Selection.SelectRange(new ItemIndexRange(newIndex, 1));
        Source.Items.RemoveAt(oldIndex);
        await Source.SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(CanMoveItemUp))]
    private async Task MoveItemUpAsync(MediaViewModel? item)
    {
        if (Source is null || item is null) return;
        int index = Source.Items.IndexOf(item);
        if (index <= 0) return;

        // RemoveAt + Insert triggers ListView's native reorder slide animation.
        Source.Items.RemoveAt(index);
        Source.Items.Insert(index - 1, item);
        MoveItemUpCommand.NotifyCanExecuteChanged();
        MoveItemDownCommand.NotifyCanExecuteChanged();
        await Source.SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(CanMoveItemDown))]
    private async Task MoveItemDownAsync(MediaViewModel? item)
    {
        if (Source is null || item is null) return;
        int index = Source.Items.IndexOf(item);
        if (index < 0 || index >= Source.Items.Count - 1) return;

        // RemoveAt + Insert triggers ListView's native reorder slide animation.
        Source.Items.RemoveAt(index);
        Source.Items.Insert(index + 1, item);
        MoveItemUpCommand.NotifyCanExecuteChanged();
        MoveItemDownCommand.NotifyCanExecuteChanged();
        await Source.SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlaySelectedNext()
    {
        List<MediaViewModel> selected = Selection.GetSelectedItems<MediaViewModel>();
        if (selected.Count == 0) return;
        Selection.ClearSelection();
        Messenger.SendPlayNext(selected);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveSelectedAsync()
    {
        if (Source is null) return;
        List<MediaViewModel> copy = Selection.GetSelectedItems<MediaViewModel>();
        if (copy.Count == 0) return;

        Selection.ClearSelection();
        foreach (MediaViewModel item in copy)
        {
            Source.Items.Remove(item);
        }

        await Source.SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(NotNull))]
    private void Play(MediaViewModel? item)
    {
        if (Source == null || item == null) return;
        var queue = new QueueSession(item, Source.Items)
        {
            Origin = QueueOrigin.ForPlaylist(Source)
        };
        Messenger.Send(new SetQueueMessage(queue, true));
    }

    [RelayCommand(CanExecute = nameof(NotEmpty))]
    private void ShuffleAndPlay(PlaylistViewModel? playlist)
    {
        if (playlist == null || playlist.Items.Count == 0) return;
        Messenger.SendShuffleAndPlay(playlist.Items, QueueOrigin.ForPlaylist(playlist));
    }

    [RelayCommand(CanExecute = nameof(NotNull))]
    private async Task Remove(MediaViewModel? item)
    {
        if (Source == null || item == null) return;
        Source.Items.Remove(item);
        await Source.SaveAsync();
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (Source == null) return;

        IReadOnlyList<StorageFile>? files = await _filesService.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;

        var items = files.Where(f => f.IsSupported()).Select(_mediaFactory.GetOrCreate).ToList();
        if (items.Count == 0) return;

        await Task.WhenAll(items.Select(m => m.LoadDetailsAsync(_sourceMetadataService)));
        await Source.AddItemsAsync(items);
        SubtextChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddDroppedItemsAsync(IReadOnlyList<IStorageItem> items, int insertIndex)
    {
        if (Source is null) return;

        var queue = await _queueService.TryParseQueueAsync(items);
        if (queue?.Items.Count > 0)
        {
            await Task.WhenAll(queue.Items.Select(m => m.LoadDetailsAsync(_sourceMetadataService)));
            await Source.AddItemsAtIndexAsync(queue.Items, insertIndex);
            SubtextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task ExportPlaylistAsync(string playlistFileDisplayName = "M3U8")
    {
        if (Source == null) return;

        var saveFileTypes = new Dictionary<string, IList<string>> { [playlistFileDisplayName] = [".m3u8"] };
        StorageFile? file = await _filesService.PickSaveFileAsync(Source.Name,
            saveFileTypes, Windows.Storage.Pickers.PickerLocationId.MusicLibrary);
        if (file is null) return;

        await _playlistService.ExportPlaylistItemsAsync(Source.Items, file);
    }

    public async Task<bool> DeletePlaylistAsync()
    {
        if (Source == null) return false;

        PlaylistViewModel target = Source;
        string playlistName = target.Name;
        target.DeleteStoredThumbnailFile();
        await _playlistService.DeletePlaylistAsync(target.Id);
        _playlistsContext.Playlists.Remove(target);
        Messenger.Send(new NotificationMessage(NotificationLevel.Success, NotificationKind.PlaylistDeleted, title: playlistName));
        return true;
    }

    public async Task RenamePlaylistAsync(string newDisplayName)
    {
        if (Source == null) return;
        await Source.RenameAsync(newDisplayName);
        Messenger.Send(new NotificationMessage(NotificationLevel.Success, NotificationKind.PlaylistRenamed, title: newDisplayName));
    }
}
