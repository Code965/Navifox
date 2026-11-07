using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App.Views;

public partial class QueryPageView : UserControl
{
    private QueryTabViewModel? _viewModel;

    public QueryPageView()
    {
        InitializeComponent();
        SqlEditor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        SqlEditor.TextChanged += (_, _) =>
        {
            if (_viewModel is not null && _viewModel.SqlText != SqlEditor.Text) _viewModel.SqlText = SqlEditor.Text;
        };
        ConsoleInputBox.AddHandler(KeyDownEvent, OnConsoleKeyDown, RoutingStrategies.Tunnel);
        RunButton.Click += (_, _) => Run(RunMode.Auto);
        RunSelectionItem.Click += (_, _) => Run(RunMode.Selection);
        RunCurrentItem.Click += (_, _) => Run(RunMode.Current);
        ExplainButton.Click += (_, _) =>
        {
            if (_viewModel is null) return;
            CaptureEditorState();
            _viewModel.ExplainCommand.Execute(null);
        };
        SnippetButton.Click += (_, _) =>
        {
            if (_viewModel is null) return;
            var flyout = new MenuFlyout { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft };
            foreach (var snippet in _viewModel.Snippets)
                flyout.Items.Add(new MenuItem
                {
                    Header = snippet.Name,
                    Icon = new MosaicDB.App.Controls.NfIcon { Kind = snippet.Icon, Width = 14, Height = 14, Classes = { "menu-icon" } },
                    Command = _viewModel.InsertSnippetCommand,
                    CommandParameter = snippet.Name
                });
            flyout.ShowAt(SnippetButton);
        };
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel?.IsConsole == true) ConsoleInputBox.Focus();
            else SqlEditor.Focus();
        }, DispatcherPriority.Input);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.InsertRequested -= OnInsert;
            _viewModel.Columns.CollectionChanged -= OnColumnsChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        base.OnDataContextChanged(e);
        _viewModel = DataContext as QueryTabViewModel;
        if (_viewModel is null) return;
        _viewModel.Columns.CollectionChanged += OnColumnsChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ResultGridBuilder.Rebuild(ResultGrid, _viewModel.Columns);
        FitSoon();
        SqlEditor.CompletionSource = _viewModel;
        _viewModel.InsertRequested += OnInsert;
        if (SqlEditor.Text != _viewModel.SqlText) SqlEditor.Text = _viewModel.SqlText;
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            ResultGridBuilder.Rebuild(ResultGrid, _viewModel.Columns);
            FitSoon();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryTabViewModel.SqlText) && _viewModel is not null && SqlEditor.Text != _viewModel.SqlText) SqlEditor.Text = _viewModel.SqlText;
        if (e.PropertyName != nameof(QueryTabViewModel.ConsoleText)) return;
        Dispatcher.UIThread.Post(() =>
        {
            ConsoleOutput.CaretIndex = ConsoleOutput.Text?.Length ?? 0;
        }, DispatcherPriority.Background);
    }

    private enum RunMode { Auto, Selection, Current }

    private void OnInsert(string text)
    {
        SqlEditor.Document.Insert(SqlEditor.CaretOffset, text);
        SqlEditor.Focus();
    }

    /// <summary>Selezione e istruzione sotto il cursore (separata da «;» o righe vuote).</summary>
    private void CaptureEditorState()
    {
        if (_viewModel is null) return;
        _viewModel.SelectedSql = SqlEditor.SelectedText;
        var text = SqlEditor.Text ?? string.Empty;
        var caret = Math.Clamp(SqlEditor.CaretOffset, 0, text.Length);
        var start = text.LastIndexOf(';', Math.Max(0, caret - 1)) + 1;
        if (caret > 0 && caret <= text.Length && text[caret - 1] == ';') start = text.LastIndexOf(';', Math.Max(0, caret - 2)) + 1;
        var end = text.IndexOf(';', caret);
        if (caret > 0 && text[caret - 1] == ';') end = caret - 1;
        end = end < 0 ? text.Length : end;
        _viewModel.CurrentStatementSql = start <= end ? text[start..end].Trim() : null;
    }

    private void Run(RunMode mode)
    {
        if (_viewModel is null) return;
        CaptureEditorState();
        switch (mode)
        {
            case RunMode.Current: _viewModel.ExecuteCurrentCommand.Execute(null); break;
            case RunMode.Selection when string.IsNullOrWhiteSpace(_viewModel.SelectedSql): _viewModel.ExecuteCurrentCommand.Execute(null); break;
            default: _viewModel.ExecuteCommand.Execute(null); break;
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Enter && ctrl && shift) { Run(RunMode.Current); e.Handled = true; }
        else if (e.Key == Key.F5 || e.Key == Key.Enter && ctrl) { Run(RunMode.Auto); e.Handled = true; }
        else if (e.Key == Key.R && ctrl) { _viewModel.ExecuteAllCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.S && ctrl) { _viewModel.SaveCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F && ctrl && shift) { _viewModel.BeautifyCommand.Execute(null); e.Handled = true; }
    }

    private void OnConsoleKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.SubmitConsoleCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.HistoryUp();
                ConsoleInputBox.CaretIndex = ConsoleInputBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Down:
                _viewModel.HistoryDown();
                ConsoleInputBox.CaretIndex = ConsoleInputBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
        }
    }

    /// <summary>Adatta le colonne al contenuto quando le righe del risultato sono arrivate.</summary>
    private void FitSoon() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_viewModel is not null) ResultGridBuilder.FitToContent(ResultGrid, _viewModel.Rows);
    }, Avalonia.Threading.DispatcherPriority.Background);
}
