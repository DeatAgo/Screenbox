using CommunityToolkit.Mvvm.DependencyInjection;
using Screenbox.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Screenbox.Controls;

public sealed partial class PropertiesView : UserControl
{
    public static readonly DependencyProperty MediaProperty = DependencyProperty.Register(
        nameof(Media),
        typeof(MediaViewModel),
        typeof(PropertiesView),
        new PropertyMetadata(null, OnMediaChanged));

    public MediaViewModel? Media
    {
        get => (MediaViewModel?)GetValue(MediaProperty);
        set => SetValue(MediaProperty, value);
    }

    internal PropertyViewModel ViewModel => (PropertyViewModel)DataContext;

    public PropertiesView()
    {
        this.InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<PropertyViewModel>();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Media == null) return;
        await ViewModel.LoadPropertiesAsync(Media);
        Bindings.Update();
    }

    private static async void OnMediaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        PropertiesView view = (PropertiesView)d;
        MediaViewModel? media = (MediaViewModel?)e.NewValue;
        if (media == null) return;

        await view.ViewModel.LoadPropertiesAsync(media);
        view.Bindings.Update();
    }
}
