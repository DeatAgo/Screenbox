using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Screenbox.Controls;
using Screenbox.Converters;
using Screenbox.Core.Enums;
using Screenbox.Core.Messages;
using Screenbox.Core.ViewModels;
using Screenbox.Dialogs;
using Screenbox.Helpers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Screenbox.Pages;

public sealed partial class PlayerPage : Page, IRecipient<PropertiesDockMessage>
{
    internal PlayerPageViewModel ViewModel => (PlayerPageViewModel)DataContext;

    private static readonly bool _isArabicLanguage = CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "ar";

    private readonly DispatcherQueueTimer _delayFlyoutOpenTimer;
    private CancellationTokenSource? _animationCancellationTokenSource;
    private CancellationTokenSource? _emptyArtDelayCts;
    private MediaViewModel? _trackedMedia;
    private MediaViewModel? _dockedPropertiesMedia;
    private bool _isDockedSidebarOpen;
    private bool _startup;
    private bool _isRefreshingDockedProperties;
    private bool _isPointerOverPropertiesSidebar;

    public PlayerPage()
    {
        this.InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<PlayerPageViewModel>();
        _delayFlyoutOpenTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();

        RegisterSeekBarPointerHandlers();
        UpdatePreviewType();

        ViewModel.PropertyChanged += ViewModelOnPropertyChanged;
        UpdateTrackedMedia();
        LayoutRoot.ActualThemeChanged += OnActualThemeChanged;
        WeakReferenceMessenger.Default.Register(this);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        UpdateBackgroundAcrylicOpacity(ActualTheme);

        if (e.Parameter is true)
        {
            LayoutRoot.Transitions.Clear();
            ViewModel.PlayerVisibility = PlayerVisibilityState.Visible;
            ViewModel.OnFileLaunched();
            _startup = true;
        }
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (ViewModel.PlayerVisibility != PlayerVisibilityState.Visible)
        {
            base.OnKeyDown(e);
            return;
        }

        bool shouldHideControls = ViewModel is { ControlsHidden: false, ViewMode: WindowViewMode.Default };

        switch (e.Key)
        {
            case VirtualKey.GamepadY when ViewModel.ViewMode != WindowViewMode.Compact:
                ViewModel.ControlsHidden = false;
                PlayQueueFlyout.ShowAt(PlayQueueButton,
                    new FlyoutShowOptions { Placement = GlobalizationHelper.MirrorWhenRightToLeft(FlyoutPlacementMode.BottomEdgeAlignedLeft) });
                break;
            case VirtualKey.GamepadMenu:
                VideoView.ContextFlyout.ShowAt(PlayerControls,
                    new FlyoutShowOptions { Placement = GlobalizationHelper.MirrorWhenRightToLeft(FlyoutPlacementMode.TopEdgeAlignedRight) });
                break;
            case VirtualKey.GamepadB when shouldHideControls:
                ViewModel.TryHideControls(true);
                break;
            default:
                base.OnKeyDown(e);
                return;
        }
    }

    public void Receive(PropertiesDockMessage message)
    {
        if (!message.IsOpen || message.Mode == PropertiesDockMode.Floating)
        {
            _isDockedSidebarOpen = false;
            LeftPropertiesSidebar.Visibility = Visibility.Collapsed;
            RightPropertiesSidebar.Visibility = Visibility.Collapsed;
            return;
        }

        if (ViewModel.Media == null)
        {
            if (message.Media != null)
            {
                var dialog = new PropertiesDialog { Media = message.Media };
                _ = dialog.ShowFloatingAsync();
            }
            return;
        }

        _dockedPropertiesMedia = message.Media ?? ViewModel.Media;
        _isDockedSidebarOpen = true;

        if (ViewModel.ViewMode == WindowViewMode.Compact)
        {
            LeftPropertiesSidebar.Visibility = Visibility.Collapsed;
            RightPropertiesSidebar.Visibility = Visibility.Collapsed;
            return;
        }

        if (ViewModel.PlayerVisibility != PlayerVisibilityState.Visible)
        {
            ViewModel.PlayerVisibility = PlayerVisibilityState.Visible;
        }

        ApplyDockedSidebarState(message.Mode);
    }

