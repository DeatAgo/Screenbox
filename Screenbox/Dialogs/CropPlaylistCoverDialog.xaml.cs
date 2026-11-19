using System;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace Screenbox.Dialogs;

public sealed partial class CropPlaylistCoverDialog : ContentDialog
{
    private const double ViewportSize = 280.0;

    private readonly StorageFile _sourceFile;
    private readonly StorageFolder _destinationFolder;
    private readonly string _destinationBaseName;

    private uint _orientedWidth;
    private uint _orientedHeight;
    private double _baseDisplayWidth;
    private double _baseDisplayHeight;
    private bool _isUpdatingZoom;
    private bool _isDragging;
    private Point _dragStartPoint;
    private double _dragStartHorizontalOffset;
    private double _dragStartVerticalOffset;

    public StorageFile? SavedCroppedFile { get; private set; }

    private CropPlaylistCoverDialog(StorageFile sourceFile, StorageFolder destinationFolder, string destinationBaseName)
    {
        this.InitializeComponent();
        _sourceFile = sourceFile;
        _destinationFolder = destinationFolder;
        _destinationBaseName = destinationBaseName;
    }

    public static async Task<StorageFile?> CropAndSaveAsync(
        StorageFile sourceFile,
        StorageFolder destinationFolder,
        string destinationBaseName)
    {
        uint width;
        uint height;
        using (IRandomAccessStream stream = await sourceFile.OpenReadAsync())
        {
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            width = decoder.OrientedPixelWidth;
            height = decoder.OrientedPixelHeight;
        }

        if (width == 0 || height == 0)
            return null;

        // Already 1:1 square — copy directly without showing crop UI
        if (width == height)
        {
            string ext = string.IsNullOrWhiteSpace(sourceFile.FileType) ? ".png" : sourceFile.FileType;
            return await sourceFile.CopyAsync(
                destinationFolder,
                $"{destinationBaseName}{ext}",
                NameCollisionOption.ReplaceExisting);
        }

        var dialog = new CropPlaylistCoverDialog(sourceFile, destinationFolder, destinationBaseName)
        {
            _orientedWidth = width,
            _orientedHeight = height
        };

        await dialog.InitializePreviewAsync();
        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? dialog.SavedCroppedFile : null;
    }

    private async Task InitializePreviewAsync()
    {
        // Scale image so its shorter side fills the 280x280 viewport at ZoomFactor = 1.0
        double minSide = Math.Min(_orientedWidth, _orientedHeight);
        double scale = ViewportSize / minSide;
        _baseDisplayWidth = _orientedWidth * scale;
        _baseDisplayHeight = _orientedHeight * scale;

        PreviewImage.Width = _baseDisplayWidth;
        PreviewImage.Height = _baseDisplayHeight;

        using IRandomAccessStream stream = await _sourceFile.OpenReadAsync();
        BitmapImage bitmap = new()
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelHeight = (int)Math.Ceiling(_baseDisplayHeight * 2)
        };
        await bitmap.SetSourceAsync(stream);
        PreviewImage.Source = bitmap;

