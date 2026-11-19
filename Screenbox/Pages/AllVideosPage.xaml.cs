using CommunityToolkit.Mvvm.DependencyInjection;
using Screenbox.Commands;
using Screenbox.Core.ViewModels;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Screenbox.Pages;

/// <summary>
/// A page that displays all videos in a grid view.
/// </summary>
public sealed partial class AllVideosPage : Page
{
    internal AllVideosPageViewModel ViewModel => (AllVideosPageViewModel)DataContext;

    internal CommonViewModel Common { get; }

    private readonly SelectDeselectAllCommand _selectionCommand;

    public AllVideosPage()
    {
        this.InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<AllVideosPageViewModel>();
        Common = Ioc.Default.GetRequiredService<CommonViewModel>();
        _selectionCommand = new SelectDeselectAllCommand();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.UpdateVideos();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Selection.DisableSelectionMode();
    }

    private void SelectDeselectAllKeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.Videos.Count > 0)
        {
            ViewModel.Selection.IsSelectionModeActive = true;
            _selectionCommand.ToggleSelection(VideosGridView);
            args.Handled = true;
        }
    }
}
