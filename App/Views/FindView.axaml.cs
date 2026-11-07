using Avalonia.Controls;
using Navifox.App.ViewModels;

namespace Navifox.App.Views;

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