    private void RestoreDockedSidebarIfNeeded()
    {
        if (_isDockedSidebarOpen &&
            ViewModel.Media != null &&
            ViewModel.PlayerVisibility == PlayerVisibilityState.Visible &&
            ViewModel.ViewMode != WindowViewMode.Compact &&
            PropertiesDialog.PreferredDockMode is PropertiesDockMode.Left or PropertiesDockMode.Right)
        {
            ApplyDockedSidebarState(PropertiesDialog.PreferredDockMode);
        }
    }

    private void ApplyDockedSidebarState(PropertiesDockMode mode)
    {
        if (mode == PropertiesDockMode.Left)
        {
            RightPropertiesSidebar.Visibility = Visibility.Collapsed;
            RightPropertiesView.Media = null;
            LeftPropertiesView.Media = null;
            LeftPropertiesView.Media = _dockedPropertiesMedia;
            LeftPropertiesSidebar.Visibility = Visibility.Visible;
        }
        else if (mode == PropertiesDockMode.Right)
        {
            LeftPropertiesSidebar.Visibility = Visibility.Collapsed;
            LeftPropertiesView.Media = null;
            RightPropertiesView.Media = null;
            RightPropertiesView.Media = _dockedPropertiesMedia;
            RightPropertiesSidebar.Visibility = Visibility.Visible;
        }
    }

    private async void RefreshDockedPropertiesMedia(MediaViewModel? media)
    {
        if (media == null || !_isDockedSidebarOpen || _isRefreshingDockedProperties) return;

        _isRefreshingDockedProperties = true;
        try
        {
            // Let libVLC / MediaViewModel settle briefly before querying MediaInfo
            await Task.Delay(50);
            if (!_isDockedSidebarOpen || !ReferenceEquals(ViewModel.Media, media)) return;

            _dockedPropertiesMedia = media;

            if (LeftPropertiesSidebar.Visibility == Visibility.Visible)
            {
                LeftPropertiesView.Media = null;
                LeftPropertiesView.Media = media;
            }

            if (RightPropertiesSidebar.Visibility == Visibility.Visible)
            {
                RightPropertiesView.Media = null;
                RightPropertiesView.Media = media;
            }
        }
        finally
        {
            _isRefreshingDockedProperties = false;
        }
    }

    private void DockPropertiesLeft_Click(object sender, RoutedEventArgs e)
    {
        PropertiesDialog.PreferredDockMode = PropertiesDockMode.Left;
        Receive(new PropertiesDockMessage(PropertiesDockMode.Left, _dockedPropertiesMedia, IsOpen: true));
    }

    private void DockPropertiesRight_Click(object sender, RoutedEventArgs e)
    {
        PropertiesDialog.PreferredDockMode = PropertiesDockMode.Right;
        Receive(new PropertiesDockMessage(PropertiesDockMode.Right, _dockedPropertiesMedia, IsOpen: true));
    }

    private void FloatProperties_Click(object sender, RoutedEventArgs e)
    {
        MediaViewModel? media = _dockedPropertiesMedia;
        _isDockedSidebarOpen = false;
        PropertiesDialog.PreferredDockMode = PropertiesDockMode.Floating;
        LeftPropertiesSidebar.Visibility = Visibility.Collapsed;
        RightPropertiesSidebar.Visibility = Visibility.Collapsed;
        LeftPropertiesView.Media = null;
        RightPropertiesView.Media = null;

        if (media != null)
        {
            var dialog = new PropertiesDialog { Media = media };
            _ = dialog.ShowFloatingAsync();
        }
    }

    private void CloseDockedProperties_Click(object sender, RoutedEventArgs e)
    {
        _isDockedSidebarOpen = false;
        LeftPropertiesSidebar.Visibility = Visibility.Collapsed;
        RightPropertiesSidebar.Visibility = Visibility.Collapsed;
        LeftPropertiesView.Media = null;
        RightPropertiesView.Media = null;
    }

