using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Animations.Expressions;
using Screenbox.Commands;
using Screenbox.Core;
using Screenbox.Core.Services;
using Screenbox.Core.ViewModels;
using Screenbox.Dialogs;
using Screenbox.Extensions;
using Screenbox.Strings;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using EF = CommunityToolkit.WinUI.Animations.Expressions.ExpressionFunctions;
using NavigationViewDisplayMode = Windows.UI.Xaml.Controls.NavigationViewDisplayMode;

namespace Screenbox.Pages;

/// <summary>
/// A page that displays playlist details with media items, allowing users to play, shuffle, add, and remove media.
/// </summary>
public sealed partial class PlaylistDetailsPage : Page
{
    internal PlaylistDetailsPageViewModel ViewModel => (PlaylistDetailsPageViewModel)DataContext;

    internal CommonViewModel Common { get; }

    private int ClampSize => Common.NavigationViewDisplayMode == NavigationViewDisplayMode.Minimal ? 64 : 96;

    private float BackgroundScaleFactor => Common.NavigationViewDisplayMode == NavigationViewDisplayMode.Minimal ? 0.75f : 0.625f;

    private float CoverScaleFactor => Common.NavigationViewDisplayMode == NavigationViewDisplayMode.Minimal ? 0.6f : 0.5f;

    private int ButtonPanelOffset => 40;

    private float BackgroundVisualHeight => (float)(Header.ActualHeight * 2.5);

    private CompositionPropertySet? _props;
    private CompositionPropertySet? _scrollerPropertySet;
    private Compositor? _compositor;
    private ScrollViewer? _scrollViewer;
    private readonly IFilesService _filesService;
    private readonly SelectDeselectAllCommand _selectionCommand;

    public PlaylistDetailsPage()
    {
        this.InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<PlaylistDetailsPageViewModel>();
        Common = Ioc.Default.GetRequiredService<CommonViewModel>();
        _filesService = Ioc.Default.GetRequiredService<IFilesService>();
        _selectionCommand = new SelectDeselectAllCommand();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.SubtextChanged += ViewModel_SubtextChanged;
        ViewModel.OnNavigatedTo(e.Parameter);
        UpdateCaptionText();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.SubtextChanged -= ViewModel_SubtextChanged;
    }

    private void ViewModel_SubtextChanged(object? sender, EventArgs e)
    {
        UpdateCaptionText();
    }

    private void UpdateCaptionText()
    {
        CaptionText.Text = ViewModel.Source is null
            ? string.Empty
            : GetSubtext(ViewModel.Source.Items);
    }

    private void PlaylistDetailsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        ScrollViewer scrollViewer = _scrollViewer = ItemList.FindDescendant<ScrollViewer>() ??
                                                    throw new Exception("Cannot find ScrollViewer in ListView");

