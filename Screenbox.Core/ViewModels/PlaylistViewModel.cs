using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Enums;
using Screenbox.Core.Factories;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Xaml.Media.Imaging;

namespace Screenbox.Core.ViewModels;

public sealed partial class PlaylistViewModel : ObservableRecipient,
    IRecipient<QueuePlaybackStateChangedMessage>
{
    private const string CustomThumbnailsFolderName = "PlaylistThumbnails";

    public ObservableCollection<MediaViewModel> Items { get; } = new();

    public double ItemsCount => Items.Count;    // For binding

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial bool IsQueueActive { get; set; }
    [ObservableProperty] public partial BitmapImage? Thumbnail { get; set; }
    [ObservableProperty] public partial bool HasCustomThumbnail { get; set; }
    [ObservableProperty] public partial DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.Now;

    public string Id => _id.ToString();

    public string? CustomThumbnailPath => _customThumbnailPath;

    private bool HasItems => Items.Count > 0;

    private Guid _id = Guid.NewGuid();
    private MediaViewModel? _coverItem;
    private string? _customThumbnailPath;
    private BitmapImage? _customThumbnailImage;

    private readonly IPlaylistService _playlistService;
    private readonly MediaViewModelFactory _mediaFactory;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _playlistSaveTimer;
    private readonly ILogger<PlaylistViewModel> _logger;

    public PlaylistViewModel(
        IPlaylistService playlistService,
        MediaViewModelFactory mediaFactory,
        ILogger<PlaylistViewModel> logger)
        : this(playlistService, mediaFactory, logger, WeakReferenceMessenger.Default)
    {
    }

    public PlaylistViewModel(
        IPlaylistService playlistService,
        MediaViewModelFactory mediaFactory,
        ILogger<PlaylistViewModel> logger,
        IMessenger messenger)
        : base(messenger)
    {
        _playlistService = playlistService;
        _mediaFactory = mediaFactory;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _playlistSaveTimer = _dispatcherQueue.CreateTimer();

        Items.CollectionChanged += Items_CollectionChanged;

        Messenger.Register<QueuePlaybackStateChangedMessage>(this);

        var request = new QueuePlaybackStateRequestMessage();
        Messenger.Send(request);
        if (request.HasReceivedResponse)
        {
            UpdatePlaybackState(request.Response);
        }
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ItemsCount));
        UpdateCoverItem();

        _playlistSaveTimer.Debounce(async () =>
        {
            // Ensures the playlist is up to date when items are reordered.
            try
            {
                await SaveAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist playlist '{PlaylistId}'.", Id);
            }

            PlayCommand.NotifyCanExecuteChanged();
            PlayNextCommand.NotifyCanExecuteChanged();
            AddToQueueCommand.NotifyCanExecuteChanged();
        }, TimeSpan.FromMilliseconds(50));
    }

    private void UpdateCoverItem()
    {
        MediaViewModel? firstItem = Items.FirstOrDefault();
        if (!ReferenceEquals(_coverItem, firstItem))
        {
            if (_coverItem is not null)
            {
                _coverItem.PropertyChanged -= CoverItem_PropertyChanged;
            }

            _coverItem = firstItem;

            if (_coverItem is not null)
            {
                _coverItem.PropertyChanged += CoverItem_PropertyChanged;
            }
        }

        if (_customThumbnailImage is not null)
        {
            Thumbnail = _customThumbnailImage;
            return;
        }

        if (_coverItem is null)
        {
            Thumbnail = null;
            return;
        }

        Thumbnail = _coverItem.Thumbnail;
        if (Thumbnail is null)
        {
            _ = LoadCoverThumbnailAsync(_coverItem);
        }
    }

    private async Task LoadCoverThumbnailAsync(MediaViewModel item)
    {
        try
        {
            await item.LoadThumbnailAsync();
            if (_customThumbnailImage is null && ReferenceEquals(_coverItem, item))
            {
                Thumbnail = item.Thumbnail;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load playlist cover thumbnail.");
        }
    }

    private void CoverItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaViewModel.Thumbnail) &&
            _customThumbnailImage is null &&
            sender is MediaViewModel media)
        {
            Thumbnail = media.Thumbnail;
        }
    }

    public async Task SetCustomThumbnailAsync(StorageFile sourceFile)
    {
        ArgumentNullException.ThrowIfNull(sourceFile);

        StorageFolder thumbnailsFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
            CustomThumbnailsFolderName,
            CreationCollisionOption.OpenIfExists);

        StorageFile storedFile;
        if (sourceFile.Path.StartsWith(thumbnailsFolder.Path, StringComparison.OrdinalIgnoreCase))
        {
            storedFile = sourceFile;
        }
        else
        {
            string extension = string.IsNullOrWhiteSpace(sourceFile.FileType) ? ".png" : sourceFile.FileType;
            string destinationName = $"{Id}_{Guid.NewGuid():N}{extension}";
            storedFile = await sourceFile.CopyAsync(
                thumbnailsFolder,
                destinationName,
                NameCollisionOption.ReplaceExisting);
        }

        BitmapImage image = await DecodeBitmapImageAsync(storedFile);

        string? oldPath = _customThumbnailPath;
        _customThumbnailPath = storedFile.Path;
        _customThumbnailImage = image;
        HasCustomThumbnail = true;
        Thumbnail = image;

        await SaveAsync();

        if (!string.IsNullOrEmpty(oldPath) && !string.Equals(oldPath, _customThumbnailPath, StringComparison.OrdinalIgnoreCase))
        {
            DeleteFileQuietly(oldPath);
        }
    }

    public async Task ClearCustomThumbnailAsync()
    {
        string? oldPath = _customThumbnailPath;
        _customThumbnailPath = null;
        _customThumbnailImage = null;
        HasCustomThumbnail = false;

        UpdateCoverItem();
        await SaveAsync();

        if (!string.IsNullOrEmpty(oldPath))
        {
            DeleteFileQuietly(oldPath);
        }
    }

    public void DeleteStoredThumbnailFile()
    {
        string? oldPath = _customThumbnailPath;
        _customThumbnailPath = null;
        _customThumbnailImage = null;
        HasCustomThumbnail = false;

        if (!string.IsNullOrEmpty(oldPath))
        {
            DeleteFileQuietly(oldPath);
        }
    }

    private async Task LoadCustomThumbnailFromPathAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _customThumbnailPath = null;
                _customThumbnailImage = null;
                HasCustomThumbnail = false;
                UpdateCoverItem();
                return;
            }

            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            BitmapImage image = await DecodeBitmapImageAsync(file);

            if (string.Equals(_customThumbnailPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _customThumbnailImage = image;
                HasCustomThumbnail = true;
                Thumbnail = image;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load custom playlist thumbnail from '{Path}'.", path);
            if (string.Equals(_customThumbnailPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _customThumbnailPath = null;
                _customThumbnailImage = null;
                HasCustomThumbnail = false;
                UpdateCoverItem();
            }
        }
    }

    private static async Task<BitmapImage> DecodeBitmapImageAsync(StorageFile file)
    {
        using IRandomAccessStream stream = await file.OpenReadAsync();
        BitmapImage image = new()
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelHeight = 400
        };
        await image.SetSourceAsync(stream);
        return image;
    }

    private void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete custom playlist thumbnail '{Path}'.", path);
        }
    }

    public QueueSession ToQueueSession()
    {
        return new QueueSession(0, Items)
        {
            Origin = QueueOrigin.ForPlaylist(this)
        };
    }

    public void Load(PlaylistRecordDto persistentPlaylist)
    {
        if (!Guid.TryParse(persistentPlaylist.Id, out _id)) return;
        Name = persistentPlaylist.DisplayName;
        LastUpdated = persistentPlaylist.LastUpdated;
        _customThumbnailPath = persistentPlaylist.CustomThumbnailPath;
        _customThumbnailImage = null;
        HasCustomThumbnail = !string.IsNullOrEmpty(_customThumbnailPath);

        Items.Clear();

        var request = new QueuePlaybackStateRequestMessage();
        Messenger.Send(request);
        if (request.HasReceivedResponse)
        {
            UpdatePlaybackState(request.Response);
        }
        var itemSet = new HashSet<MediaViewModel>(persistentPlaylist.Items.Count);

        var albumFactory = new AlbumViewModelFactory();
        var artistFactory = new ArtistViewModelFactory();

        foreach (var item in persistentPlaylist.Items)
        {
            try
            {
                var vm = ToMediaViewModel(item, albumFactory, artistFactory);
                Items.Add(itemSet.Add(vm) ? vm : new MediaViewModel(vm));
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(_customThumbnailPath))
        {
            _ = LoadCustomThumbnailFromPathAsync(_customThumbnailPath);
        }
        else
        {
            UpdateCoverItem();
        }
    }

    public async Task SaveAsync()
    {
        LastUpdated = DateTimeOffset.Now;
        var persistentPlaylist = ToPersistentPlaylist();
        await _playlistService.SavePlaylistAsync(persistentPlaylist);
    }

    public async Task SaveWithTimestampAsync(DateTimeOffset timestamp)
    {
        LastUpdated = timestamp;
        var persistentPlaylist = ToPersistentPlaylist();
        await _playlistService.SavePlaylistAsync(persistentPlaylist);
    }

    public async Task RenameAsync(string newDisplayName)
    {
        Name = newDisplayName;
        await SaveAsync();
    }

    public void Receive(QueuePlaybackStateChangedMessage message)
    {
        UpdatePlaybackState(message.State);
    }

    private void UpdatePlaybackState(QueuePlaybackState state)
    {
        bool isActive = state.SessionId != Guid.Empty &&
                        state.CurrentItem is not null &&
                        (state.Origin?.Matches(RecentItemKind.Playlist, Id) ?? false);
        IsQueueActive = isActive;
        IsPlaying = isActive && state.IsPlaying;
    }

    [RelayCommand(CanExecute = nameof(HasItems))]
    private void Play()
    {
        if (IsQueueActive)
        {
            Messenger.Send(new TogglePlayPauseMessage(showBadge: false));
            return;
        }

        Messenger.Send(new SetQueueMessage(ToQueueSession(), true));
    }

    [RelayCommand(CanExecute = nameof(HasItems))]
    private void PlayNext()
    {
        Messenger.SendPlayNext(Items);
    }

    [RelayCommand(CanExecute = nameof(HasItems))]
    private void AddToQueue()
    {
        Messenger.SendAddToQueue(Items);
    }

    [RelayCommand]
    public async Task AddItemsAsync(IReadOnlyList<MediaViewModel> items)
    {
        if (items.Count == 0) return;
        foreach (var item in items)
        {
            Items.Add(Items.Contains(item) ? new MediaViewModel(item) : item);
        }

        _playlistSaveTimer.Stop();
        await SaveAsync();
        Messenger.Send(new NotificationMessage(NotificationLevel.Success, NotificationKind.PlaylistItemsAdded, title: Name, numericValue: items.Count));
    }

    public async Task AddItemsAtIndexAsync(IReadOnlyList<MediaViewModel> items, int insertIndex = -1)
    {
        if (items.Count == 0) return;

        foreach (var item in items)
        {
            if (insertIndex < 0 || insertIndex >= Items.Count)
            {
                Items.Add(Items.Contains(item) ? new MediaViewModel(item) : item);
            }
            else
            {
                Items.Insert(insertIndex, Items.Contains(item) ? new MediaViewModel(item) : item);
                insertIndex++;
            }
        }

        _playlistSaveTimer.Stop();
        await SaveAsync();
        Messenger.Send(new NotificationMessage(NotificationLevel.Success, NotificationKind.PlaylistItemsAdded, title: Name, numericValue: items.Count));
    }

    private PlaylistRecordDto ToPersistentPlaylist()
    {
        return new PlaylistRecordDto
        {
            Id = _id.ToString(),
            DisplayName = Name,
            CustomThumbnailPath = _customThumbnailPath,
            LastUpdated = LastUpdated,
            Items = Items.Select(RawMediaRecordDto.FromMediaViewModel).ToList()
        };
    }

    private MediaViewModel ToMediaViewModel(RawMediaRecordDto record, AlbumViewModelFactory albumFactory, ArtistViewModelFactory artistFactory)
    {
        MediaViewModel media;
        bool existing = false;
        if (Uri.TryCreate(record.Path, UriKind.Absolute, out var uri))
        {
            if (_mediaFactory.TryGetOrCreate(uri, out var existingMedia))
            {
                media = existingMedia!;
                existing = true;
            }
            else
            {
                media = _mediaFactory.GetOrCreate(uri);
            }
        }
        else
        {
            media = _mediaFactory.Create(new Uri("about:blank"));
            media.IsAvailable = false;
        }

        if (!existing)
        {
            if (!string.IsNullOrEmpty(record.Title))
                media.Name = record.Title;

            media.MediaInfo = record.ToMediaInfo();

            if (media.MediaType == MediaPlaybackType.Music)
            {
                // Placeholder album and artist for media list view.
                // This path can happen when playlist is loaded before the music library is loaded
                albumFactory.AddSong(media);
                artistFactory.AddSong(media);
                media.Album = albumFactory.SongsToAlbums[media];
                media.Artists = artistFactory.SongsToArtists[media].ToArray();
                media.Album.UpdateProperties();
            }

            if (record.DateAdded != default)
            {
                media.DateAdded = record.DateAdded.ToLocalTime();
            }
        }

        return media;
    }
}
