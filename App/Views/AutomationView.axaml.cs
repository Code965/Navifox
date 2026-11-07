using System.ComponentModel;
using Avalonia.Controls;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App.Views;

public partial class AutomationView : UserControl
{
    private AutomationTabViewModel? _viewModel;

    public AutomationView()
    {
        InitializeComponent();
        JobSql.TextChanged += (_, _) =>
        {
            if (_viewModel is not null && _viewModel.Sql != JobSql.Text) _viewModel.Sql = JobSql.Text;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnChanged;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as AutomationTabViewModel;
        if (_viewModel is null) return;
        _viewModel.PropertyChanged += OnChanged;
        JobSql.CompletionSource = _viewModel;
        JobSql.Text = _viewModel.Sql;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AutomationTabViewModel.Sql) && _viewModel is not null && JobSql.Text != _viewModel.Sql) JobSql.Text = _viewModel.Sql;
    }
}
