using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Screenbox.Core.Common;
using Screenbox.Core.Enums;
using Screenbox.Core.Messages;
using Screenbox.Core.ViewModels;
using Screenbox.Helpers;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;

namespace Screenbox.Dialogs;

public sealed partial class PropertiesDialog : UserControl, IPropertiesDialog
{
    public static readonly DependencyProperty MediaProperty = DependencyProperty.Register(
        nameof(Media),
        typeof(MediaViewModel),
        typeof(PropertiesDialog),
        new PropertyMetadata(null));

    public MediaViewModel? Media
    {
        get => (MediaViewModel?)GetValue(MediaProperty);
        set => SetValue(MediaProperty, value);
    }

    public object Title { get; set; } = Screenbox.Strings.Resources.Properties;

    public ContentDialogButton DefaultButton { get; set; } = ContentDialogButton.Close;

    public static PropertiesDockMode PreferredDockMode
    {
        get => SettingsPageViewModel.GetPropertiesDockMode();
        set => SettingsPageViewModel.SetPropertiesDockMode(value);
    }

    private static PropertiesDialog? _activeDialog;
    private readonly Popup _popup;

    private bool _isDragging;
    private Point _dragStartPoint;
    private double _dragStartOffsetX;
    private double _dragStartOffsetY;

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    public PropertiesDialog()
    {
        this.InitializeComponent();
        FlowDirection = GlobalizationHelper.GetFlowDirection();
        RequestedTheme = ((FrameworkElement)Window.Current.Content).RequestedTheme;

        _popup = new Popup
        {
            Child = this,
            IsLightDismissEnabled = false,
            ShouldConstrainToRootBounds = true
        };
    }

    public IAsyncOperation<ContentDialogResult> ShowAsync()
    {
        if (PreferredDockMode is PropertiesDockMode.Left or PropertiesDockMode.Right)
        {
            WeakReferenceMessenger.Default.Send(new PropertiesDockMessage(PreferredDockMode, Media, IsOpen: true));
            return Task.FromResult(ContentDialogResult.None).AsAsyncOperation();
        }

        return ShowFloatingAsync();
    }

    public IAsyncOperation<ContentDialogResult> ShowFloatingAsync()
    {
        if (_activeDialog != null && _activeDialog._popup.IsOpen)
        {
            _activeDialog.Media = Media;
            return Task.FromResult(ContentDialogResult.None).AsAsyncOperation();
        }

        _activeDialog = this;

        var bounds = Window.Current.Bounds;
        Width = Math.Min(480, Math.Max(MinWidth, bounds.Width - 48));
        Height = Math.Min(560, Math.Max(MinHeight, bounds.Height - 48));

        _popup.HorizontalOffset = Math.Max(16, (bounds.Width - Width) / 2);
        _popup.VerticalOffset = Math.Max(16, (bounds.Height - Height) / 2);
        _popup.IsOpen = true;

        return Task.FromResult(ContentDialogResult.None).AsAsyncOperation();
    }

    private void DockTo(PropertiesDockMode mode)
    {
        PreferredDockMode = mode;
        MediaViewModel? currentMedia = Media;
        CloseDialog();
        WeakReferenceMessenger.Default.Send(new PropertiesDockMessage(mode, currentMedia, IsOpen: true));
    }

    private void DockLeftButton_Click(object sender, RoutedEventArgs e) => DockTo(PropertiesDockMode.Left);

    private void DockRightButton_Click(object sender, RoutedEventArgs e) => DockTo(PropertiesDockMode.Right);

    private void CloseDialog()
    {
        Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);
        _popup.IsOpen = false;
        if (ReferenceEquals(_activeDialog, this))
        {
            _activeDialog = null;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseDialog();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            CloseDialog();
            e.Handled = true;
        }
    }

    private void HeaderGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(HeaderGrid).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStartPoint = e.GetCurrentPoint(null).Position;
            _dragStartOffsetX = _popup.HorizontalOffset;
            _dragStartOffsetY = _popup.VerticalOffset;
            HeaderGrid.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void HeaderGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;

        Point current = e.GetCurrentPoint(null).Position;
        double deltaX = current.X - _dragStartPoint.X;
        double deltaY = current.Y - _dragStartPoint.Y;

        var bounds = Window.Current.Bounds;
        double newX = _dragStartOffsetX + deltaX;
        double newY = _dragStartOffsetY + deltaY;

        _popup.HorizontalOffset = Math.Max(0, Math.Min(newX, Math.Max(0, bounds.Width - 120)));
        _popup.VerticalOffset = Math.Max(0, Math.Min(newY, Math.Max(0, bounds.Height - 48)));
        e.Handled = true;
    }

    private void HeaderGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            HeaderGrid.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void ResizeBy(double leftDelta, double topDelta, double rightDelta, double bottomDelta)
    {
        var bounds = Window.Current.Bounds;
        double currentW = double.IsNaN(Width) ? ActualWidth : Width;
        double currentH = double.IsNaN(Height) ? ActualHeight : Height;

        if (leftDelta != 0)
        {
            double maxLeftShift = currentW - MinWidth;
            double minLeftShift = -_popup.HorizontalOffset;
            double clampedLeft = Math.Max(minLeftShift, Math.Min(maxLeftShift, leftDelta));
            _popup.HorizontalOffset += clampedLeft;
            Width = currentW - clampedLeft;
        }
        else if (rightDelta != 0)
        {
            double maxW = Math.Max(MinWidth, bounds.Width - _popup.HorizontalOffset - 8);
            Width = Math.Max(MinWidth, Math.Min(maxW, currentW + rightDelta));
        }

        if (topDelta != 0)
        {
            double maxTopShift = currentH - MinHeight;
            double minTopShift = -_popup.VerticalOffset;
            double clampedTop = Math.Max(minTopShift, Math.Min(maxTopShift, topDelta));
            _popup.VerticalOffset += clampedTop;
            Height = currentH - clampedTop;
        }
        else if (bottomDelta != 0)
        {
            double maxH = Math.Max(MinHeight, bounds.Height - _popup.VerticalOffset - 8);
            Height = Math.Max(MinHeight, Math.Min(maxH, currentH + bottomDelta));
        }
    }

    private void TopThumb_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(0, e.VerticalChange, 0, 0);
    private void BottomThumb_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(0, 0, 0, e.VerticalChange);
    private void LeftThumb_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(e.HorizontalChange, 0, 0, 0);
    private void RightThumb_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(0, 0, e.HorizontalChange, 0);
    private void TopLeftThumb_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(e.HorizontalChange, e.VerticalChange, 0, 0);
    private void TopRightThumb_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(0, e.VerticalChange, e.HorizontalChange, 0);
    private void BottomLeftThumb_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(e.HorizontalChange, 0, 0, e.VerticalChange);
    private void BottomRightThumb_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBy(0, 0, e.HorizontalChange, e.VerticalChange);

    private void NorthSouth_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeNorthSouth, 0);

    private void EastWest_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeWestEast, 0);

    private void NwSe_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeNorthwestSoutheast, 0);

    private void NeSw_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeNortheastSouthwest, 0);

    private void EdgeThumb_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!e.Pointer.IsInContact)
        {
            Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);
        }
    }
}
