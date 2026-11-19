using Screenbox.Core.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Screenbox.Core.Factories;
using Screenbox.Core.Helpers;
using Screenbox.Core.MediaSources;
using Screenbox.Core.Messages;
using Screenbox.Core.Services;
using Windows.Storage;
using Windows.Storage.Search;
using Windows.System;
using Windows.UI.Xaml.Data;

namespace Screenbox.Core.ViewModels;

public partial class FolderViewPageViewModel : ObservableRecipient,
    IRecipient<RefreshFolderMessage>
{
    public string TitleText => BreadcrumbLocations.LastOrDefault()?.Name ?? string.Empty;

    public ObservableCollection<StorageItemViewModel> Items { get; } = [];

    public SelectionViewModel Selection { get; }

    public string[] Breadcrumbs { get; private set; } = [];

    public StorageFolder[] BreadcrumbLocations { get; private set; } = [];

    internal NavigationMetadata? NavData { get; private set; }

    [ObservableProperty] public partial StorageItemViewModel? ContextItem { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasVideos { get; set; }
    [ObservableProperty] public partial bool? AreAllVideosSelected { get; set; } = false;

    private readonly IFilesService _filesService;
    private readonly INavigationService _navigationService;
    private readonly StorageItemViewModelFactory _storageVmFactory;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _loadingTimer;
    private readonly List<MediaViewModel> _playableItems = [];
    private bool _isActive;
    private object? _source;

    public FolderViewPageViewModel(
        IFilesService filesService,
        INavigationService navigationService,
        StorageItemViewModelFactory storageVmFactory)
        : this(filesService, navigationService, storageVmFactory, new SelectionViewModel())
    {
    }

    [ActivatorUtilitiesConstructor]
    public FolderViewPageViewModel(
        IFilesService filesService,
        INavigationService navigationService,
        StorageItemViewModelFactory storageVmFactory,
        SelectionViewModel selection)
    {
        _filesService = filesService;
        _storageVmFactory = storageVmFactory;
        _navigationService = navigationService;
        Selection = selection;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _loadingTimer = _dispatcherQueue.CreateTimer();

        Selection.SetItemsSource(Items);
        ((INotifyCollectionChanged)Selection.SelectedRanges).CollectionChanged += Selection_SelectedRangesChanged;

        Messenger.Register<RefreshFolderMessage>(this);
    }

    private void Selection_SelectedRangesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PlaySelectedCommand.NotifyCanExecuteChanged();
        PlaySelectedNextCommand.NotifyCanExecuteChanged();
        AddSelectedToQueueCommand.NotifyCanExecuteChanged();
        UpdateAreAllVideosSelected();
    }

    private void UpdateAreAllVideosSelected()
    {
        int videoCount = Items.Count(i => i.Media != null);
        int selectedCount = Selection.SelectedRanges.Sum(r => (int)r.Length);
        HasVideos = videoCount > 0;
        if (videoCount == 0 || selectedCount == 0)
        {
            AreAllVideosSelected = false;
        }
        else if (selectedCount >= videoCount)
        {
            AreAllVideosSelected = true;
        }
        else
        {
            AreAllVideosSelected = null;
        }
    }

    public void Receive(RefreshFolderMessage message)
    {
        if (!_isActive) return;
        _dispatcherQueue.TryEnqueue(RefreshFolderContent);
    }

    public async Task OnNavigatedTo(object? parameter)
    {
        _isActive = true;
        _source = parameter;
        Selection.DisableSelectionMode();
        NavData = parameter as NavigationMetadata;
        await FetchContentAsync(NavData?.Parameter ?? parameter);
    }

    public void OnBreadcrumbBarItemClicked(int index)
    {
        IReadOnlyList<StorageFolder> crumbs = BreadcrumbLocations.Take(index + 1).ToArray();
        if (NavData != null)
        {
            if (index == 0)
            {
                _navigationService.Navigate(NavData.RootViewModelType);
            }
            else
            {
                _navigationService.Navigate(typeof(FolderViewPageViewModel),
                    new NavigationMetadata(NavData.RootViewModelType, crumbs));
            }
        }
        else
        {
            _navigationService.Navigate(typeof(FolderViewPageViewModel),
                new NavigationMetadata(typeof(FolderViewPageViewModel), crumbs));
        }
    }

    [DynamicWindowsRuntimeCast(typeof(StorageLibrary))]
    [DynamicWindowsRuntimeCast(typeof(StorageFileQueryResult))]
    private async Task FetchContentAsync(object? parameter)
    {
        switch (parameter)
        {
            case IReadOnlyList<StorageFolder> { Count: > 0 } breadcrumbs:
                BreadcrumbLocations = [.. breadcrumbs];
                Breadcrumbs = breadcrumbs.Select(f => f.DisplayName).ToArray();
                await FetchFolderContentAsync(breadcrumbs.Last());
                break;
            case StorageLibrary library:
                await FetchFolderContentAsync(library);
                break;
            case StorageFileQueryResult queryResult:
                await FetchQueryItemAsync(queryResult);
                break;
            case "VideosLibrary":
                try
                {
                    BreadcrumbLocations = [SystemInformation.IsXbox ? KnownFolders.RemovableDevices : KnownFolders.VideosLibrary];
                    Breadcrumbs = [BreadcrumbLocations[0].DisplayName];
                    NavData = new NavigationMetadata(typeof(VideosPageViewModel), BreadcrumbLocations);
                    await FetchFolderContentAsync(BreadcrumbLocations[0]);
                }
                catch (Exception)
                {
                }
                break;
        }

        UpdateAreAllVideosSelected();
    }

    public void OnNavigatedFrom()
    {
        _isActive = false;
    }

    protected virtual void Navigate(object? parameter = null)
    {
        _navigationService.Navigate(typeof(FolderViewPageViewModel),
            new NavigationMetadata(NavData?.RootViewModelType ?? typeof(FolderViewPageViewModel), parameter));
    }

    [RelayCommand]
    private void SelectAllOrNoneVideos()
    {
        if (AreAllVideosSelected == true)
        {
            Selection.ClearSelection();
            return;
        }

        var videoRanges = new List<ItemIndexRange>();
        int start = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            bool isVideo = Items[i].Media != null;
            if (isVideo && start == -1)
            {
                start = i;
            }
            else if (!isVideo && start != -1)
            {
                videoRanges.Add(new ItemIndexRange(start, (uint)(i - start)));
                start = -1;
            }
        }

        if (start != -1)
        {
            videoRanges.Add(new ItemIndexRange(start, (uint)(Items.Count - start)));
        }

        Selection.SetRanges(videoRanges);
    }

    private List<MediaViewModel> GetSelectedVideos() =>
        Selection.GetSelectedItems<StorageItemViewModel>()
            .Where(i => i.Media != null)
            .Select(i => i.Media!)
            .ToList();

    private bool HasSelectedVideos() => GetSelectedVideos().Count > 0;

    [RelayCommand]
    private void Play(StorageItemViewModel? item)
    {
        if (item?.Media == null) return;
        if (item.Media.Source is IExpandableMediaSource)
        {
            Messenger.Send(new PlayMediaMessage(item.Media, false));
        }
        else
        {
            var origin = _playableItems.Count > 1
                ? QueueOrigin.ForMediaCollection(
                    !string.IsNullOrEmpty(TitleText) ? TitleText : item.Media.Name,
                    BreadcrumbLocations.LastOrDefault()?.Path)
                : QueueOrigin.ForSingleMedia(item.Media);
            Messenger.SendQueueAndPlay(item.Media, _playableItems, true, origin);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedVideos))]
    private void PlaySelected()
    {
        List<MediaViewModel> selected = GetSelectedVideos();
        if (selected.Count == 0) return;
        Selection.DisableSelectionMode();
        var origin = QueueOrigin.ForMediaCollection(
            !string.IsNullOrEmpty(TitleText) ? TitleText : selected[0].Name,
            BreadcrumbLocations.LastOrDefault()?.Path);
        Messenger.SendQueueAndPlay(selected[0], selected, true, origin);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedVideos))]
    private void PlaySelectedNext()
    {
        List<MediaViewModel> selected = GetSelectedVideos();
        if (selected.Count == 0) return;
        Selection.ClearSelection();
        Messenger.SendPlayNext(selected);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedVideos))]
    private void AddSelectedToQueue()
    {
        List<MediaViewModel> selected = GetSelectedVideos();
        if (selected.Count == 0) return;
        Selection.ClearSelection();
        Messenger.SendAddToQueue(selected);
    }

    [RelayCommand]
    private void PlayNext(StorageItemViewModel? item)
    {
        if (item?.Media == null) return;
        Messenger.SendPlayNext(item.Media);
    }

    [RelayCommand]
    private void AddToQueue(StorageItemViewModel? item)
    {
        if (item?.Media == null) return;
        Messenger.SendAddToQueue(item.Media);
    }

    [RelayCommand]
    [DynamicWindowsRuntimeCast(typeof(StorageFolder))]
    private void Click(StorageItemViewModel? item)
    {
        if (item?.Media != null)
        {
            Play(item);
        }
        else if (item?.StorageItem is StorageFolder folder)
        {
            StorageFolder[] crumbs = BreadcrumbLocations.Append(folder).ToArray();
            Navigate(crumbs);
        }
    }

    private async Task FetchQueryItemAsync(StorageFileQueryResult query)
    {
        Items.Clear();
        _playableItems.Clear();

        uint fetchIndex = 0;
        while (_isActive)
        {
            _loadingTimer.Debounce(() => IsLoading = true, TimeSpan.FromMilliseconds(800));
            IReadOnlyList<StorageFile> items = await query.GetFilesAsync(fetchIndex, 30);
            if (items.Count == 0) break;
            fetchIndex += (uint)items.Count;
            foreach (StorageFile storageFile in items)
            {
                StorageItemViewModel item = _storageVmFactory.GetInstance(storageFile);
                Items.Add(item);
                if (item.Media != null && item.Media.Source is not IExpandableMediaSource) _playableItems.Add(item.Media);
            }
        }

        _loadingTimer.Stop();
        IsLoading = false;
        IsEmpty = Items.Count == 0;
        UpdateAreAllVideosSelected();
    }

    private async Task FetchFolderContentAsync(StorageFolder folder)
    {
        Items.Clear();
        _playableItems.Clear();

        StorageItemQueryResult itemQuery = _filesService.GetSupportedItems(folder);
        uint fetchIndex = 0;
        while (_isActive)
        {
            _loadingTimer.Debounce(() => IsLoading = true, TimeSpan.FromMilliseconds(800));
            IReadOnlyList<IStorageItem> items = await itemQuery.GetItemsAsync(fetchIndex, 30);
            if (items.Count == 0) break;
            fetchIndex += (uint)items.Count;
            foreach (IStorageItem storageItem in items)
            {
                StorageItemViewModel item = _storageVmFactory.GetInstance(storageItem);
                Items.Add(item);
                if (item.Media != null && item.Media.Source is not IExpandableMediaSource) _playableItems.Add(item.Media);
            }
        }

        _loadingTimer.Stop();
        IsLoading = false;
        IsEmpty = Items.Count == 0;
        UpdateAreAllVideosSelected();
    }

    private async Task FetchFolderContentAsync(StorageLibrary library)
    {
        if (library.Folders.Count <= 0)
        {
            IsEmpty = true;
            return;
        }

        if (library.Folders.Count == 1)
        {
            StorageFolder folder = library.Folders[0];
            BreadcrumbLocations = [folder];
            Breadcrumbs = [folder.DisplayName];
            await FetchFolderContentAsync(folder);
        }
        else
        {
            Items.Clear();
            foreach (StorageFolder folder in library.Folders)
            {
                StorageItemViewModel item = _storageVmFactory.GetInstance(folder);
                Items.Add(item);
                await item.UpdateCaptionAsync();
            }

            IsEmpty = Items.Count == 0;
            UpdateAreAllVideosSelected();
        }
    }

    private async void RefreshFolderContent()
    {
        await FetchContentAsync(NavData?.Parameter ?? _source);
    }
}
