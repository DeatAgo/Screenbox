using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Xaml.Interactivity;
using Screenbox.Core.Contexts;
using Screenbox.Core.ViewModels;
using Screenbox.Dialogs;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Screenbox.Behaviors;

/// <summary>
/// Represents a behavior that populates a <see cref="MenuFlyout"/>, or a target <see cref="MenuFlyoutSubItem"/>,
/// with playlist actions, allowing users to add media items to existing playlists or create new playlists.
/// </summary>
internal sealed partial class AddToPlaylistFlyoutBehavior : Behavior<MenuFlyout>
{
    public static readonly DependencyProperty DataContextProperty = DependencyProperty.Register(
        nameof(DataContext),
        typeof(object),
        typeof(AddToPlaylistFlyoutBehavior),
        new PropertyMetadata(null));

    public object? DataContext
    {
        get => GetValue(DataContextProperty);
        set => SetValue(DataContextProperty, value);
    }

    public static readonly DependencyProperty TargetSubItemProperty = DependencyProperty.Register(
        nameof(TargetSubItem),
        typeof(MenuFlyoutSubItem),
        typeof(AddToPlaylistFlyoutBehavior),
        new PropertyMetadata(null));

    public MenuFlyoutSubItem? TargetSubItem
    {
        [DynamicWindowsRuntimeCast(typeof(MenuFlyoutSubItem))]
        get => (MenuFlyoutSubItem?)GetValue(TargetSubItemProperty);
        set => SetValue(TargetSubItemProperty, value);
    }

    private IAsyncRelayCommand<IEnumerable<MediaViewModel>> CreatePlaylistCommand { get; }

    private readonly PlaylistsContext _playlistsContext;
    private FrameworkElement? _flyoutTarget;

    public AddToPlaylistFlyoutBehavior()
    {
        _playlistsContext = Ioc.Default.GetRequiredService<PlaylistsContext>();
        CreatePlaylistCommand = new AsyncRelayCommand<IEnumerable<MediaViewModel>>(CreatePlaylistAsync);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.Opening += AssociatedObjectOnOpening;
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        AssociatedObject.Opening -= AssociatedObjectOnOpening;
    }

    private void AssociatedObjectOnOpening(object? sender, object e)
    {
        _flyoutTarget = AssociatedObject.Target;
        PopulateMenu();
    }

    private void PopulateMenu()
    {
        object? dataContext = DataContext;
        IList<MenuFlyoutItemBase> menuItems = TargetSubItem?.Items ?? AssociatedObject.Items;
        dataContext ??= _flyoutTarget?.DataContext;

        MediaViewModel[] contextItems = dataContext switch
        {
            StorageItemViewModel { Media: { } media } => [media],
            MediaViewModel vm => [vm],
            AlbumViewModel album => album.OrderedSongs.ToArray(),
            ArtistViewModel artist => artist.OrderedSongs.ToArray(),
            SelectionViewModel selection => ExtractMediaItems(selection.GetSelectedItems()),
            IReadOnlyList<MediaViewModel> list => list.ToArray(),
            IEnumerable<MediaViewModel> collection => collection.ToArray(),
            IEnumerable<object> objects => ExtractMediaItems(objects),
            _ => [],
        };

        menuItems.Clear();
        menuItems.Add(new MenuFlyoutItem
        {
            Icon = new SymbolIcon(Symbol.Add),
            Text = Strings.Resources.CreateNewPlaylist,
            Command = CreatePlaylistCommand,
            CommandParameter = contextItems
        });

        menuItems.Add(new MenuFlyoutSeparator());

        if (_playlistsContext.Playlists.Count == 0)
        {
            menuItems.Add(new MenuFlyoutItem
            {
                Text = Strings.Resources.NoPlaylists,
                IsEnabled = false
            });
            return;
        }

        foreach (var playlist in _playlistsContext.Playlists.Where(p => p is not null))
        {
            menuItems.Add(new MenuFlyoutItem
            {
                Text = playlist.Name,
                Command = playlist.AddItemsCommand,
                CommandParameter = contextItems
            });
        }
    }

    private static MediaViewModel[] ExtractMediaItems(IEnumerable<object> items)
    {
        var result = new List<MediaViewModel>();
        foreach (object item in items)
        {
            switch (item)
            {
                case MediaViewModel media:
                    result.Add(media);
                    break;
                case StorageItemViewModel { Media: { } storageMedia }:
                    result.Add(storageMedia);
                    break;
            }
        }

        return result.ToArray();
    }

    private async Task CreatePlaylistAsync(IEnumerable<MediaViewModel>? itemsToAdd)
    {
        var playlistName = await CreatePlaylistDialog.GetPlaylistNameAsync();
        if (string.IsNullOrWhiteSpace(playlistName))
            return;

        var playlist = Ioc.Default.GetRequiredService<PlaylistViewModel>();
        playlist.Name = playlistName!;
        if (itemsToAdd != null)
        {
            foreach (var item in itemsToAdd)
            {
                playlist.Items.Add(item);
            }
        }

        await playlist.SaveAsync();
        _playlistsContext.Playlists.Insert(0, playlist);
    }
}
