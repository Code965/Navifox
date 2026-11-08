using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Navifox.App.ViewModels;

namespace Navifox.App.Views;

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
        InitFindBar();
        SqlEditor.SnippetLookup = name => Services.SnippetStore.Find(name)?.Body;
        ResultGrid.SelectionChanged += (_, _) => UpdateSelectionSummary();
        ResultGrid.CurrentCellChanged += (_, _) => UpdateSelectionSummary();
        RunButton.Click += (_, _) => Run(RunMode.Auto);
        RunSelectionItem.Click += (_, _) => Run(RunMode.Selection);
        RunCurrentItem.Click += (_, _) => Run(RunMode.Current);
        MultiRunItem.Click += async (_, _) => await OpenMultiRunAsync();
        AssistExplainItem.Click += (_, _) =>
        {
            if (_viewModel is null) return;
            CaptureEditorState();
            _viewModel.AssistExplainCommand.Execute(null);
        };
        AssistOptimizeItem.Click += (_, _) =>
        {
            if (_viewModel is null) return;
            CaptureEditorState();
            _viewModel.AssistOptimizeCommand.Execute(null);
        };
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
                    Icon = new Navifox.App.Controls.NfIcon { Kind = snippet.Icon, Width = 14, Height = 14, Classes = { "menu-icon" } },
                    Command = _viewModel.InsertSnippetCommand,
                    CommandParameter = snippet.Name
                });
            // Snippet personali (con scorciatoia: parola + Tab nell'editor).
            var personal = Services.SnippetStore.All;
            if (personal.Count > 0) flyout.Items.Add(new Separator());
            foreach (var snippet in personal)
            {
                var item = new MenuItem
                {
                    Header = snippet.Shortcut.Length > 0 ? $"{snippet.Name}   ({snippet.Shortcut} + Tab)" : snippet.Name,
                    Icon = new Navifox.App.Controls.NfIcon { Kind = "star", Width = 14, Height = 14, Classes = { "menu-icon" } }
                };
                var body = snippet.Body;
                item.Click += (_, _) => InsertSnippetText(body);
                flyout.Items.Add(item);
            }
            flyout.Items.Add(new Separator());
            var fromSelection = new MenuItem { Header = "Salva la selezione come snippet…", IsEnabled = !string.IsNullOrWhiteSpace(SqlEditor.SelectedText) };
            fromSelection.Click += async (_, _) => await ManageSnippetsAsync(SqlEditor.SelectedText);
            var manage = new MenuItem { Header = "Gestisci snippet personali…" };
            manage.Click += async (_, _) => await ManageSnippetsAsync(null);
            flyout.Items.Add(fromSelection);
            flyout.Items.Add(manage);
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
        RebuildGrid();
        FitSoon();
        SqlEditor.CompletionSource = _viewModel;
        _viewModel.InsertRequested += OnInsert;
        if (SqlEditor.Text != _viewModel.SqlText) SqlEditor.Text = _viewModel.SqlText;
    }

    private void RebuildGrid()
    {
        if (_viewModel is not { } vm) return;
        ResultGridBuilder.Rebuild(ResultGrid, vm.Columns, filterText: vm.QuickFilterText, onFilter: vm.SetQuickFilter);
    }

    /// <summary>Più righe selezionate: conteggio, somma, media, min e max della colonna corrente.</summary>
    private void UpdateSelectionSummary()
    {
        if (_viewModel is null) return;
        var rows = ResultGrid.SelectedItems.OfType<TableRowViewModel>().ToArray();
        var column = ResultGrid.CurrentColumn?.Tag is int index ? index : -1;
        _viewModel.SelectionSummary = rows.Length < 2 || column < 0
            ? string.Empty
            : Navifox.Core.SelectionStats.Compute(rows.Select(row => row.ValueAt(column))).Format();
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            RebuildGrid();
            FitSoon();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryTabViewModel.SqlText) && _viewModel is not null && SqlEditor.Text != _viewModel.SqlText) SqlEditor.Text = _viewModel.SqlText;
        if (e.PropertyName == nameof(QueryTabViewModel.ShowQuickFilter) && _viewModel is not null)
        {
            if (!_viewModel.ShowQuickFilter) ResultGridBuilder.ClearFilters(ResultGrid);
            ResultGridBuilder.SetFilterRowVisible(ResultGrid, _viewModel.ShowQuickFilter);
        }
        if (e.PropertyName != nameof(QueryTabViewModel.ConsoleText)) return;
        Dispatcher.UIThread.Post(() =>
        {
            ConsoleOutput.CaretIndex = ConsoleOutput.Text?.Length ?? 0;
        }, DispatcherPriority.Background);
    }

    private enum RunMode { Auto, Selection, Current }

    /// <summary>Stessa query (selezione, altrimenti tutto l'editor) su più connessioni o database, risultati affiancati.</summary>
    private async Task OpenMultiRunAsync()
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var sql = string.IsNullOrWhiteSpace(SqlEditor.SelectedText) ? SqlEditor.Text ?? string.Empty : SqlEditor.SelectedText;
        if (string.IsNullOrWhiteSpace(sql)) return;
        var access = App.Services.GetService(typeof(Services.IDatabaseAccessService)) as Services.IDatabaseAccessService;
        if (access is null || _viewModel.SelectedProfile is not { } profile) return;
        await new MultiRunWindow(access, _viewModel.Profiles, profile, _viewModel.SelectedDatabase, sql).ShowDialog(owner);
    }

    private void InsertSnippetText(string body)
    {
        var caret = body.IndexOf("$0", StringComparison.Ordinal);
        body = body.Replace("$0", string.Empty);
        var start = SqlEditor.SelectionLength > 0 ? SqlEditor.SelectionStart : SqlEditor.CaretOffset;
        SqlEditor.Document.Replace(start, SqlEditor.SelectionLength, body);
        SqlEditor.CaretOffset = start + (caret >= 0 ? caret : body.Length);
        SqlEditor.Focus();
    }

    private async Task ManageSnippetsAsync(string? initialBody)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await new SnippetManagerWindow(initialBody).ShowDialog<bool>(owner);
        SqlEditor.Focus();
    }

    // ───────────── Trova e sostituisci ─────────────

    private void InitFindBar()
    {
        FindBox.TextChanged += (_, _) => UpdateFindCount(select: true);
        foreach (var toggle in new[] { MatchCaseToggle, WholeWordToggle, RegexToggle })
            toggle.IsCheckedChanged += (_, _) => UpdateFindCount(select: false);
        FindNextButton.Click += (_, _) => FindNext(backwards: false);
        FindPrevButton.Click += (_, _) => FindNext(backwards: true);
        FindCloseButton.Click += (_, _) => CloseFind();
        ReplaceOneButton.Click += (_, _) => ReplaceOne();
        ReplaceAllButton.Click += (_, _) => ReplaceAll();
        FindBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
        };
        ReplaceBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { ReplaceOne(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
        };
    }

    private System.Text.RegularExpressions.Regex? FindRegex() =>
        Navifox.Core.TextSearch.Build(FindBox.Text ?? string.Empty, MatchCaseToggle.IsChecked == true, WholeWordToggle.IsChecked == true, RegexToggle.IsChecked == true);

    private void OpenFind(bool replace)
    {
        FindBar.IsVisible = true;
        ReplaceToggle.IsChecked = replace || ReplaceToggle.IsChecked == true;
        var selected = SqlEditor.SelectedText;
        if (!string.IsNullOrEmpty(selected) && !selected.Contains('\n')) FindBox.Text = selected;
        FindBox.Focus();
        FindBox.SelectAll();
        UpdateFindCount(select: false);
    }

    private void CloseFind()
    {
        FindBar.IsVisible = false;
        SqlEditor.Focus();
    }

    private void UpdateFindCount(bool select)
    {
        var regex = FindRegex();
        var text = SqlEditor.Text ?? string.Empty;
        var matches = Navifox.Core.TextSearch.FindAll(text, regex);
        FindCount.Text = string.IsNullOrEmpty(FindBox.Text) ? string.Empty
            : regex is null ? "espressione non valida"
            : matches.Count == 0 ? "non presente nel testo SQL" : matches.Count == 1 ? "1 risultato" : $"{matches.Count:N0} risultati";
        if (select && Navifox.Core.TextSearch.Next(matches, SqlEditor.SelectionStart, false) is { } first) Select(first);
    }

    private void FindNext(bool backwards)
    {
        if (!FindBar.IsVisible || string.IsNullOrEmpty(FindBox.Text)) { OpenFind(replace: false); return; }
        var matches = Navifox.Core.TextSearch.FindAll(SqlEditor.Text ?? string.Empty, FindRegex());
        var from = backwards ? SqlEditor.SelectionStart : SqlEditor.SelectionStart + Math.Max(1, SqlEditor.SelectionLength);
        if (Navifox.Core.TextSearch.Next(matches, from, backwards) is { } match) Select(match);
    }

    private void Select(Navifox.Core.TextMatch match)
    {
        SqlEditor.Select(match.Index, match.Length);
        SqlEditor.TextArea.Caret.BringCaretToView();
        SqlEditor.ScrollTo(SqlEditor.Document.GetLineByOffset(match.Index).LineNumber, 0);
    }

    private void ReplaceOne()
    {
        var regex = FindRegex();
        if (regex is null) return;
        var text = SqlEditor.Text ?? string.Empty;
        var matches = Navifox.Core.TextSearch.FindAll(text, regex);
        var current = matches.FirstOrDefault(m => m.Index == SqlEditor.SelectionStart && m.Length == SqlEditor.SelectionLength);
        if (current is null) { FindNext(backwards: false); return; }
        var replacement = Navifox.Core.TextSearch.Replacement(regex, text, current, ReplaceBox.Text ?? string.Empty, RegexToggle.IsChecked == true);
        SqlEditor.Document.Replace(current.Index, current.Length, replacement);
        SqlEditor.Select(current.Index + replacement.Length, 0);
        UpdateFindCount(select: false);
        FindNext(backwards: false);
    }

    private void ReplaceAll()
    {
        var (text, count) = Navifox.Core.TextSearch.ReplaceAll(SqlEditor.Text ?? string.Empty, FindRegex(), ReplaceBox.Text ?? string.Empty, RegexToggle.IsChecked == true);
        if (count == 0) { FindCount.Text = "nessun risultato"; return; }
        // Una sola operazione annullabile con Ctrl+Z.
        SqlEditor.Document.Replace(0, SqlEditor.Document.TextLength, text);
        FindCount.Text = count == 1 ? "1 sostituzione" : $"{count:N0} sostituzioni";
    }

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
        else if (e.Key == Key.F && ctrl) { OpenFind(replace: false); e.Handled = true; }
        else if (e.Key == Key.H && ctrl) { OpenFind(replace: true); e.Handled = true; }
        else if (e.Key == Key.F3) { FindNext(backwards: shift); e.Handled = true; }
        else if (e.Key == Key.Escape && FindBar.IsVisible) { CloseFind(); e.Handled = true; }
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
