using System.Collections.ObjectModel;
using System.Data.Common;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Differenza trovata dal confronto: una tabella/vista (struttura) o i dati di una tabella.</summary>
public partial class SyncItem(string name, string kind, string status, string detail, IReadOnlyList<string> statements) : ObservableObject
{
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    /// <summary>Nuova, Modificata, Da eliminare, Uguale, Errore.</summary>
    public string Status { get; } = status;
    public string Detail { get; } = detail;
    public IReadOnlyList<string> Statements { get; } = statements;
    public bool HasChanges => Statements.Count > 0;
    public string Glyph => Kind == "VIEW" ? "eye" : "table";
    public bool IsNew => Status == "Nuova";
    public bool IsChanged => Status == "Modificata";
    public bool IsDrop => Status == "Da eliminare";
    public bool IsEqual => Status == "Uguale";
    public bool IsError => Status == "Errore";
    [ObservableProperty] private bool _isSelected = statements.Count > 0;
    [ObservableProperty] private string _state = string.Empty;
}

/// <summary>
/// Sincronizzazione struttura o dati tra due database dello stesso tipo: confronta, mostra le differenze tabella per tabella,
/// genera lo script e lo esegue sulla destinazione (o lo salva su file).
/// </summary>
public partial class SyncTabViewModel : WorkspaceTabViewModel
{
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _running;

    public SyncTabViewModel(IDatabaseAccessService databaseAccess, IDialogService dialogs, IReadOnlyList<ConnectionProfile> profiles, bool data,
        ConnectionProfile? source = null, string? sourceDatabase = null)
    {
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        IsData = data;
        Profiles = profiles;
        _pendingSource = sourceDatabase;
        _sourceProfile = source ?? profiles.FirstOrDefault();
        _ = LoadDatabasesAsync(true);
        RefreshTargets();
    }

    public bool IsData { get; }
    public bool IsStructure => !IsData;
    public IReadOnlyList<ConnectionProfile> Profiles { get; }
    public ObservableCollection<ConnectionProfile> TargetProfiles { get; } = [];
    public ObservableCollection<string> SourceDatabases { get; } = [];
    public ObservableCollection<string> TargetDatabases { get; } = [];
    public ObservableCollection<SyncItem> Items { get; } = [];
    private string? _pendingSource;

    public override string TabTitle => IsData ? "Sincronizzazione dati" : "Sincronizzazione struttura";
    public override string TabGlyph => IsData ? "sync" : "layers";
    public override ConnectionProfile? TabConnection => TargetProfile;