        // Center the initial square crop once layout is measured
        Loaded += (_, _) =>
        {
            double initialOffsetX = Math.Max(0, (_baseDisplayWidth - ViewportSize) / 2.0);
            double initialOffsetY = Math.Max(0, (_baseDisplayHeight - ViewportSize) / 2.0);
            CropScrollViewer.ChangeView(initialOffsetX, initialOffsetY, 1.0f, disableAnimation: true);
        };
    }

    private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingZoom || CropScrollViewer is null) return;

        _isUpdatingZoom = true;
        try
        {
            float oldZoom = CropScrollViewer.ZoomFactor;
            float newZoom = (float)e.NewValue;
            if (Math.Abs(oldZoom - newZoom) < 0.001f) return;

            // Keep the center of the viewport anchored while zooming
            double centerX = (CropScrollViewer.HorizontalOffset + ViewportSize / 2.0) / oldZoom;
            double centerY = (CropScrollViewer.VerticalOffset + ViewportSize / 2.0) / oldZoom;

            double targetOffsetX = Math.Max(0, centerX * newZoom - ViewportSize / 2.0);
            double targetOffsetY = Math.Max(0, centerY * newZoom - ViewportSize / 2.0);

            CropScrollViewer.ChangeView(targetOffsetX, targetOffsetY, newZoom, disableAnimation: true);
        }
        finally
        {
            _isUpdatingZoom = false;
        }
    }

    private void CropScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isUpdatingZoom) return;

        _isUpdatingZoom = true;
        try
        {
            ZoomSlider.Value = CropScrollViewer.ZoomFactor;
        }
        finally
        {
            _isUpdatingZoom = false;
        }
    }

    private void PreviewImage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(PreviewImage).Properties.IsLeftButtonPressed) return;

        _isDragging = true;
        _dragStartPoint = e.GetCurrentPoint(this).Position;
        _dragStartHorizontalOffset = CropScrollViewer.HorizontalOffset;
        _dragStartVerticalOffset = CropScrollViewer.VerticalOffset;
        PreviewImage.CapturePointer(e.Pointer);
    }

    private void PreviewImage_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;

        Point current = e.GetCurrentPoint(this).Position;
        double deltaX = current.X - _dragStartPoint.X;
        double deltaY = current.Y - _dragStartPoint.Y;

        double targetX = Math.Max(0, _dragStartHorizontalOffset - deltaX);
        double targetY = Math.Max(0, _dragStartVerticalOffset - deltaY);
        CropScrollViewer.ChangeView(targetX, targetY, null, disableAnimation: true);
    }

    private void PreviewImage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;

        _isDragging = false;
        PreviewImage.ReleasePointerCapture(e.Pointer);
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        try
        {
            double zoom = CropScrollViewer.ZoomFactor;
            double unzoomedX = CropScrollViewer.HorizontalOffset / zoom;
            double unzoomedY = CropScrollViewer.VerticalOffset / zoom;
            double unzoomedSide = ViewportSize / zoom;

            double pixelsPerDisplayUnit = _orientedWidth / _baseDisplayWidth;

            uint cropX = (uint)Math.Max(0, Math.Round(unzoomedX * pixelsPerDisplayUnit));
            uint cropY = (uint)Math.Max(0, Math.Round(unzoomedY * pixelsPerDisplayUnit));
            uint cropSize = (uint)Math.Max(1, Math.Round(unzoomedSide * pixelsPerDisplayUnit));

            // Clamp inside source bounds
            if (cropX + cropSize > _orientedWidth)
            {
                cropSize = _orientedWidth - cropX;
            }
            if (cropY + cropSize > _orientedHeight)
            {
                cropSize = _orientedHeight - cropY;
            }

            uint finalSquare = Math.Max(1, Math.Min(cropSize, Math.Min(_orientedWidth - cropX, _orientedHeight - cropY)));

            StorageFile outputFile = await _destinationFolder.CreateFileAsync(
                $"{_destinationBaseName}.png",
                CreationCollisionOption.ReplaceExisting);

            using IRandomAccessStream inputStream = await _sourceFile.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(inputStream);

            // Cap output resolution at 600x600 so UI thumbnails stay fast and lightweight
            uint targetDimension = Math.Min(finalSquare, 600u);
            double resizeRatio = (double)targetDimension / finalSquare;

            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(_orientedWidth * resizeRatio)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(_orientedHeight * resizeRatio)),
                InterpolationMode = BitmapInterpolationMode.Fant,
                Bounds = new BitmapBounds
                {
                    X = (uint)Math.Round(cropX * resizeRatio),
                    Y = (uint)Math.Round(cropY * resizeRatio),
                    Width = targetDimension,
                    Height = targetDimension
                }
            };

            PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            byte[] pixels = pixelData.DetachPixelData();

            using IRandomAccessStream outputStream = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                targetDimension,
                targetDimension,
                decoder.DpiX,
                decoder.DpiY,
                pixels);
            await encoder.FlushAsync();

            SavedCroppedFile = outputFile;
        }
        finally
        {
            deferral.Complete();
        }
    }
}
