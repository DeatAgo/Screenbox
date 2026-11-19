using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Screenbox.Behaviors;
using Screenbox.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Screenbox.Pages;

public sealed partial class FolderViewPage : Page
{
    internal FolderViewPageViewModel ViewModel => (FolderViewPageViewModel)DataContext;

    internal CommonViewModel Common { get; }

    public Visibility HeaderVisibility { get; private set; }

    private double _contentVerticalOffset;
    private ScrollViewer? _scrollViewer;
    private bool _filteringFolderSelection;

    public FolderViewPage()
    {
        this.InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<FolderViewPageViewModel>();
        Common = Ioc.Default.GetRequiredService<CommonViewModel>();
        FolderView.ChoosingItemContainer += FolderViewOnChoosingItemContainer;
    }

    private void FolderViewOnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
    {
        FolderView.ChoosingItemContainer -= FolderViewOnChoosingItemContainer;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Selection.PropertyChanged += Selection_PropertyChanged;
        HeaderVisibility = e.Parameter is "VideosLibrary" ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Visibility = HeaderVisibility;
        BreadcrumbBar.Visibility = HeaderVisibility;
        if (e.NavigationMode == NavigationMode.Back
            && Common.TryGetPageState(nameof(FolderViewPage), Frame.BackStackDepth, out var state)
            && state is double verticalOffset)
        {
            _contentVerticalOffset = verticalOffset;
        }

        await ViewModel.OnNavigatedTo(e.Parameter);
        RestoreScrollVerticalOffset();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Selection.PropertyChanged -= Selection_PropertyChanged;
        ViewModel.OnNavigatedFrom();
    }

    private void Selection_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectionViewModel.IsSelectionModeActive))
        {
            UpdateFolderContainersSelectionVisibility();
        }
    }

    [DynamicWindowsRuntimeCast(typeof(SelectorItem))]
    private void FolderView_OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is SelectorItem container && args.Item is StorageItemViewModel item)
        {
            ApplyFolderContainerSelectionState(container, item);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(SelectorItem))]
    private void UpdateFolderContainersSelectionVisibility()
    {
        for (int i = 0; i < ViewModel.Items.Count; i++)
        {
            if (FolderView.ContainerFromIndex(i) is SelectorItem container)
            {
                ApplyFolderContainerSelectionState(container, ViewModel.Items[i]);
            }
        }
    }

    private void ApplyFolderContainerSelectionState(SelectorItem container, StorageItemViewModel item)
    {
        bool isFolder = item.Media == null;
        container.IsEnabled = !(isFolder && ViewModel.Selection.IsSelectionModeActive);
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    [DynamicWindowsRuntimeCast(typeof(CheckBox))]
    private static FrameworkElement? FindSelectionCheckBox(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe &&
                (fe.Name is "MultiSelectCheckBox" or "MultiSelectClipTransform" ||
                 fe is CheckBox))
            {
                return fe;
            }

            FrameworkElement? nested = FindSelectionCheckBox(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private void RestoreScrollVerticalOffset()
    {
        if (_scrollViewer == null) return;
        if (_contentVerticalOffset > 0 && _scrollViewer.VerticalOffset == 0)
        {
            _scrollViewer.ChangeView(null, _contentVerticalOffset, null, true);
        }
    }

    private void FolderView_OnItemContextRequested(ListViewContextBehavior sender, ListViewContextRequestedEventArgs e)
    {
        if (e.Item.Content is not StorageItemViewModel content || content.Media == null)
        {
            e.Handled = true;
        }
    }

    private void FolderView_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filteringFolderSelection) return;
        _filteringFolderSelection = true;
        try
        {
            foreach (object added in e.AddedItems)
            {
                if (added is StorageItemViewModel { Media: null })
                {
                    FolderView.SelectedItems.Remove(added);
                }
            }
        }
        finally
        {
            _filteringFolderSelection = false;
        }
    }

    private void SelectDeselectAllKeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.HasVideos)
        {
            ViewModel.Selection.IsSelectionModeActive = true;
            ViewModel.SelectAllOrNoneVideosCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void BreadcrumbBar_OnItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        ViewModel.OnBreadcrumbBarItemClicked(args.Index);
    }

    private void FolderView_OnLoaded(object sender, RoutedEventArgs e)
    {
        _scrollViewer = FolderView.FindDescendant<ScrollViewer>();
        if (_scrollViewer == null) return;
        _scrollViewer.ViewChanging += ScrollViewerOnViewChanging;
    }

    private void ScrollViewerOnViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        Common.SavePageState(e.NextView.VerticalOffset, nameof(FolderViewPage), Frame.BackStackDepth);
    }
}
