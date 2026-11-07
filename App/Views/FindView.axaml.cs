using Avalonia.Controls;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App.Views;

public partial class FindView : UserControl
{
    public FindView()
    {
        InitializeComponent();
        FindGrid.DoubleTapped += (_, _) =>
        {
            if (DataContext is FindTabViewModel viewModel) viewModel.OpenCommand.Execute(null);
        };
    }
}
