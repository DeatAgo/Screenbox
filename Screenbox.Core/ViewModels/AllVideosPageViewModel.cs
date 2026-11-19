using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using CommunityToolkit.WinUI;
using Screenbox.Core.Contexts;
using Screenbox.Core.Helpers;
using Screenbox.Core.Models;
using Windows.System;

namespace Screenbox.Core.ViewModels;

public sealed partial class AllVideosPageViewModel : ObservableRecipient,
    IRecipient<PropertyChangedMessage<VideosLibrary>>
{
    public SelectionViewModel Selection { get; }

    [ObservableProperty] public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial MediaViewModel? ContextMedia { get; set; }

    public ObservableCollection<MediaViewModel> Videos { get; }

    private readonly LibraryContext _libraryContext;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _timer;

    public AllVideosPageViewModel(LibraryContext libraryContext, SelectionViewModel selection)
    {
        _libraryContext = libraryContext;
        Selection = selection;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _timer = _dispatcherQueue.CreateTimer();
        Videos = new ObservableCollection<MediaViewModel>();

        Selection.SetItemsSource(Videos);
        ((INotifyCollectionChanged)Selection.SelectedRanges).CollectionChanged += Selection_SelectedRangesChanged;

        Messenger.Register<PropertyChangedMessage<VideosLibrary>>(this);
    }

    private void Selection_SelectedRangesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PlaySelectedCommand.NotifyCanExecuteChanged();
        PlaySelectedNextCommand.NotifyCanExecuteChanged();
        AddSelectedToQueueCommand.NotifyCanExecuteChanged();
    }

    public void Receive(PropertyChangedMessage<VideosLibrary> message)
    {
        _dispatcherQueue.TryEnqueue(UpdateVideos);
    }

    public void UpdateVideos()
    {
        IsLoading = _libraryContext.IsLoadingVideos;
        IReadOnlyList<MediaViewModel> videos = _libraryContext.Videos.Videos;
        if (videos.Count < 5000)
        {
            Videos.SyncItems(videos);
        }
        else
        {
            Videos.Clear();
            foreach (MediaViewModel video in videos)
            {
                Videos.Add(video);
            }
        }

        if (Videos.Count == 0)
        {
            Selection.IsSelectionModeActive = false;
        }

        if (IsLoading)
        {
            _timer.Debounce(UpdateVideos, TimeSpan.FromSeconds(5));
        }
        else
        {
            _timer.Stop();
        }
    }

    private bool HasSelection() => Selection.SelectedRanges.Count > 0;

    [RelayCommand]
    private void Play(MediaViewModel media)
    {
        if (Videos.Count == 0) return;
        Messenger.SendQueueAndPlay(media, Videos, true, QueueOrigin.ForLibraryQueue(media));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlaySelected()
    {
        List<MediaViewModel> selected = Selection.GetSelectedItems<MediaViewModel>();
        if (selected.Count == 0) return;
        Selection.DisableSelectionMode();
        Messenger.SendQueueAndPlay(selected[0], selected, true, QueueOrigin.ForLibraryQueue(selected[0]));
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
    private void AddSelectedToQueue()
    {
        List<MediaViewModel> selected = Selection.GetSelectedItems<MediaViewModel>();
        if (selected.Count == 0) return;
        Selection.ClearSelection();
        Messenger.SendAddToQueue(selected);
    }
}
