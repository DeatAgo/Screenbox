using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Screenbox.Core.Contexts;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml.Data;

namespace Screenbox.Core.ViewModels;

public partial class PlaylistsPageViewModel : ObservableRecipient
{
    public SelectionViewModel Selection { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MovePlaylistUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MovePlaylistDownCommand))]
    public partial PlaylistViewModel? ContextPlaylist { get; set; }

    private readonly IFilesService _filesService;
    private readonly IPlaylistService _playlistService;
    private readonly PlaylistsContext _playlistsContext;
    private readonly DispatcherQueueTimer _orderSaveTimer;

    public ObservableCollection<PlaylistViewModel> Playlists => _playlistsContext.Playlists;

    [ObservableProperty] public partial PlaylistViewModel? SelectedPlaylist { get; set; }

    public PlaylistsPageViewModel(IFilesService filesService, IPlaylistService playlistService,
        PlaylistsContext playlistsContext, SelectionViewModel selection)
    {
        Selection = selection;
        _filesService = filesService;
        _playlistService = playlistService;
        _playlistsContext = playlistsContext;
        _orderSaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();

        Selection.SetItemsSource(Playlists);
        ((INotifyCollectionChanged)Selection.SelectedRanges).CollectionChanged += Selection_SelectedRangesChanged;
        Playlists.CollectionChanged += Playlists_CollectionChanged;
    }

    private void Playlists_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MovePlaylistUpCommand.NotifyCanExecuteChanged();
        MovePlaylistDownCommand.NotifyCanExecuteChanged();
        MoveSelectedPlaylistUpCommand.NotifyCanExecuteChanged();
        MoveSelectedPlaylistDownCommand.NotifyCanExecuteChanged();

        // When drag-and-drop reorders the GridView, UWP fires NotifyCollectionChangedAction.Move.
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            SchedulePersistOrder();
        }
    }

    private void Selection_SelectedRangesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PlaySelectedCommand.NotifyCanExecuteChanged();
        PlaySelectedNextCommand.NotifyCanExecuteChanged();
        AddSelectedToQueueCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        MoveSelectedPlaylistUpCommand.NotifyCanExecuteChanged();
        MoveSelectedPlaylistDownCommand.NotifyCanExecuteChanged();
    }

    private void SchedulePersistOrder()
    {
        _orderSaveTimer.Debounce(async () =>
        {
            await PersistPlaylistOrderAsync();
        }, TimeSpan.FromMilliseconds(150));
    }

    public async Task PersistPlaylistOrderAsync()
    {
        DateTimeOffset baseTime = DateTimeOffset.Now;
        for (int i = 0; i < Playlists.Count; i++)
        {
            await Playlists[i].SaveWithTimestampAsync(baseTime.AddMilliseconds(-i));
        }
    }

    private bool IsSelectionSingle() =>
        Selection.SelectedRanges.Count == 1 && Selection.SelectedRanges[0] is { Length: 1 };

    private bool IsSelectedPlaylistNotFirst() =>
        IsSelectionSingle() && Playlists.Count > 0 && Selection.SelectedRanges[0].FirstIndex > 0;

    private bool IsSelectedPlaylistNotLast() =>
        IsSelectionSingle() && Playlists.Count > 0 && Selection.SelectedRanges[0].LastIndex < Playlists.Count - 1;

    private bool CanMovePlaylistUp(PlaylistViewModel? playlist) =>
        playlist is not null && Playlists.IndexOf(playlist) > 0;

    private bool CanMovePlaylistDown(PlaylistViewModel? playlist)
    {
        if (playlist is null) return false;
        int index = Playlists.IndexOf(playlist);
        return index >= 0 && index < Playlists.Count - 1;
    }

    [RelayCommand(CanExecute = nameof(CanMovePlaylistUp))]
    private async Task MovePlaylistUpAsync(PlaylistViewModel? playlist)
    {
        if (playlist is null) return;
        int index = Playlists.IndexOf(playlist);
        if (index <= 0) return;

        Playlists.RemoveAt(index);
        Playlists.Insert(index - 1, playlist);
        await PersistPlaylistOrderAsync();
    }

    [RelayCommand(CanExecute = nameof(CanMovePlaylistDown))]
    private async Task MovePlaylistDownAsync(PlaylistViewModel? playlist)
    {
        if (playlist is null) return;
        int index = Playlists.IndexOf(playlist);
        if (index < 0 || index >= Playlists.Count - 1) return;

        Playlists.RemoveAt(index);
        Playlists.Insert(index + 1, playlist);
        await PersistPlaylistOrderAsync();
    }

    [RelayCommand(CanExecute = nameof(IsSelectedPlaylistNotFirst))]
    private async Task MoveSelectedPlaylistUpAsync()
    {
        if (!IsSelectedPlaylistNotFirst()) return;
        int oldIndex = Selection.SelectedRanges[0].FirstIndex;
        PlaylistViewModel item = Playlists[oldIndex];

        int newIndex = oldIndex - 1;
        Playlists.Insert(newIndex, item);
        Playlists.RemoveAt(oldIndex + 1);
        Selection.SelectRange(new ItemIndexRange(newIndex, 1));
        await PersistPlaylistOrderAsync();
    }

    [RelayCommand(CanExecute = nameof(IsSelectedPlaylistNotLast))]
    private async Task MoveSelectedPlaylistDownAsync()
    {
        if (!IsSelectedPlaylistNotLast()) return;
        int oldIndex = Selection.SelectedRanges[0].FirstIndex;
        PlaylistViewModel item = Playlists[oldIndex];

        int newIndex = oldIndex + 2;
        Playlists.Insert(newIndex, item);
        Selection.SelectRange(new ItemIndexRange(newIndex, 1));
        Playlists.RemoveAt(oldIndex);
        await PersistPlaylistOrderAsync();
    }

    public async Task CreatePlaylistAsync(string displayName)
    {
        var playlist = Ioc.Default.GetRequiredService<PlaylistViewModel>();
        playlist.Name = displayName;
        await playlist.SaveAsync();

        Playlists.Insert(0, playlist);
        Messenger.Send(new NotificationMessage(NotificationLevel.Success, NotificationKind.PlaylistCreated, title: displayName));
    }

    public async Task RenamePlaylistAsync(PlaylistViewModel playlist, string newDisplayName)
    {
        await playlist.RenameAsync(newDisplayName);
        Messenger.Send(new NotificationMessage(NotificationLevel.Success, NotificationKind.PlaylistRenamed, title: newDisplayName));
    }

    public async Task DeletePlaylistAsync(PlaylistViewModel playlist)
    {
        string playlistName = playlist.Name;
        playlist.DeleteStoredThumbnailFile();
        await _playlistService.DeletePlaylistAsync(playlist.Id);
        Playlists.Remove(playlist);
        Messenger.Send(new NotificationMessage(NotificationLevel.Success, NotificationKind.PlaylistDeleted, title: playlistName));
    }

    [RelayCommand]
    private async Task ImportPlaylistAsync()
    {
        StorageFile? file = await _filesService.PickFileAsync(".m3u8", ".m3u");
        if (file is null) return;

        IReadOnlyList<MediaViewModel> items = await _playlistService.ImportPlaylistItemsAsync(file);
        if (items.Count == 0) return;

        var playlist = Ioc.Default.GetRequiredService<PlaylistViewModel>();
        playlist.Name = file.DisplayName;
        await playlist.AddItemsAsync(items);
        Playlists.Insert(0, playlist);
        Messenger.Send(new NotificationMessage(NotificationLevel.Success, NotificationKind.PlaylistCreated, title: playlist.Name));
    }

    public async Task ExportPlaylistAsync(PlaylistViewModel playlist, string playlistFileDisplayName = "M3U8")
    {
        var saveFileTypes = new Dictionary<string, IList<string>> { [playlistFileDisplayName] = [".m3u8"] };
        StorageFile? file = await _filesService.PickSaveFileAsync(playlist.Name,
            saveFileTypes, Windows.Storage.Pickers.PickerLocationId.MusicLibrary);
        if (file is null) return;

        await _playlistService.ExportPlaylistItemsAsync(playlist.Items, file);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlaySelected()
    {
        var selectedPlaylists = Selection.GetSelectedItems<PlaylistViewModel>();
        var items = selectedPlaylists
            .SelectMany(p => p.Items)
            .ToArray();
        if (items.Length > 0)
        {
            var origin = selectedPlaylists.Count == 1
                ? QueueOrigin.ForPlaylist(selectedPlaylists[0])
                : QueueOrigin.ForMediaCollection(items[0].Name);
            Messenger.SendQueueAndPlay(items[0], items, origin: origin);
        }
        Selection.DisableSelectionMode();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlaySelectedNext()
    {
        var selectedItems = Selection.GetSelectedItems<PlaylistViewModel>();
        if (selectedItems.Count == 0)
            return;

        selectedItems.Reverse();
        foreach (var item in selectedItems)
        {
            Messenger.SendPlayNext(item.Items);
        }

        Selection.DisableSelectionMode();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddSelectedToQueue()
    {
        var selectedItems = Selection.GetSelectedItems<PlaylistViewModel>();
        foreach (var item in selectedItems)
        {
            Messenger.SendAddToQueue(item.Items);
        }

        Selection.DisableSelectionMode();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelectedAsync()
    {
        var selectedItems = Selection.GetSelectedItems<PlaylistViewModel>();
        Selection.DisableSelectionMode();
        foreach (var item in selectedItems)
        {
            await DeletePlaylistAsync(item);
        }
    }

    private bool HasSelection => Selection.SelectedRanges.Count > 0;
}
