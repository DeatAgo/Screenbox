using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Screenbox.Commands;
using Screenbox.Core.Services;
using Screenbox.Core.ViewModels;
using Screenbox.Dialogs;
using Screenbox.Strings;
using Windows.Storage;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace Screenbox.Pages;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class PlaylistsPage : Page
{
    internal PlaylistsPageViewModel ViewModel => (PlaylistsPageViewModel)DataContext;

    internal CommonViewModel Common { get; }

    private readonly SelectDeselectAllCommand _selectionCommand;
    private readonly IFilesService _filesService;

    public PlaylistsPage()
    {
        this.InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<PlaylistsPageViewModel>();
        Common = Ioc.Default.GetRequiredService<CommonViewModel>();
        _filesService = Ioc.Default.GetRequiredService<IFilesService>();

        _selectionCommand = new SelectDeselectAllCommand();
    }

    private void SelectDeselectAllKeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.Playlists.Count > 0)
        {
            ViewModel.Selection.IsSelectionModeActive = true;
            _selectionCommand.ToggleSelection(PlaylistsGridView);
            args.Handled = true;
        }
    }

    [RelayCommand]
    private async Task CreatePlaylistAsync()
    {
        string? playlistName = await CreatePlaylistDialog.GetPlaylistNameAsync();
        if (!string.IsNullOrWhiteSpace(playlistName))
        {
            await ViewModel.CreatePlaylistAsync(playlistName!);
        }
    }

    [RelayCommand]
    private async Task RenamePlaylistAsync(PlaylistViewModel playlist)
    {
        RenamePlaylistDialog dialog = new(playlist.Name);
        string? newName = await dialog.GetPlaylistNameAsync();
        if (!string.IsNullOrWhiteSpace(newName) && newName != playlist.Name)
        {
            await ViewModel.RenamePlaylistAsync(playlist, newName!);
        }
    }

    [RelayCommand]
    private async Task SetPlaylistThumbnailAsync(PlaylistViewModel? playlist)
    {
        if (playlist is null) return;
        StorageFile? file = await _filesService.PickFileAsync(".png", ".jpg", ".jpeg", ".webp", ".bmp");
        if (file is null) return;

        StorageFolder thumbnailsFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
            "PlaylistThumbnails",
            CreationCollisionOption.OpenIfExists);

        StorageFile? croppedFile = await CropPlaylistCoverDialog.CropAndSaveAsync(
            file,
            thumbnailsFolder,
            $"{playlist.Id}_{Guid.NewGuid():N}");

        if (croppedFile is null) return;
        await playlist.SetCustomThumbnailAsync(croppedFile);
    }

    [RelayCommand]
    private async Task ResetPlaylistThumbnailAsync(PlaylistViewModel? playlist)
    {
        if (playlist is null) return;
        await playlist.ClearCustomThumbnailAsync();
    }

    [RelayCommand]
    private async Task DeletePlaylistAsync(PlaylistViewModel playlist)
    {
        var deleteConfirmation = new DeletePlaylistDialog(playlist.Name);
        var result = await deleteConfirmation.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await ViewModel.DeletePlaylistAsync(playlist);
    }

    [RelayCommand]
    private async Task ExportPlaylistAsync(PlaylistViewModel playlist)
    {
        await ViewModel.ExportPlaylistAsync(playlist, ManifestResources.FileDisplayNameM3U8);
    }
}