        _scrollerPropertySet = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scrollViewer);
        _compositor = _scrollerPropertySet.Compositor;

        _props = _compositor.CreatePropertySet();
        _props.InsertScalar("progress", 0);
        _props.InsertScalar("clampSize", ClampSize);
        _props.InsertScalar("backgroundScaleFactor", BackgroundScaleFactor);
        _props.InsertScalar("coverScaleFactor", CoverScaleFactor);
        _props.InsertScalar("buttonPanelOffset", ButtonPanelOffset);
        _props.InsertScalar("headerPadding", 12);

        ManipulationPropertySetReferenceNode scrollingProperties = _scrollerPropertySet.GetSpecializedReference<ManipulationPropertySetReferenceNode>();

        CreateHeaderAnimation(_props, scrollingProperties.Translation.Y);
    }

    private void SelectDeselectAllKeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.HasItems)
        {
            ViewModel.Selection.IsSelectionModeActive = true;
            _selectionCommand.ToggleSelection(ItemList);
            args.Handled = true;
        }
    }

    private void ItemList_OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;

        if (e.DragUIOverride != null)
        {
            e.DragUIOverride.Caption = Strings.Resources.AddToPlaylist;
        }
    }

    private async void ItemList_OnDrop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            IReadOnlyList<IStorageItem>? items = await e.DataView.GetStorageItemsAsync();
            if (items?.Count > 0)
            {
                int insertIndex = ItemList.GetDropIndex(e);
                await ViewModel.AddDroppedItemsAsync(items, insertIndex);
            }
        }
    }

    private void CreateHeaderAnimation(CompositionPropertySet propSet, ScalarNode scrollVerticalOffset)
    {
        PropertySetReferenceNode props = propSet.GetReference();
        ScalarNode progressNode = props.GetScalarProperty("progress");
        ScalarNode clampSizeNode = props.GetScalarProperty("clampSize");
        ScalarNode backgroundScaleFactorNode = props.GetScalarProperty("backgroundScaleFactor");
        ScalarNode coverScaleFactorNode = props.GetScalarProperty("coverScaleFactor");
        ScalarNode buttonPanelOffsetNode = props.GetScalarProperty("buttonPanelOffset");
        ScalarNode headerPaddingNode = props.GetScalarProperty("headerPadding");

        ExpressionNode progressAnimation = EF.Clamp(-scrollVerticalOffset / clampSizeNode, 0, 1);
        propSet.StartAnimation("progress", progressAnimation);

        Visual backgroundVisual = ElementCompositionPreview.GetElementVisual(BackgroundAcrylic);

        ExpressionNode backgroundScaleAnimation = EF.Lerp(1, backgroundScaleFactorNode, progressNode);
        ExpressionNode backgroundOpacityAnimation = progressNode;
        backgroundVisual.StartAnimation("Scale.Y", backgroundScaleAnimation);
        backgroundVisual.StartAnimation("Opacity", backgroundOpacityAnimation);

        Visual contentVisual = ElementCompositionPreview.GetElementVisual(ContentContainer);
        ElementCompositionPreview.SetIsTranslationEnabled(ContentContainer, true);

        ExpressionNode contentTranslationAnimation = progressNode * headerPaddingNode;
        contentVisual.StartAnimation("Translation.Y", contentTranslationAnimation);

        Visual coverArtVisual = ElementCompositionPreview.GetElementVisual(CoverArt);
        ElementCompositionPreview.SetIsTranslationEnabled(CoverArt, true);

        ExpressionNode coverArtScaleAnimation = EF.Lerp(1, coverScaleFactorNode, progressNode);
        ExpressionNode coverArtTranslationAnimation = progressNode * headerPaddingNode;
        coverArtVisual.StartAnimation("Scale.X", coverArtScaleAnimation);
        coverArtVisual.StartAnimation("Scale.Y", coverArtScaleAnimation);
        coverArtVisual.StartAnimation("Translation.X", coverArtTranslationAnimation);

        Visual textVisual = ElementCompositionPreview.GetElementVisual(TextPanel);
        ElementCompositionPreview.SetIsTranslationEnabled(TextPanel, true);

        ExpressionNode textTranslationAnimation = progressNode * (-clampSizeNode + headerPaddingNode);
        textVisual.StartAnimation("Translation.X", textTranslationAnimation);

        Visual captionVisual = ElementCompositionPreview.GetElementVisual(CaptionText);

        ScalarNode fadeThreshold = ExpressionValues.Constant.CreateConstantScalar("fadeThreshold", 0.6f);
        ExpressionNode textFadeAnimation = 1 - EF.Conditional(progressNode < fadeThreshold, progressNode / fadeThreshold, 1);

        textFadeAnimation.SetScalarParameter("fadeThreshold", 0.2f);
        captionVisual.StartAnimation("Opacity", textFadeAnimation);

        Visual buttonVisual = ElementCompositionPreview.GetElementVisual(ButtonPanel);
        ElementCompositionPreview.SetIsTranslationEnabled(ButtonPanel, true);

        ExpressionNode buttonTranslationAnimation = progressNode * (-buttonPanelOffsetNode);
        buttonVisual.StartAnimation("Translation.Y", buttonTranslationAnimation);
    }

    private void CoverArt_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _props?.InsertScalar("clampSize", ClampSize);
        _props?.InsertScalar("backgroundScaleFactor", BackgroundScaleFactor);
        _props?.InsertScalar("coverScaleFactor", CoverScaleFactor);
        _props?.InsertScalar("buttonPanelOffset", ButtonPanelOffset);
    }

    private void BackgroundHost_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
    }

    private Thickness GetScrollbarVerticalMargin(Thickness value)
    {
        double headerHeight = CoverArt.Height + Header.Margin.Bottom;
        return new Thickness(value.Left, value.Top - headerHeight, value.Right, value.Bottom);
    }

    public static string GetSubtext(IReadOnlyCollection<MediaViewModel>? items)
    {
        if (items == null) return string.Empty;

        int itemsCount = items.Count;
        TimeSpan duration = GetTotalDuration(items);

        string itemsCountText = Strings.Resources.ItemsCount(itemsCount);
        string runTime = Strings.Resources.RunTime(Humanizer.ToDuration(duration));
        return $"{itemsCountText} • {runTime}";
    }

    public static TimeSpan GetTotalDuration(IReadOnlyCollection<MediaViewModel>? items)
    {
        if (items == null) return TimeSpan.Zero;

        TimeSpan duration = TimeSpan.Zero;
        foreach (MediaViewModel item in items)
        {
            duration += item.Duration;
        }

        return duration;
    }

    [RelayCommand]
    private async Task RenamePlaylistAsync()
    {
        if (ViewModel.Source == null) return;
        RenamePlaylistDialog dialog = new(ViewModel.Source.Name);
        string? newName = await dialog.GetPlaylistNameAsync();
        if (!string.IsNullOrWhiteSpace(newName) && newName != ViewModel.Source.Name)
        {
            await ViewModel.RenamePlaylistAsync(newName!);
        }
    }

    [RelayCommand]
    private async Task SetPlaylistThumbnailAsync()
    {
        if (ViewModel.Source == null) return;
        StorageFile? file = await _filesService.PickFileAsync(".png", ".jpg", ".jpeg", ".webp", ".bmp");
        if (file is null) return;

        StorageFolder thumbnailsFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
            "PlaylistThumbnails",
            CreationCollisionOption.OpenIfExists);

        StorageFile? croppedFile = await CropPlaylistCoverDialog.CropAndSaveAsync(
            file,
            thumbnailsFolder,
            $"{ViewModel.Source.Id}_{Guid.NewGuid():N}");

        if (croppedFile is null) return;
        await ViewModel.Source.SetCustomThumbnailAsync(croppedFile);
    }

    [RelayCommand]
    private async Task ResetPlaylistThumbnailAsync()
    {
        if (ViewModel.Source == null) return;
        await ViewModel.Source.ClearCustomThumbnailAsync();
    }

    [RelayCommand]
    private async Task DeletePlaylistAsync()
    {
        if (ViewModel.Source == null) return;
        var deleteConfirmation = new DeletePlaylistDialog(ViewModel.Source.Name);
        var result = await deleteConfirmation.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.Source.DeleteStoredThumbnailFile();
            bool deleted = await ViewModel.DeletePlaylistAsync();
            if (deleted && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }

    [RelayCommand]
    private async Task ExportPlaylistAsync()
    {
        await ViewModel.ExportPlaylistAsync(ManifestResources.FileDisplayNameM3U8);
    }
}