    private void LeftSidebarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newWidth = LeftPropertiesSidebar.Width + e.HorizontalChange;
        LeftPropertiesSidebar.Width = Math.Max(LeftPropertiesSidebar.MinWidth, Math.Min(LeftPropertiesSidebar.MaxWidth, newWidth));
    }

    private void RightSidebarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newWidth = RightPropertiesSidebar.Width - e.HorizontalChange;
        RightPropertiesSidebar.Width = Math.Max(RightPropertiesSidebar.MinWidth, Math.Min(RightPropertiesSidebar.MaxWidth, newWidth));
    }

    private void SidebarSplitter_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeWestEast, 0);

    private void SidebarSplitter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!e.Pointer.IsInContact)
        {
            Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);
        }
    }

    private void UpdateTrackedMedia()
    {
        if (ReferenceEquals(_trackedMedia, ViewModel.Media))
            return;

        if (_trackedMedia is not null)
        {
            _trackedMedia.PropertyChanged -= TrackedMediaOnPropertyChanged;
        }

        _trackedMedia = ViewModel.Media;

        if (_trackedMedia is not null)
        {
            _trackedMedia.PropertyChanged += TrackedMediaOnPropertyChanged;
        }

        OnMediaThumbnailUpdated();
    }

    private void TrackedMediaOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaViewModel.Thumbnail))
        {
            OnMediaThumbnailUpdated();
        }
    }

    private void PropertiesSidebar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPropertiesSidebar = true;
        Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);
        ViewModel.ControlsHidden = false;
        ViewModel.OnPointerMoved();
    }

    private void PropertiesSidebar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPropertiesSidebar = false;
        ViewModel.OnPointerMoved();
    }

    private void PropertiesSidebar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPropertiesSidebar = true;
        if (Window.Current.CoreWindow.PointerCursor == null)
        {
            Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);
        }

        ViewModel.OnPointerMoved();
    }

    private async void OnMediaThumbnailUpdated()
    {
        _emptyArtDelayCts?.Cancel();
        _emptyArtDelayCts = null;

        ImageSource? newThumbnail = _trackedMedia?.Thumbnail;

        if (newThumbnail == null && _trackedMedia != null)
        {
            using var cts = _emptyArtDelayCts = new CancellationTokenSource();
            try
            {
                await Task.Delay(1000, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cts != _emptyArtDelayCts) return;
            _emptyArtDelayCts = null;
        }

        AlbumArtImage.ImageSource = newThumbnail;
        AlbumArtPlaceholder.Visibility = newThumbnail == null ? Visibility.Visible : Visibility.Collapsed;
        PlayBackgroundArtChangeCrossFadeAnimation();
    }

    private void OnLoading(FrameworkElement sender, object args)
    {
        if (ViewModel.PlayerVisibility == PlayerVisibilityState.Hidden)
            VisualStateManager.GoToState(this, "Hidden", false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (LayoutRoot.Transitions.Count != 0) return;
        PaneThemeTransition transition = new()
        {
            Edge = EdgeTransitionLocation.Bottom
        };

        LayoutRoot.Transitions.Add(transition);

        if (ViewModel.PlayerVisibility == PlayerVisibilityState.Visible)
        {
            PlayerTitleBar.RefreshDragRegion();
            PlayerControls.FocusFirstButton();
        }
    }

    private void BackgroundElementOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double overshoot = 200;
        double edgeLength = Math.Max(e.NewSize.Width, e.NewSize.Height) + overshoot;
        BackgroundArt.Width = edgeLength;
        BackgroundArt.Height = edgeLength;
    }

    private void RegisterSeekBarPointerHandlers()
    {
        SeekBar? seekBar = PlayerControls.FindDescendant<SeekBar>();
        Guard.IsNotNull(seekBar, nameof(seekBar));
        seekBar.AddHandler(PointerPressedEvent, (PointerEventHandler)SeekBarPointerPressedOrEnteredEventHandler, true);
        seekBar.AddHandler(PointerReleasedEvent, (PointerEventHandler)SeekBarPointerReleasedEventHandler, true);
        seekBar.AddHandler(PointerCanceledEvent, (PointerEventHandler)SeekBarPointerReleasedEventHandler, true);
        seekBar.AddHandler(PointerEnteredEvent, (PointerEventHandler)SeekBarPointerPressedOrEnteredEventHandler, false);
        seekBar.AddHandler(PointerExitedEvent, (PointerEventHandler)SeekBarPointerExitedEventHandler, false);
    }

    private void SeekBarPointerPressedOrEnteredEventHandler(object s, PointerRoutedEventArgs e)
    {
        ViewModel.SeekBarPointerInteracting = true;
    }

    private void SeekBarPointerReleasedEventHandler(object s, PointerRoutedEventArgs e)
    {
        ViewModel.SeekBarPointerInteracting = false;
        if (ViewModel.PlayerVisibility == PlayerVisibilityState.Visible)
            PlayerControls.FocusFirstButton();
    }

    private void SeekBarPointerExitedEventHandler(object s, PointerRoutedEventArgs e)
    {
        ViewModel.SeekBarPointerInteracting = false;
    }

    private void OnLayoutVisualStateChanged(object _, VisualStateChangedEventArgs args)
    {
        bool expanding = args.OldState?.Name == nameof(MiniPlayer) || (args.OldState?.Name == nameof(Hidden) &&
            (args.NewState == null || args.NewState.Name == nameof(Normal)));

        bool collapsing = args.OldState?.Name == nameof(Normal) && args.NewState?.Name == nameof(MiniPlayer);

        if (expanding || collapsing) PlayerControls.FocusFirstButton();
        UpdateRootTheme();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerPageViewModel.Media):
                UpdateTrackedMedia();
                RefreshDockedPropertiesMedia(ViewModel.Media);
                break;
            case nameof(PlayerPageViewModel.IsOpening) when !ViewModel.IsOpening:
                RefreshDockedPropertiesMedia(ViewModel.Media);
                break;
            case nameof(PlayerPageViewModel.ControlsHidden):
                if (ViewModel.ControlsHidden && _isPointerOverPropertiesSidebar)
                {
                    ViewModel.ControlsHidden = false;
                    Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);
                    break;
                }

                VisualStateManager.GoToState(this, ViewModel.ControlsHidden ? "ControlsHidden" : "ControlsVisible", true);
                if (!ViewModel.ControlsHidden)
                {
                    PlayerControls.FocusFirstButton();
                }

                break;
            case nameof(PlayerPageViewModel.ViewMode):
                switch (ViewModel.ViewMode)
                {
                    case WindowViewMode.Default:
                        VisualStateManager.GoToState(this, "Normal", true);
                        RestoreDockedSidebarIfNeeded();
                        break;
                    case WindowViewMode.Compact:
                        ViewModel.PlayerVisibility = PlayerVisibilityState.Visible;
                        VisualStateManager.GoToState(this, "CompactOverlay", true);
                        LeftPropertiesSidebar.Visibility = Visibility.Collapsed;
                        RightPropertiesSidebar.Visibility = Visibility.Collapsed;
                        break;
                    case WindowViewMode.FullScreen:
                        ViewModel.PlayerVisibility = PlayerVisibilityState.Visible;
                        VisualStateManager.GoToState(this, "Fullscreen", true);
                        RestoreDockedSidebarIfNeeded();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                UpdateContentState();
                break;
            case nameof(PlayerPageViewModel.AudioOnly):
                UpdateContentState();
                UpdateRootTheme();
                UpdatePreviewType();
                break;
            case nameof(PlayerPageViewModel.PlayerVisibility):
                switch (ViewModel.PlayerVisibility)
                {
                    case PlayerVisibilityState.Visible:
                        VisualStateManager.GoToState(this, "NoPreview", true);
                        VisualStateManager.GoToState(this, "Normal", true);
                        PlayerTitleBar.RefreshDragRegion();
                        RestoreDockedSidebarIfNeeded();
                        break;
                    case PlayerVisibilityState.Minimal:
                        VisualStateManager.GoToState(this, "MiniPlayer", true);
                        LeftPropertiesSidebar.Visibility = Visibility.Collapsed;
                        RightPropertiesSidebar.Visibility = Visibility.Collapsed;
                        break;
                    case PlayerVisibilityState.Hidden:
                        VisualStateManager.GoToState(this, "Hidden", true);
                        LeftPropertiesSidebar.Visibility = Visibility.Collapsed;
                        RightPropertiesSidebar.Visibility = Visibility.Collapsed;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                UpdateContentState();
                UpdateRootTheme();
                UpdatePreviewType();
                UpdateMiniPlayerMargin();
                break;
            case nameof(PlayerPageViewModel.NavigationViewDisplayMode) when ViewModel.ViewMode == WindowViewMode.Default:
                UpdateMiniPlayerMargin();
                break;
            case nameof(PlayerPageViewModel.IsPlaying):
                if (ViewModel.IsPlaying)
                {
                    if (_startup)
                    {
                        _startup = false;
                        PlayerControls.FocusFirstButton();
                    }

                    BackgroundArtAnimation.Resume();
                }
                else
                {
                    BackgroundArtAnimation.Pause();
                }

                break;
            case nameof(PlayerPageViewModel.ShowVisualizer):
                if (ViewModel.ShowVisualizer)
                {
                    FindName(nameof(LivelyWallpaperPlayer));
                }

                UpdateContentState();
                break;
            case nameof(PlayerPageViewModel.ShouldClosePlayQueueFlyout) when ViewModel.ShouldClosePlayQueueFlyout:
                if (PlayQueueFlyout.IsOpen)
                {
                    PlayQueueFlyout.Hide();
                }

                ViewModel.ShouldClosePlayQueueFlyout = false;
                break;
        }
    }

    private async void PlayBackgroundArtChangeCrossFadeAnimation()
    {
        _animationCancellationTokenSource?.Cancel();
        if (BackgroundElement.Visibility == Visibility.Collapsed ||
            BackgroundArt.Visibility == Visibility.Collapsed)
        {
            BackgroundImage.Source = AlbumArtImage.ImageSource;
            return;
        }

        using CancellationTokenSource cts = _animationCancellationTokenSource = new CancellationTokenSource();
        if (ViewModel.Media == null)
        {
            await BackgroundArtFadeOutAnimation.StartAsync(cts.Token);
            BackgroundImage.Source = null;
        }
        else if (BackgroundImage.Source == null)
        {
            BackgroundImageNext.Visibility = Visibility.Collapsed;
            BackgroundImage.GetVisual().Opacity = 0;
            BackgroundImage.Source = AlbumArtImage.ImageSource;
            await BackgroundArtFadeInAnimation.StartAsync(cts.Token);
        }
        else
        {
            BackgroundImageNext.Visibility = Visibility.Visible;
            await BackgroundArtFadeOutAnimation.StartAsync(cts.Token);
            BackgroundImage.Source = AlbumArtImage.ImageSource;
            await BackgroundArtFadeInAnimation.StartAsync(cts.Token);
            BackgroundImageNext.Visibility = Visibility.Collapsed;
        }

        if (cts == _animationCancellationTokenSource)
            _animationCancellationTokenSource = null;
    }

    private void UpdateContentState()
    {
        var contentVisualStateName = ViewModel.AudioOnly
            ? (ViewModel is { ShowVisualizer: true, PlayerVisibility: PlayerVisibilityState.Visible, ViewMode: not WindowViewMode.Compact }
                ? "AudioWithVisualizer"
                : "AudioOnly")
            : "Video";
        VisualStateManager.GoToState(this, contentVisualStateName, true);

        LivelyOptionsButton.Visibility = ViewModel.PlayerVisibility == PlayerVisibilityState.Visible &&
                                         ViewModel.ViewMode != WindowViewMode.Compact &&
                                         ViewModel.AudioOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdatePreviewType()
    {
        if (ViewModel.PlayerVisibility == PlayerVisibilityState.Visible || ViewModel.ViewMode == WindowViewMode.Compact)
        {
            VisualStateManager.GoToState(this, "NoPreview", true);
        }
        else
        {
            VisualStateManager.GoToState(this, ViewModel.AudioOnly ? "AudioPreview" : "VideoPreview", true);
        }
    }

    private void UpdateMiniPlayerMargin()
    {
        if (ViewModel.PlayerVisibility == PlayerVisibilityState.Visible || ViewModel.ViewMode == WindowViewMode.Compact)
        {
            VisualStateManager.GoToState(this, "NoMargin", false);
        }
        else
        {
            switch (ViewModel.NavigationViewDisplayMode)
            {
                case NavigationViewDisplayMode.Minimal when ViewModel.PlayerVisibility == PlayerVisibilityState.Hidden:
                    VisualStateManager.GoToState(this, "HiddenMinimalMargin", false);
                    break;
                case NavigationViewDisplayMode.Minimal:
                    VisualStateManager.GoToState(this, "MinimalMargin", false);
                    break;
                case NavigationViewDisplayMode.Compact when ViewModel.PlayerVisibility == PlayerVisibilityState.Hidden:
                case NavigationViewDisplayMode.Expanded when ViewModel.PlayerVisibility == PlayerVisibilityState.Hidden:
                    VisualStateManager.GoToState(this, "HiddenNormalMargin", false);
                    break;
                case NavigationViewDisplayMode.Compact:
                case NavigationViewDisplayMode.Expanded:
                    VisualStateManager.GoToState(this, "NormalMargin", false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private void UpdateRootTheme()
    {
        LayoutRoot.RequestedTheme = !ViewModel.AudioOnly && ViewModel.PlayerVisibility == PlayerVisibilityState.Visible
            ? ElementTheme.Dark
            : ElementTheme.Default;
    }

    private void PlayQueueFlyout_OnOpening(object sender, object e)
    {
        FindName(nameof(PlayQueue));
    }

    private async void PlayQueueFlyout_OnOpened(object sender, object e)
    {
        if (PlayQueue == null) return;
        await PlayQueue.SmoothScrollActiveItemIntoViewAsync();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateBackgroundAcrylicOpacity(ActualTheme);
    }

    private void UpdateBackgroundAcrylicOpacity(ElementTheme theme)
    {
        BackgroundAcrylicBrush.TintLuminosityOpacity = theme == ElementTheme.Light ? 0.5 : 0.4;
    }

    private void PlayQueueButton_OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        _delayFlyoutOpenTimer.Debounce(() => PlayQueueFlyout.ShowAt(PlayQueueButton), TimeSpan.FromMilliseconds(500));
    }

    private void PlayQueueButton_OnDragLeave(object sender, DragEventArgs e)
    {
        _delayFlyoutOpenTimer.Stop();
    }

    private void PlayerControlsBackground_OnTapped(object sender, TappedRoutedEventArgs e)
    {
        PlayerControls.FocusFirstButton(FocusState.Pointer);
        e.Handled = true;
    }

    private async void VideoView_OnManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        await Dispatcher.RunAsync(CoreDispatcherPriority.Low,
            () => PlayerControls.FocusFirstButton(FocusState.Programmatic));
    }

    private void ControlsVisibilityStates_OnCurrentStateChanged(object sender, VisualStateChangedEventArgs e)
    {
        if (e.NewState.Name == nameof(ControlsHidden))
        {
            HiddenButton.Focus(FocusState.Programmatic);
        }
    }

    private void VideoView_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.OnPlayerClick())
        {
            PlayerControls.FocusFirstButton();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Link;
        if (e.DragUIOverride != null) e.DragUIOverride.Caption = Strings.Resources.Play;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        await ViewModel.OnDropAsync(e.DataView);
    }

    private void LayoutRoot_OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalKey is VirtualKey.Space or VirtualKey.GamepadX &&
            !KeyboardAcceleratorHelper.IsControlKeyDown &&
            !KeyboardAcceleratorHelper.IsShiftKeyDown &&
            !e.KeyStatus.IsMenuKeyDown)
        {
            e.Handled = true;
            ViewModel.HandlePlayPauseKeyDown();
        }
    }

    private void LayoutRoot_OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalKey is VirtualKey.Space or VirtualKey.GamepadX &&
            !KeyboardAcceleratorHelper.IsControlKeyDown &&
            !KeyboardAcceleratorHelper.IsShiftKeyDown &&
            !e.KeyStatus.IsMenuKeyDown)
        {
            e.Handled = true;
            ViewModel.HandlePlayPauseKeyUp();
        }
    }

    private void ChangeVolumeKeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.HandleVolumeKey(args.KeyboardAccelerator.Key);
        args.Handled = true;
    }

    private void SeekKeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.HandleSeekKey(args.KeyboardAccelerator.Key, args.KeyboardAccelerator.Modifiers);
        args.Handled = true;
    }

    private void FrameSteppingKeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.HandleFrameSteppingKey(args.KeyboardAccelerator.Key);
        args.Handled = true;
    }

    private void PlaybackRateKeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.HandlePlaybackRateToggleKey(args.KeyboardAccelerator.Key, args.KeyboardAccelerator.Modifiers);
        args.Handled = true;
    }

    private void WindowResizeKeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var boundsRect = Window.Current.CoreWindow.Bounds;

        ViewModel.HandleResizeKey(args.KeyboardAccelerator.Key, args.KeyboardAccelerator.Modifiers, new Size(boundsRect.Width, boundsRect.Height));
        args.Handled = true;
    }

    private void SeekToPercentageKeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.HandlePercentJumpKey(args.KeyboardAccelerator.Key);
        args.Handled = true;
    }

    private void VideoView_OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        PlayerControls.ViewModel.ToggleFullscreenCommand.Execute(null);
    }

    private void EscapeKeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        switch (ViewModel.ViewMode)
        {
            case WindowViewMode.Compact:
            case WindowViewMode.FullScreen:
                ViewModel.GoBack();
                args.Handled = true;
                break;
            case WindowViewMode.Default:
                ViewModel.TryHideControls();
                args.Handled = true;
                break;
        }
    }

    private string GetMessageText(PlaybackCommandKind kind, object? value)
    {
        return kind switch
        {
            PlaybackCommandKind.Rewind or PlaybackCommandKind.FastForward when value is string timeStr
                => _isArabicLanguage ? timeStr : $"\u202A{timeStr}\u202C",
            PlaybackCommandKind.Volume when value is int volume => Strings.Resources.VolumeChangeStatusMessage(volume),
            PlaybackCommandKind.RateUp or PlaybackCommandKind.RateDown when value is double rate
                => _isArabicLanguage ? $"{rate.ToString("0.##", CultureInfo.CurrentCulture)}×" : $"\u202A{rate.ToString("0.##", CultureInfo.CurrentCulture)}×\u202C",
            PlaybackCommandKind.AspectRatio when value is string ratio
                => Strings.Resources.AspectRatioStatusMessage(ItemLabelHelper.GetValueOrFallback(ResourceNameToResourceStringConverter.FromName(ratio), ratio)),
            PlaybackCommandKind.Scale when value is double scale => Strings.Resources.ScaleStatus($"{scale * 100:0.##}%"),
            PlaybackCommandKind.Subtitle when value is string label => Strings.Resources.SubtitleStatus(label),
            PlaybackCommandKind.SubtitleOff => Strings.Resources.SubtitleStatus(Strings.Resources.None),
            _ when value is string str => str,
            _ => string.Empty,
        };
    }

    private HorizontalAlignment GetBadgeHorizontalAlignment(PlaybackCommandKind kind)
    {
        if (ViewModel.PlayerVisibility == PlayerVisibilityState.Minimal)
            return HorizontalAlignment.Center;

        return kind switch
        {
            PlaybackCommandKind.Rewind => GlobalizationHelper.IsRightToLeftLanguage ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            PlaybackCommandKind.FastForward => GlobalizationHelper.IsRightToLeftLanguage ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
    }

    private bool ShouldMirrorBadgeIcon(PlaybackCommandKind kind)
    {
        return kind is not (PlaybackCommandKind.Rewind or PlaybackCommandKind.FastForward
                or PlaybackCommandKind.Next or PlaybackCommandKind.Previous
                or PlaybackCommandKind.RateUp or PlaybackCommandKind.RateDown);
    }
}
