using Screenbox.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Screenbox.Selectors;

public sealed partial class FolderViewItemContainerStyleSelector : StyleSelector
{
    public Style? VideoItemStyle { get; set; }

    public Style? FolderItemStyle { get; set; }

    protected override Style? SelectStyleCore(object item, DependencyObject container)
    {
        if (item is StorageItemViewModel { Media: null })
        {
            return FolderItemStyle ?? VideoItemStyle;
        }

        return VideoItemStyle;
    }
}