    [ObservableProperty] private ConnectionProfile? _sourceProfile;
    [ObservableProperty] private ConnectionProfile? _targetProfile;
    [ObservableProperty] private string? _sourceDatabase;
    [ObservableProperty] private string? _targetDatabase;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Script))] private SyncItem? _selectedItem;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusMessage = "Scegli origine e destinazione, poi premi Confronta.";
    [ObservableProperty] private string _summary = string.Empty;

    // Opzioni
    [ObservableProperty] private bool _dropMissing;
    [ObservableProperty] private bool _includeViews = true;
    [ObservableProperty] private bool _doInsert = true;
    [ObservableProperty] private bool _doUpdate = true;
    [ObservableProperty] private bool _doDelete;
    [ObservableProperty] private bool _showEqual;

    public string Script => SelectedItem is { } item
        ? item.Statements.Count == 0 ? $"-- «{item.Name}»: {item.Detail}" : string.Join(";\n\n", item.Statements) + ";"
        : Items.Any(i => i.HasChanges) ? "-- Seleziona una riga per vedere il suo script." : string.Empty;

    partial void OnSourceProfileChanged(ConnectionProfile? value)
    {
        RefreshTargets();
        _ = LoadDatabasesAsync(true);
    }

    partial void OnTargetProfileChanged(ConnectionProfile? value) => _ = LoadDatabasesAsync(false);

    private void RefreshTargets()
    {
        var keep = TargetProfile;
        TargetProfiles.Clear();
        foreach (var profile in Profiles.Where(p => p.Kind == SourceProfile?.Kind)) TargetProfiles.Add(profile);
        TargetProfile = keep is not null && TargetProfiles.Contains(keep) ? keep : TargetProfiles.FirstOrDefault(p => p.Id != SourceProfile?.Id) ?? TargetProfiles.FirstOrDefault();
    }

    private async Task LoadDatabasesAsync(bool source)
    {
        var profile = source ? SourceProfile : TargetProfile;
        var list = source ? SourceDatabases : TargetDatabases;
        list.Clear();
        if (profile is null) return;
        try
        {
            foreach (var database in await _databaseAccess.ListDatabasesAsync(profile, CancellationToken.None)) list.Add(database);
            if (source)
            {
                SourceDatabase = _pendingSource is not null && list.Contains(_pendingSource) ? _pendingSource : list.FirstOrDefault();
                _pendingSource = null;
            }
            else TargetDatabase = list.FirstOrDefault(d => d != SourceDatabase || TargetProfile?.Id != SourceProfile?.Id) ?? list.FirstOrDefault();
        }
        catch (Exception exception) { StatusMessage = $"{profile.Name}: {exception.Message}"; }
    }

    [RelayCommand]
    private void Swap()
    {
        if (SourceProfile is null || TargetProfile is null) return;
        var (profile, database) = (SourceProfile, SourceDatabase);
        _pendingSource = TargetDatabase;
        SourceProfile = TargetProfile;
        TargetProfile = profile;
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => TargetDatabase = database);
        });
    }

    private static string SchemaFor(ConnectionProfile profile, string database) => profile.Kind.DefaultSchema(database);

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (IsRunning || SourceProfile is not { } source || TargetProfile is not { } target || SourceDatabase is not { } sourceDb || TargetDatabase is not { } targetDb) return;
        if (source.Id == target.Id && sourceDb == targetDb)
        {
            StatusMessage = "Origine e destinazione coincidono.";
            return;
        }
        IsRunning = true;
        Items.Clear();
        SelectedItem = null;
        Progress = 0;
        _running = new CancellationTokenSource();
        try
        {
            if (IsData) await CompareDataAsync(source, sourceDb, target, targetDb, _running.Token);
            else await CompareStructureAsync(source, sourceDb, target, targetDb, _running.Token);
            var changed = Items.Count(i => i.HasChanges);
            Summary = $"{Items.Count(i => i.IsNew)} nuove · {Items.Count(i => i.IsChanged)} modificate · {Items.Count(i => i.IsDrop)} da eliminare · {Items.Count(i => i.IsEqual)} uguali";
            StatusMessage = changed == 0 ? "Nessuna differenza: i due database sono allineati." : $"{changed} differenze trovate. Controlla lo script e premi Applica.";
            SelectedItem = Items.FirstOrDefault(i => i.HasChanges);
            OnPropertyChanged(nameof(Script));
        }
        catch (OperationCanceledException) { StatusMessage = "Confronto interrotto."; }
        catch (Exception exception) { StatusMessage = $"Confronto non riuscito: {exception.Message}"; }
        finally
        {
            IsRunning = false;
            Progress = 100;
        }
    }

    // ───────────── Struttura ─────────────

    private async Task CompareStructureAsync(ConnectionProfile source, string sourceDb, ConnectionProfile target, string targetDb, CancellationToken token)
    {
        var provider = _databaseAccess.Provider(source);
        var mysql = provider.UsesMySqlScriptSyntax;
        var sourceSchema = SchemaFor(source, sourceDb);
        var targetSchema = SchemaFor(target, targetDb);
        await using var from = await _databaseAccess.OpenAsync(source, sourceDb, token);
        await using var to = await _databaseAccess.OpenAsync(target, targetDb, token);
        var sourceObjects = await provider.ListTablesAsync(from, sourceDb, sourceSchema, token);
        var targetObjects = await provider.ListTablesAsync(to, targetDb, targetSchema, token);
        bool IsView(DatabaseObjectInfo o) => o.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase);
        var targetTables = targetObjects.Where(o => !IsView(o)).ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);
        var targetViews = targetObjects.Where(IsView).ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);
        var tables = sourceObjects.Where(o => !IsView(o)).ToArray();
        var views = IncludeViews ? sourceObjects.Where(IsView).ToArray() : [];
        var total = tables.Length + views.Length + (DropMissing ? 1 : 0);
        var done = 0;

        foreach (var table in tables)
        {
            token.ThrowIfCancellationRequested();
            StatusMessage = $"Confronto {table.Name}…";
            try
            {
                var design = await provider.GetTableDesignAsync(from, sourceDb, sourceSchema, table.Name, token);
                if (!targetTables.TryGetValue(table.Name, out var existing))
                {
                    var create = Retarget(design, null, sourceSchema, targetSchema);
                    AddItem(new SyncItem(table.Name, "TABLE", "Nuova", $"Tabella da creare ({design.Columns.Count} campi)", TableDesignSql.Build(target.Kind, targetSchema, null, create)));
                }
                else
                {
                    var current = await provider.GetTableDesignAsync(to, targetDb, targetSchema, existing.Name, token);
                    var wanted = Retarget(design, current, sourceSchema, targetSchema);
                    var statements = TableDesignSql.Build(target.Kind, targetSchema, current, wanted);
                    AddItem(statements.Count == 0
                        ? new SyncItem(table.Name, "TABLE", "Uguale", "Struttura identica", [])
                        : new SyncItem(table.Name, "TABLE", "Modificata", Describe(current, wanted), statements));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddItem(new SyncItem(table.Name, "TABLE", "Errore", exception.Message, []));
            }
            Progress = ++done * 100d / Math.Max(1, total);
        }

        foreach (var view in views)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var select = await provider.GetSchemaObjectDefinitionAsync(from, sourceDb, sourceSchema, SchemaObjectKind.View, view, token);
                if (!targetViews.TryGetValue(view.Name, out var existing))
                    AddItem(new SyncItem(view.Name, "VIEW", "Nuova", "Vista da creare", SchemaObjectSql.BuildViewSave(target.Kind, targetSchema, null, view.Name, Requalify(select, sourceSchema, targetSchema, mysql)).Statements));
                else
                {
                    var current = await provider.GetSchemaObjectDefinitionAsync(to, targetDb, targetSchema, SchemaObjectKind.View, existing, token);
                    var wanted = Requalify(select, sourceSchema, targetSchema, mysql);
                    AddItem(Normalize(current) == Normalize(wanted)
                        ? new SyncItem(view.Name, "VIEW", "Uguale", "Definizione identica", [])
                        : new SyncItem(view.Name, "VIEW", "Modificata", "Definizione diversa", SchemaObjectSql.BuildViewSave(target.Kind, targetSchema, existing.Name, view.Name, wanted).Statements));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddItem(new SyncItem(view.Name, "VIEW", "Errore", exception.Message, []));
            }
            Progress = ++done * 100d / Math.Max(1, total);
        }

        if (DropMissing)
        {
            var sourceNames = sourceObjects.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var extra in targetObjects.Where(o => !sourceNames.Contains(o.Name) && (IncludeViews || !IsView(o))))
            {
                var name = mysql ? provider.QuoteIdentifier(extra.Name) : $"{provider.QuoteIdentifier(targetSchema)}.{provider.QuoteIdentifier(extra.Name)}";
                AddItem(new SyncItem(extra.Name, IsView(extra) ? "VIEW" : "TABLE", "Da eliminare", "Non esiste nell'origine",
                    [$"DROP {(IsView(extra) ? "VIEW" : "TABLE")} {name}"]));
            }
        }
    }

    /// <summary>
    /// Prepara la struttura dell'origine per il confronto con la destinazione: gli elementi con lo stesso nome diventano «esistenti»
    /// (OriginalName = nome nella destinazione), gli altri nuovi; i riferimenti allo schema di origine passano a quello di destinazione.
    /// </summary>
    private static TableDesign Retarget(TableDesign design, TableDesign? current, string sourceSchema, string targetSchema)
    {
        string? Match<T>(IEnumerable<T>? items, Func<T, string> name, string wanted) =>
            items?.Select(name).FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
        return design with
        {
            OriginalName = current?.Name,
            Name = current?.Name ?? design.Name,
            Columns = design.Columns.Select(c => c with { OriginalName = Match(current?.Columns, x => x.Name, c.Name) }).ToArray(),
            Indexes = design.Indexes.Select(i => i with { OriginalName = Match(current?.Indexes, x => x.Name, i.Name), IsConstraint = current?.Indexes.FirstOrDefault(x => x.Name == i.Name)?.IsConstraint ?? i.IsConstraint }).ToArray(),
            ForeignKeys = design.ForeignKeys.Select(k => k with
            {
                OriginalName = Match(current?.ForeignKeys, x => x.Name, k.Name),
                ReferencedSchema = string.Equals(k.ReferencedSchema, sourceSchema, StringComparison.OrdinalIgnoreCase) ? targetSchema : k.ReferencedSchema,
            }).ToArray(),
            Triggers = design.Triggers.Select(t => t with { OriginalName = Match(current?.Triggers, x => x.Name, t.Name) }).ToArray(),
            // L'AUTO_INCREMENT corrente resta quello della destinazione.
            Options = design.Options with { AutoIncrement = current?.Options.AutoIncrement },
            PrimaryKeyName = current?.PrimaryKeyName ?? design.PrimaryKeyName,
        };
    }

    private static string Describe(TableDesign current, TableDesign wanted)
    {
        var parts = new List<string>();
        var added = wanted.Columns.Count(c => c.OriginalName is null);
        var removed = current.Columns.Count(c => wanted.Columns.All(w => w.OriginalName != c.Name));
        var changed = wanted.Columns.Count(c => c.OriginalName is not null && current.Columns.FirstOrDefault(x => x.Name == c.OriginalName) is { } old && (old with { OriginalName = null }) != (c with { OriginalName = null }));
        if (added > 0) parts.Add($"+{added} campi");
        if (removed > 0) parts.Add($"−{removed} campi");
        if (changed > 0) parts.Add($"{changed} campi modificati");
        var indexes = wanted.Indexes.Count(i => i.OriginalName is null) + current.Indexes.Count(i => wanted.Indexes.All(w => w.OriginalName != i.Name));
        if (indexes > 0) parts.Add("indici");
        if (wanted.ForeignKeys.Count != current.ForeignKeys.Count || wanted.ForeignKeys.Any(k => k.OriginalName is null)) parts.Add("chiavi esterne");
        if (wanted.Triggers.Any(t => t.OriginalName is null) || wanted.Triggers.Count != current.Triggers.Count) parts.Add("trigger");
        return parts.Count == 0 ? "Opzioni o dettagli diversi" : string.Join(", ", parts);
    }

    private static string Requalify(string select, string sourceSchema, string targetSchema, bool mysql) =>
        sourceSchema == targetSchema ? select : select.Replace(mysql ? $"`{sourceSchema}`." : $"{sourceSchema}.", mysql ? $"`{targetSchema}`." : $"{targetSchema}.", StringComparison.Ordinal);

    private static string Normalize(string sql) => string.Join(' ', sql.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd(';').ToLowerInvariant();

    // ───────────── Dati ─────────────

    private async Task CompareDataAsync(ConnectionProfile source, string sourceDb, ConnectionProfile target, string targetDb, CancellationToken token)
    {
        var provider = _databaseAccess.Provider(source);
        var mysql = provider.UsesMySqlScriptSyntax;
        var sourceSchema = SchemaFor(source, sourceDb);
        var targetSchema = SchemaFor(target, targetDb);
        await using var from = await _databaseAccess.OpenAsync(source, sourceDb, token);
        await using var to = await _databaseAccess.OpenAsync(target, targetDb, token);
        var sourceTables = (await provider.ListTablesAsync(from, sourceDb, sourceSchema, token)).Where(o => !o.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase)).ToArray();
        var targetTables = (await provider.ListTablesAsync(to, targetDb, targetSchema, token)).Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var table in sourceTables)
        {
            token.ThrowIfCancellationRequested();
            StatusMessage = $"Confronto dati di {table.Name}…";
            try
            {
                if (!targetTables.Contains(table.Name))
                {
                    AddItem(new SyncItem(table.Name, "TABLE", "Errore", "La tabella non esiste nella destinazione: usa prima la sincronizzazione struttura", []));
                    continue;
                }
                AddItem(await CompareTableDataAsync(provider, from, sourceDb, sourceSchema, to, targetDb, targetSchema, table.Name, mysql, token));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddItem(new SyncItem(table.Name, "TABLE", "Errore", exception.Message, []));
            }
            finally { Progress = ++done * 100d / Math.Max(1, sourceTables.Length); }
        }
    }

    private async Task<SyncItem> CompareTableDataAsync(IDialectProvider provider, DbConnection from, string sourceDb, string sourceSchema, DbConnection to, string targetDb, string targetSchema,
        string table, bool mysql, CancellationToken token)
    {
        var sourceColumns = await provider.GetColumnsAsync(from, sourceDb, sourceSchema, table, token);
        var targetColumns = await provider.GetColumnsAsync(to, targetDb, targetSchema, table, token);
        var key = sourceColumns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToArray();
        if (key.Length == 0) return new SyncItem(table, "TABLE", "Errore", "Senza chiave primaria: impossibile confrontare le righe", []);
        var common = sourceColumns.Select(c => c.Name).Where(n => targetColumns.Any(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase))).ToArray();
        int[] Indexes(IReadOnlyList<ColumnInfo> columns, IEnumerable<string> names) => names.Select(n => columns.ToList().FindIndex(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).ToArray();
        var targetKey = Indexes(targetColumns, key);
        var targetCommon = Indexes(targetColumns, common);
        var sourceKey = Indexes(sourceColumns, key);
        var sourceCommon = Indexes(sourceColumns, common);
        string Literal(object? value) => provider.FormatLiteral(value);
        string KeyOf(IReadOnlyList<object?> row, int[] positions) => string.Join("\u001f", positions.Select(p => Literal(row[p])));

        // Destinazione in memoria: chiave → valori delle colonne comuni.
        var existing = new Dictionary<string, string[]>();
        await foreach (var row in provider.StreamRowsAsync(to, targetSchema, table, token))
            existing[KeyOf(row, targetKey)] = targetCommon.Select(p => Literal(row[p])).ToArray();

        var name = mysql ? provider.QuoteIdentifier(table) : $"{provider.QuoteIdentifier(targetSchema)}.{provider.QuoteIdentifier(table)}";
        var statements = new List<string>();
        var inserts = new List<IReadOnlyList<object?>>();
        int insertCount = 0, updateCount = 0, deleteCount = 0;
        var seen = new HashSet<string>();
        string Where(IReadOnlyList<object?> row) => string.Join(" AND ", key.Select((k, i) => $"{provider.QuoteIdentifier(k)} = {Literal(row[sourceKey[i]])}"));
        await foreach (var row in provider.StreamRowsAsync(from, sourceSchema, table, token))
        {
            var k = KeyOf(row, sourceKey);
            seen.Add(k);
            if (!existing.TryGetValue(k, out var current))
            {
                insertCount++;
                if (DoInsert)
                {
                    inserts.Add(sourceCommon.Select(p => row[p]).ToArray());
                    if (inserts.Count == 200) { statements.Add(provider.BuildInsert(targetSchema, table, common, inserts).TrimEnd().TrimEnd(';')); inserts.Clear(); }
                }
                continue;
            }
            var changes = new List<string>();
            for (var i = 0; i < common.Length; i++)
            {
                if (key.Contains(common[i], StringComparer.OrdinalIgnoreCase)) continue;
                var value = Literal(row[sourceCommon[i]]);
                if (value != current[i]) changes.Add($"{provider.QuoteIdentifier(common[i])} = {value}");
            }
            if (changes.Count == 0) continue;
            updateCount++;
            if (DoUpdate) statements.Add($"UPDATE {name} SET {string.Join(", ", changes)} WHERE {Where(row)}");
        }
        if (inserts.Count > 0) statements.Add(provider.BuildInsert(targetSchema, table, common, inserts).TrimEnd().TrimEnd(';'));
        foreach (var (k, values) in existing)
        {
            if (seen.Contains(k)) continue;
            deleteCount++;
            if (!DoDelete) continue;
            var keyValues = k.Split('\u001f');
            statements.Add($"DELETE FROM {name} WHERE {string.Join(" AND ", key.Select((col, i) => $"{provider.QuoteIdentifier(col)} = {keyValues[i]}"))}");
        }
        var detail = $"{insertCount:N0} da inserire · {updateCount:N0} da aggiornare · {deleteCount:N0} solo nella destinazione";
        var status = insertCount + updateCount + deleteCount == 0 ? "Uguale" : "Modificata";
        return new SyncItem(table, "TABLE", status, status == "Uguale" ? $"Dati identici ({seen.Count:N0} righe)" : detail, statements);
    }

    private void AddItem(SyncItem item)
    {
        if (!ShowEqual && item.IsEqual) return;
        Items.Add(item);
    }

    // ───────────── Applica / salva ─────────────

    [RelayCommand]
    private void SelectAll(string? mode)
    {
        foreach (var item in Items.Where(i => i.HasChanges)) item.IsSelected = mode != "none";
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsRunning || TargetProfile is not { } target || TargetDatabase is not { } targetDb) return;
        var selected = Items.Where(i => i.IsSelected && i.HasChanges).ToArray();
        if (selected.Length == 0)
        {
            StatusMessage = "Seleziona almeno una differenza da applicare.";
            return;
        }
        var count = selected.Sum(i => i.Statements.Count);
        if (!await _dialogs.ConfirmAsync(TabTitle, $"Applicare {selected.Length} differenze ({count:N0} istruzioni) a «{targetDb}» su «{target.Name}»?", "Applica")) return;
        if (!await SqlGuard.ConfirmProductionAsync(_dialogs, target, $"Stai per applicare {count:N0} istruzioni a «{targetDb}».")) return;
        IsRunning = true;
        Progress = 0;
        var errors = 0;
        var mysql = _databaseAccess.Provider(target).UsesMySqlScriptSyntax;
        try
        {
            for (var i = 0; i < selected.Length; i++)
            {
                var item = selected[i];
                item.State = "in corso…";
                try
                {
                    var list = IsData && mysql ? new[] { "SET FOREIGN_KEY_CHECKS = 0" }.Concat(item.Statements).ToArray() : item.Statements;
                    await _databaseAccess.ExecuteStatementsAsync(target, targetDb, list, CancellationToken.None, atomic: IsData || !mysql);
                    item.State = "✓ applicata";
                }
                catch (Exception exception)
                {
                    errors++;
                    item.State = "errore: " + (exception.InnerException?.Message ?? exception.Message);
                }
                Progress = (i + 1) * 100d / selected.Length;
            }
            StatusMessage = errors == 0 ? $"Applicate {selected.Length} differenze a «{targetDb}». Premi Confronta per verificare." : $"{selected.Length - errors} applicate, {errors} con errori (vedi colonna Stato).";
        }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private async Task SaveScriptAsync()
    {
        var selected = Items.Where(i => i.IsSelected && i.HasChanges).ToArray();
        if (selected.Length == 0) return;
        var path = await _dialogs.PickSaveFileAsync("Salva script", $"sync_{TargetDatabase}.sql", "sql", new FileFilter("Script SQL", "*.sql"));
        if (path is null) return;
        var text = new StringBuilder($"-- Navifox · {TabTitle}\n-- Origine: {SourceProfile?.Name} / {SourceDatabase}\n-- Destinazione: {TargetProfile?.Name} / {TargetDatabase}\n-- {DateTime.Now:yyyy-MM-dd HH:mm}\n\n");
        foreach (var item in selected)
        {
            text.Append($"-- {item.Name}: {item.Detail}\n");
            foreach (var statement in item.Statements) text.Append(statement).Append(";\n");
            text.Append('\n');
        }
        await File.WriteAllTextAsync(path, text.ToString());
        StatusMessage = $"Script salvato in {Path.GetFileName(path)}.";
    }

    [RelayCommand]
    private void Stop() => _running?.Cancel();
}
