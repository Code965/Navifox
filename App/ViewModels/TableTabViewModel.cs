using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>
/// Scheda dati di una tabella: griglia paginata e modificabile, barra filtri e ordinamento,
/// editor di campo (Testo / Esadecimale), transazioni esplicite, import ed export.
/// </summary>
public partial class TableTabViewModel : WorkspaceTabViewModel
{
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IDialogService _dialogs;
    private readonly ConnectionProfile _profile;
    public override ConnectionProfile? TabConnection => _profile;
    private readonly IDialectProvider _provider;
    private IReadOnlyList<ColumnInfo> _metadata = [];
    private DbConnection? _transaction;
    private CancellationTokenSource? _running;
    private string? _appliedWhere;
    private string? _appliedOrderBy;
    private int _pageIndex;
    // Ultima impostazione scelta: le nuove schede la riprendono.
    private static bool s_defaultLimit = true;
    private static int s_defaultPageSize = 1000;

    public static void SetDefaults(bool limit, int pageSize)
    {
        s_defaultLimit = limit;
        s_defaultPageSize = Math.Clamp(pageSize, 1, 1_000_000);
    }

    /// <summary>Apre la barra filtri con «colonna contiene testo» e applica (usato da Trova nel database).</summary>
    public async Task FilterContainsAsync(string column, string text)
    {
        if (_metadata.Count == 0) await LoadAsync();
        Filters.Clear();
        Filters.Add(new FilterConditionViewModel(ColumnNames, column) { Operator = FilterOperator.All.First(op => op.Kind == "contains"), Value = text });
        IsFilterOpen = true;
        await ApplyFilterAsync();
    }

    public TableTabViewModel(IDatabaseAccessService databaseAccess, IDialogService dialogs, ConnectionProfile profile, string database, string schema, string table, bool isView = false)
    {
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        _profile = profile;
        _provider = databaseAccess.Provider(profile);
        Database = database;
        Schema = schema;
        TableName = table;
        IsView = isView;
        TabTitle = $"{table} @{database} ({profile.Name})";
        Context = schema == database ? $"{profile.Name} · {database}" : $"{profile.Name} · {database} · {schema}";
        Filters.CollectionChanged += (_, _) => RenumberFilters();
    }

    public override string TabTitle { get; }
    public override string TabGlyph => IsView ? "eye" : "table";
    public string Context { get; }
    public Guid ProfileId => _profile.Id;
    public string Database { get; }
    public string Schema { get; }
    public string TableName { get; }
    public bool IsView { get; }
    public IDialectProvider Provider => _provider;

    public ObservableCollection<ColumnInfo> Columns { get; } = [];
    [ObservableProperty] private ObservableCollection<TableRowViewModel> _rows = [];

    /// <summary>Impostazioni dell'ingranaggio: limite di record per pagina (deselezionato = nessun limite).</summary>
    [ObservableProperty] private bool _limitRecords = s_defaultLimit;
    [ObservableProperty] private decimal? _pageSizeSetting = s_defaultPageSize;
    [ObservableProperty] private bool _isLimitPanelOpen;
    public int PageSize => LimitRecords ? (int)Math.Clamp(PageSizeSetting ?? 1000, 1, 1_000_000) : 0;
    public string PageInfoLabel => PageSize == 0 ? "Tutti i record in un’unica pagina" : $"Pagina {_pageIndex + 1} · {PageSize:N0} record per pagina";
    public ObservableCollection<FilterConditionViewModel> Filters { get; } = [];
    public ObservableCollection<SortConditionViewModel> Sorts { get; } = [];
    public IReadOnlyList<string> ColumnNames => Columns.Select(column => column.Name).ToArray();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _sqlPreview = string.Empty;
    [ObservableProperty] private long _totalRows = -1;
    [ObservableProperty] private string _recordLabel = string.Empty;
    [ObservableProperty] private string _pageLabel = "1";
    [ObservableProperty] private bool _isFilterOpen;
    [ObservableProperty] private bool _isSortOpen;
    [ObservableProperty] private bool _isFieldPanelOpen;
    [ObservableProperty] private bool _isHexMode;
    [ObservableProperty] private bool _isTransaction;
    [ObservableProperty] private bool _hasPendingChanges;
    [ObservableProperty] private bool _canEdit;
    [ObservableProperty] private string _editHint = string.Empty;
    [ObservableProperty] private bool _isFilterActive;
    [ObservableProperty] private bool _isSortActive;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentCellText), nameof(CurrentCellHex), nameof(CurrentCellTitle), nameof(HasCurrentCell))]
    private CellViewModel? _currentCell;
    [ObservableProperty] private string _currentColumnName = string.Empty;
    [ObservableProperty] private TableRowViewModel? _currentRow;

    /// <summary>Righe selezionate nella griglia (impostate dalla vista).</summary>
    public IReadOnlyList<TableRowViewModel> SelectedRows { get; set; } = [];

    public bool HasCurrentCell => CurrentCell is not null;
    public string CurrentCellTitle => CurrentCell is null ? "Nessun campo selezionato" : $"{CurrentColumnName}{(CurrentCell.IsNull ? " · NULL" : string.Empty)}";
    public string CurrentCellText
    {
        get => CurrentCell?.EditText ?? string.Empty;
        set
        {
            if (CurrentCell is null || !CanEdit || CurrentCell.EditText == value) return;
            CurrentCell.EditText = value;
            OnPropertyChanged(nameof(CurrentCellHex));
        }
    }
    public string CurrentCellHex => HexDump(CurrentCell?.Value);
    public bool IsTextMode => !IsHexMode;
    public string FieldModeLabel => FieldModeTitle;
    public bool HasFilters => Filters.Count > 0;
    public bool HasSorts => Sorts.Count > 0;
    public string TransactionLabel => IsTransaction ? "Transazione attiva" : "Inizia transazione";

    partial void OnIsHexModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTextMode));
        OnPropertyChanged(nameof(FieldModeLabel));
    }
    partial void OnIsTransactionChanged(bool value) => OnPropertyChanged(nameof(TransactionLabel));
    partial void OnCurrentRowChanged(TableRowViewModel? value) => UpdateRecordLabel();
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnHasMoreChanged(bool value) => NotifyCommands();

    // ───────────── Caricamento ─────────────

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (HasPendingChanges && !await _dialogs.ConfirmAsync("Modifiche non salvate", "Ci sono modifiche non applicate in questa pagina. Scartarle e ricaricare?", "Scarta"))
            return;
        await RunAsync(async (connection, token) =>
        {
            if (_metadata.Count == 0)
            {
                _metadata = await _provider.GetColumnsAsync(connection, Database, Schema, TableName, token);
                UpdateEditability();
                await LoadReferencesAsync(connection, token);
            }
            var page = await _provider.QueryPagedAsync(connection, Schema, TableName, _pageIndex, PageSize, token, _appliedWhere, _appliedOrderBy);
            TotalRows = await _provider.CountRowsAsync(connection, Schema, TableName, _appliedWhere, token);
            var byName = _metadata.ToDictionary(column => column.Name, StringComparer.Ordinal);
            var columns = page.Columns.Select(column => byName.TryGetValue(column.Name, out var meta) ? meta : column).ToArray();
            if (!Columns.Select(column => column.Name).SequenceEqual(columns.Select(column => column.Name)))
            {
                Columns.Clear();
                foreach (var column in columns) Columns.Add(column);
                OnPropertyChanged(nameof(ColumnNames));
            }
            Rows = new ObservableCollection<TableRowViewModel>(page.Rows.Select(row => TrackRow(new TableRowViewModel(row))));
            HasMore = page.HasMore;
            HasPendingChanges = false;
            CurrentCell = null;
            PageLabel = $"{_pageIndex + 1}";
            SqlPreview = BuildSelectSql(withPaging: true);
            UpdateRecordLabel();
            StatusMessage = $"{Rows.Count:N0} righe caricate";
        }, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousAsync()
    {
        _pageIndex--;
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task FirstPageAsync()
    {
        _pageIndex = 0;
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        _pageIndex++;
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task LastPageAsync()
    {
        if (TotalRows <= 0) return;
        if (PageSize == 0) return;
        _pageIndex = (int)Math.Max(0, (TotalRows - 1) / PageSize);
        await LoadAsync();
    }

    private bool CanGoPrevious() => _pageIndex > 0 && !IsBusy;
    private bool CanGoNext() => HasMore && !IsBusy;

    [RelayCommand]
    private void Stop() => _running?.Cancel();

    [RelayCommand]
    private void ToggleLimitPanel() => IsLimitPanelOpen = !IsLimitPanelOpen;

    /// <summary>Applica il limite scelto con l'ingranaggio e ricarica dalla prima pagina.</summary>
    [RelayCommand]
    private async Task ApplyLimitAsync()
    {
        s_defaultLimit = LimitRecords;
        s_defaultPageSize = (int)Math.Clamp(PageSizeSetting ?? 1000, 1, 1_000_000);
        PageSizeSetting = s_defaultPageSize;
        IsLimitPanelOpen = false;
        _pageIndex = 0;
        OnPropertyChanged(nameof(PageSize));
        if (!LimitRecords && TotalRows > 200_000 &&
            !await _dialogs.ConfirmAsync("Nessun limite", $"La tabella contiene {TotalRows:N0} record: caricarli tutti può richiedere tempo e memoria. Continuare?", "Carica tutto"))
        {
            LimitRecords = true;
            return;
        }
        await LoadAsync();
    }

    partial void OnLimitRecordsChanged(bool value) => OnPropertyChanged(nameof(PageSize));

    // ───────────── Filtro ─────────────

    [RelayCommand]
    private void ToggleFilter() => IsFilterOpen = !IsFilterOpen;

    [RelayCommand]
    private void AddFilter(FilterConditionViewModel? after)
    {
        var column = CurrentColumnName.Length > 0 ? CurrentColumnName : null;
        var condition = new FilterConditionViewModel(ColumnNames, column);
        var index = after is null ? Filters.Count : Filters.IndexOf(after) + 1;
        Filters.Insert(index, condition);
        SelectFilter(condition);
    }

    /// <summary>Secondo pulsante della riga: aggiunge una condizione collegata in OR.</summary>
    [RelayCommand]
    private void AddFilterOr(FilterConditionViewModel? after)
    {
        AddFilter(after);
        var added = Filters.FirstOrDefault(filter => filter.IsSelected);
        if (added is not null && !added.IsFirst) added.Connector = "OR";
    }

    [RelayCommand]
    private void RemoveFilter(FilterConditionViewModel? condition)
    {
        if (condition is null) return;
        Filters.Remove(condition);
    }

    [RelayCommand]
    private void SelectFilter(FilterConditionViewModel? condition)
    {
        foreach (var filter in Filters) filter.IsSelected = filter == condition;
    }

    [RelayCommand]
    private void MoveFilterUp() => MoveSelectedFilter(-1);

    [RelayCommand]
    private void MoveFilterDown() => MoveSelectedFilter(1);

    private void MoveSelectedFilter(int delta)
    {
        var selected = Filters.FirstOrDefault(filter => filter.IsSelected);
        if (selected is null) return;
        var index = Filters.IndexOf(selected);
        var target = index + delta;
        if (target < 0 || target >= Filters.Count) return;
        Filters.Move(index, target);
    }

    public bool CanApplyFilter => Filters.Count > 0 || IsFilterActive;

    [RelayCommand]
    private async Task ApplyFilterAsync()
    {
        _appliedWhere = BuildWhere();
        IsFilterActive = _appliedWhere is not null;
        _pageIndex = 0;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ClearFilterAsync()
    {
        Filters.Clear();
        _appliedWhere = null;
        IsFilterActive = false;
        _pageIndex = 0;
        await LoadAsync();
    }

    /// <summary>Filtro rapido dal menu della cella: colonna = valore corrente.</summary>
    [RelayCommand]
    private async Task FilterByCurrentValueAsync()
    {
        if (CurrentCell is null || CurrentColumnName.Length == 0) return;
        var condition = new FilterConditionViewModel(ColumnNames, CurrentColumnName);
        if (CurrentCell.IsNull) condition.Operator = FilterOperator.All.First(op => op.Kind == "null");
        else condition.Value = CurrentCell.Text;
        Filters.Add(condition);
        IsFilterOpen = true;
        await ApplyFilterAsync();
    }

    private string? BuildWhere()
    {
        var parts = new StringBuilder();
        foreach (var filter in Filters)
        {
            var sql = filter.BuildSql(_provider);
            if (sql is null) continue;
            if (parts.Length > 0) parts.Append(' ').Append(filter.Connector).Append(' ');
            parts.Append('(').Append(sql).Append(')');
        }
        return parts.Length == 0 ? null : parts.ToString();
    }

    private void RenumberFilters()
    {
        for (var index = 0; index < Filters.Count; index++) Filters[index].IsFirst = index == 0;
        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(CanApplyFilter));
    }

    partial void OnIsFilterActiveChanged(bool value) => OnPropertyChanged(nameof(CanApplyFilter));

    // ───────────── Ordinamento ─────────────

    [RelayCommand]
    private void ToggleSort()
    {
        IsSortOpen = !IsSortOpen;
        if (IsSortOpen && Sorts.Count == 0) AddSort(null);
    }

    [RelayCommand]
    private void AddSort(SortConditionViewModel? after)
    {
        var index = after is null ? Sorts.Count : Sorts.IndexOf(after) + 1;
        Sorts.Insert(index, new SortConditionViewModel(ColumnNames, CurrentColumnName.Length > 0 ? CurrentColumnName : null));
        OnPropertyChanged(nameof(HasSorts));
    }

    [RelayCommand]
    private void RemoveSort(SortConditionViewModel? sort)
    {
        if (sort is null) return;
        Sorts.Remove(sort);
        OnPropertyChanged(nameof(HasSorts));
    }

    [RelayCommand]
    private async Task ApplySortAsync()
    {
        var parts = Sorts.Select(sort => sort.BuildSql(_provider)).OfType<string>().ToArray();
        _appliedOrderBy = parts.Length == 0 ? null : string.Join(", ", parts);
        IsSortActive = _appliedOrderBy is not null;
        _pageIndex = 0;
        await LoadAsync();
    }

    /// <summary>Clic sull'intestazione: ordina per quella colonna (alterna crescente/decrescente).</summary>
    public async Task SortByColumnAsync(string column)
    {
        var existing = Sorts.Count == 1 && Sorts[0].Column == column ? Sorts[0] : null;
        var descending = existing is not null && !existing.IsDescending;
        Sorts.Clear();
        Sorts.Add(new SortConditionViewModel(ColumnNames, column, descending));
        OnPropertyChanged(nameof(HasSorts));
        await ApplySortAsync();
    }

    /// <summary>Menu della freccina: ordina crescente/decrescente per la colonna, oppure (null) rimuove il suo ordinamento.</summary>
    public async Task SortColumnAsync(string column, bool? descending)
    {
        if (descending is null)
        {
            foreach (var sort in Sorts.Where(s => s.Column == column).ToArray()) Sorts.Remove(sort);
        }
        else
        {
            Sorts.Clear();
            Sorts.Add(new SortConditionViewModel(ColumnNames, column, descending.Value));
        }
        OnPropertyChanged(nameof(HasSorts));
        await ApplySortAsync();
    }

    public string? SortGlyphFor(string column)
    {
        var sort = Sorts.FirstOrDefault(item => item.IsEnabled && item.Column == column);
        return IsSortActive && sort is not null ? sort.IsDescending ? "▼" : "▲" : null;
    }

    public bool IsFiltered(string column) => IsFilterActive && Filters.Any(filter => filter.IsEnabled && !filter.IsCustom && filter.Column == column);

    // ───────────── Editor di campo ─────────────

    [RelayCommand]
    private void ToggleFieldPanel() => IsFieldPanelOpen = !IsFieldPanelOpen;

    [RelayCommand]
    private void SetFieldMode(string? mode)
    {
        FieldMode = mode is "hex" or "json" or "image" ? mode : "text";
        IsFieldPanelOpen = true;
    }

    public void SetCurrent(TableRowViewModel? row, int columnIndex)
    {
        CurrentRow = row;
        CurrentColumnName = columnIndex >= 0 && columnIndex < Columns.Count ? Columns[columnIndex].Name : string.Empty;
        CurrentCell = row?.CellAt(columnIndex);
    }

    // ───────────── Modifica dati ─────────────

    [RelayCommand]
    private void AddRow()
    {
        if (!EnsureEditable()) return;
        var row = TrackRow(new TableRowViewModel(Enumerable.Repeat<object?>(null, Columns.Count), isNew: true));
        Rows.Add(row);
        CurrentRow = row;
        HasPendingChanges = true;
        StatusMessage = "Nuova riga: compila i campi e premi ✓ per salvarla.";
    }

    [RelayCommand]
    private void DeleteRows()
    {
        if (!EnsureEditable()) return;
        var targets = SelectedRows.Count > 0 ? SelectedRows.ToArray() : CurrentRow is null ? [] : [CurrentRow];
        foreach (var row in targets)
        {
            if (row.IsNew) Rows.Remove(row);
            else row.IsDeleted = true;
        }
        RefreshPending();
        if (targets.Length > 0) StatusMessage = $"{targets.Length} righe contrassegnate per l’eliminazione: premi ✓ per confermare.";
    }

    [RelayCommand]
    private void SetNull()
    {
        if (!EnsureEditable() || CurrentCell is null) return;
        CurrentCell.SetNull();
        OnPropertyChanged(nameof(CurrentCellText));
        OnPropertyChanged(nameof(CurrentCellTitle));
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        foreach (var row in Rows.Where(row => row.IsNew).ToArray()) Rows.Remove(row);
        foreach (var row in Rows) row.Revert();
        RefreshPending();
        StatusMessage = "Modifiche annullate.";
    }

    [RelayCommand]
    private async Task ApplyChangesAsync()
    {
        if (!HasPendingChanges) return;
        var statements = BuildChangeStatements();
        if (statements.Count == 0)
        {
            RefreshPending();
            return;
        }
        if (!await Services.SqlGuard.ConfirmProductionAsync(_dialogs, _profile, $"Stai per salvare {statements.Count} modifiche ai dati di «{TableName}».")) return;
        var ok = false;
        await RunAsync(async (connection, token) =>
        {
            var begin = _provider.UsesMySqlScriptSyntax ? "START TRANSACTION" : "BEGIN";
            if (!IsTransaction) await _provider.ExecuteAsync(connection, begin, 0, token);
            try
            {
                foreach (var statement in statements) await _provider.ExecuteAsync(connection, statement, 0, token);
                if (!IsTransaction) await _provider.ExecuteAsync(connection, "COMMIT", 0, token);
            }
            catch
            {
                if (!IsTransaction) await _provider.ExecuteAsync(connection, "ROLLBACK", 0, CancellationToken.None);
                throw;
            }
            ok = true;
        }, CancellationToken.None, reportErrors: true);
        if (!ok) return;
        foreach (var row in Rows.Where(row => row.IsDeleted).ToArray()) Rows.Remove(row);
        foreach (var row in Rows) row.AcceptChanges();
        HasPendingChanges = false;
        StatusMessage = IsTransaction
            ? $"{statements.Count} modifiche applicate nella transazione (non ancora confermate)."
            : $"{statements.Count} modifiche salvate.";
        await LoadAsync();
    }

    private List<string> BuildChangeStatements()
    {
        var statements = new List<string>();
        var target = $"{_provider.QuoteIdentifier(Schema)}.{_provider.QuoteIdentifier(TableName)}";
        var keyIndexes = KeyIndexes();
        foreach (var row in Rows.Where(row => row.HasChanges))
        {
            if (row.IsDeleted)
            {
                if (!row.IsNew) statements.Add($"DELETE FROM {target} WHERE {KeyPredicate(row, keyIndexes)}");
                continue;
            }
            if (row.IsNew)
            {
                var assigned = row.Cells.Select((cell, index) => (cell, index)).Where(pair => pair.cell.IsModified).ToArray();
                statements.Add(assigned.Length == 0
                    ? _provider.UsesMySqlScriptSyntax ? $"INSERT INTO {target} () VALUES ()" : $"INSERT INTO {target} DEFAULT VALUES"
                    : $"INSERT INTO {target} ({string.Join(", ", assigned.Select(pair => _provider.QuoteIdentifier(Columns[pair.index].Name)))}) VALUES ({string.Join(", ", assigned.Select(pair => _provider.FormatLiteral(pair.cell.Value)))})");
                continue;
            }
            var changes = row.Cells.Select((cell, index) => (cell, index)).Where(pair => pair.cell.IsModified)
                .Select(pair => $"{_provider.QuoteIdentifier(Columns[pair.index].Name)} = {_provider.FormatLiteral(pair.cell.Value)}");
            statements.Add($"UPDATE {target} SET {string.Join(", ", changes)} WHERE {KeyPredicate(row, keyIndexes)}");
        }
        return statements;
    }

    private int[] KeyIndexes() => Columns.Select((column, index) => (column, index)).Where(pair => pair.column.IsPrimaryKey).Select(pair => pair.index).ToArray();

    private string KeyPredicate(TableRowViewModel row, int[] keyIndexes) => string.Join(" AND ", keyIndexes.Select(index =>
    {
        var original = row.Cells[index].OriginalValue;
        var column = _provider.QuoteIdentifier(Columns[index].Name);
        return original is null ? $"{column} IS NULL" : $"{column} = {_provider.FormatLiteral(original)}";
    }));

    private bool EnsureEditable()
    {
        if (CanEdit) return true;
        StatusMessage = EditHint;
        return false;
    }

    private void UpdateEditability()
    {
        var hasKey = _metadata.Any(column => column.IsPrimaryKey);
        CanEdit = !IsView && hasKey;
        EditHint = IsView ? "Le viste sono in sola lettura." : hasKey ? string.Empty : "La tabella non ha una chiave primaria: modifica disattivata.";
    }

    private TableRowViewModel TrackRow(TableRowViewModel row)
    {
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TableRowViewModel.State)) RefreshPending();
        };
        return row;
    }

    private void RefreshPending() => HasPendingChanges = Rows.Any(row => row.HasChanges);

    // ───────────── Transazioni ─────────────

    [RelayCommand]
    private async Task BeginTransactionAsync()
    {
        if (IsTransaction) return;
        try
        {
            _transaction = await _databaseAccess.OpenAsync(_profile, Database, CancellationToken.None);
            await _provider.ExecuteAsync(_transaction, _provider.UsesMySqlScriptSyntax ? "START TRANSACTION" : "BEGIN", 0, CancellationToken.None);
            IsTransaction = true;
            StatusMessage = "Transazione avviata: le modifiche resteranno provvisorie fino a «Commit».";
        }
        catch (Exception exception)
        {
            await DisposeTransactionAsync();
            await _dialogs.ShowMessageAsync("Impossibile avviare la transazione", exception.Message, isError: true);
        }
    }

    [RelayCommand]
    private async Task CommitAsync() => await EndTransactionAsync("COMMIT", "Transazione confermata.");

    [RelayCommand]
    private async Task RollbackAsync()
    {
        HasPendingChanges = false;
        await EndTransactionAsync("ROLLBACK", "Transazione annullata: modifiche scartate.");
        await LoadAsync();
    }

    private async Task EndTransactionAsync(string command, string message)
    {
        if (_transaction is null) return;
        try
        {
            if (command == "COMMIT" && HasPendingChanges) await ApplyChangesAsync();
            await _provider.ExecuteAsync(_transaction, command, 0, CancellationToken.None);
            StatusMessage = message;
        }
        catch (Exception exception) { await _dialogs.ShowMessageAsync($"{command} non riuscito", exception.Message, isError: true); }
        finally { await DisposeTransactionAsync(); }
    }

    private async Task DisposeTransactionAsync()
    {
        if (_transaction is not null) await _transaction.DisposeAsync();
        _transaction = null;
        IsTransaction = false;
    }

    public override async ValueTask CloseAsync()
    {
        _running?.Cancel();
        if (_transaction is null) return;
        try { await _provider.ExecuteAsync(_transaction, "ROLLBACK", 0, CancellationToken.None); }
        catch { /* la connessione viene comunque chiusa */ }
        await DisposeTransactionAsync();
    }

    // ───────────── Import / export ─────────────

    [RelayCommand]
    private async Task ExportAsync()
    {
        var wizard = new ExportWizardViewModel(_databaseAccess, _dialogs, _profile, Database, Schema, TableName,
            Columns.ToArray(), BuildSelectSql(withPaging: false), Rows.ToArray(), SelectedRows.ToArray(), TotalRows);
        await _dialogs.ShowExportWizardAsync(wizard);
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (IsView)
        {
            StatusMessage = "Non è possibile importare dati in una vista.";
            return;
        }
        var columns = _metadata.Count > 0 ? _metadata : Columns.ToArray();
        var wizard = new ImportWizardViewModel(_databaseAccess, _dialogs, _profile, Database, Schema, TableName, columns);
        if (await _dialogs.ShowImportWizardAsync(wizard) && wizard.ImportedRows > 0) await LoadAsync();
    }

    // ───────────── Utilità ─────────────

    private string BuildSelectSql(bool withPaging)
    {
        var sql = $"SELECT * FROM {_provider.QuoteIdentifier(Schema)}.{_provider.QuoteIdentifier(TableName)}"
                  + (_appliedWhere is null ? string.Empty : $" WHERE {_appliedWhere}")
                  + (_appliedOrderBy is null ? string.Empty : $" ORDER BY {_appliedOrderBy}");
        return withPaging && PageSize > 0 ? $"{sql} LIMIT {PageSize} OFFSET {(long)_pageIndex * PageSize}" : sql;
    }

    private void UpdateRecordLabel()
    {
        var total = TotalRows < 0 ? "?" : TotalRows.ToString("N0");
        var index = CurrentRow is null ? -1 : Rows.IndexOf(CurrentRow);
        var inPage = PageSize == 0 ? string.Empty : $" nella pagina {_pageIndex + 1}";
        var totals = TotalRows >= 0 && TotalRows != Rows.Count ? $" · {total} totali" : string.Empty;
        RecordLabel = index < 0
            ? $"{Rows.Count:N0} record{inPage}{totals}"
            : $"Record {index + 1:N0} di {Rows.Count:N0}{inPage}{totals}";
        OnPropertyChanged(nameof(PageInfoLabel));
    }

    private void NotifyCommands()
    {
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        FirstPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    private async Task RunAsync(Func<DbConnection, CancellationToken, Task> action, CancellationToken cancellationToken, bool reportErrors = false)
    {
        _running?.Cancel();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = source;
        IsBusy = true;
        HasError = false;
        var timer = Stopwatch.StartNew();
        DbConnection? owned = null;
        try
        {
            var connection = _transaction ?? (owned = await _databaseAccess.OpenAsync(_profile, Database, source.Token));
            await action(connection, source.Token);
            StatusMessage += $" · {timer.Elapsed.TotalSeconds:0.000} s";
        }
        catch (OperationCanceledException) { StatusMessage = "Operazione interrotta."; }
        catch (Exception exception)
        {
            HasError = true;
            StatusMessage = $"Errore: {exception.Message}";
            if (reportErrors) await _dialogs.ShowMessageAsync("Operazione non riuscita", exception.Message, isError: true);
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync();
            IsBusy = false;
            if (ReferenceEquals(_running, source)) _running = null;
        }
    }

    private static string HexDump(object? value)
    {
        var bytes = value switch
        {
            null => [],
            byte[] raw => raw,
            _ => Encoding.UTF8.GetBytes(TableRowViewModel.Format(value))
        };
        var limit = Math.Min(bytes.Length, 64 * 1024);
        var builder = new StringBuilder();
        for (var offset = 0; offset < limit; offset += 16)
        {
            var count = Math.Min(16, limit - offset);
            builder.Append(offset.ToString("X8")).Append("  ");
            for (var i = 0; i < 16; i++) builder.Append(i < count ? bytes[offset + i].ToString("X2") + " " : "   ");
            builder.Append(" |");
            for (var i = 0; i < count; i++) builder.Append(bytes[offset + i] is >= 32 and < 127 ? (char)bytes[offset + i] : '.');
            builder.Append("|\n");
        }
        if (bytes.Length > limit) builder.Append($"… ({bytes.Length:N0} byte totali)");
        return builder.ToString();
    }
}
