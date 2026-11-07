using System.Text;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public sealed record TableOperation(TreeNodeViewModel Node, string Kind);

/// <summary>Azioni del menu contestuale di una tabella (Apri, Elimina, Svuota, Tronca, Duplica, Manutenzione, Rinomina…).</summary>
public partial class MainWindowViewModel
{
    private string TableName(TreeNodeViewModel node)
    {
        var provider = _databaseAccess.Provider(node.Profile!);
        return provider.UsesMySqlScriptSyntax
            ? provider.QuoteIdentifier(node.Name)
            : $"{provider.QuoteIdentifier(node.Schema!)}.{provider.QuoteIdentifier(node.Name)}";
    }

    private bool IsViewNode(TreeNodeViewModel node) => node.Category == "Viste";

    private IReadOnlyList<ContextAction> TableContextActions(TreeNodeViewModel node)
    {
        var view = IsViewNode(node);
        var what = view ? "vista" : "tabella";
        var mysql = node.Profile?.Kind == DatabaseKind.MySql;
        var kind = node.Profile?.Kind ?? DatabaseKind.MySql;
        return
        [
            new($"Apri {what}", OpenNodeCommand, node, view ? "eye" : "table"),
            new($"Progetta {what}", view ? OpenSchemaObjectCommand : DesignTableCommand, node, "pencil"),
            new("Nuova tabella", NewTableCommand, node.CategoryNode, "plus"),
            new($"Elimina {what}", TableOperationCommand, new TableOperation(node, "drop"), "trash"),
            new("Svuota tabella", TableOperationCommand, new TableOperation(node, "empty"), "x", !view),
            new("Tronca tabella", TableOperationCommand, new TableOperation(node, "truncate"), "x", !view),
            new("Duplica tabella", Glyph: "copy", IsEnabled: !view, Children:
            [
                new("Struttura e dati", TableOperationCommand, new TableOperation(node, "duplicate-data")),
                new("Solo struttura", TableOperationCommand, new TableOperation(node, "duplicate-structure")),
            ]),
            new("Imposta privilegi…", OpenUsersCommand, node, "users", kind is DatabaseKind.MySql or DatabaseKind.PostgreSql),
            ContextAction.Separator,
            new("Import Wizard…", TableOperationCommand, new TableOperation(node, "import"), "import", !view),
            new("Export Wizard…", TableOperationCommand, new TableOperation(node, "export"), "export"),
            ContextAction.Separator,
            new("Dump file SQL", Glyph: "export", IsEnabled: !view, Children:
            [
                new("Struttura e dati…", TableOperationCommand, new TableOperation(node, "dump-data")),
                new("Solo struttura…", TableOperationCommand, new TableOperation(node, "dump-structure")),
            ]),
            new("Stampa tabella…", PrintCommand, node, "printer", !view),
            new("Manutenzione", Glyph: "sliders", IsEnabled: !view, Children: kind == DatabaseKind.SqlServer
                ?
                [
                    new("Aggiorna statistiche", TableOperationCommand, new TableOperation(node, "analyze")),
                    new("Controlla tabella (DBCC CHECKTABLE)", TableOperationCommand, new TableOperation(node, "check")),
                    new("Riorganizza indici", TableOperationCommand, new TableOperation(node, "optimize")),
                    new("Ricostruisci indici", TableOperationCommand, new TableOperation(node, "reindex")),
                ]
                : kind == DatabaseKind.Sqlite
                ?
                [
                    new("Analizza (ANALYZE)", TableOperationCommand, new TableOperation(node, "analyze")),
                    new("Controlla integrità", TableOperationCommand, new TableOperation(node, "check")),
                    new("Ricostruisci indici (REINDEX)", TableOperationCommand, new TableOperation(node, "reindex")),
                    new("Compatta il database (VACUUM)", TableOperationCommand, new TableOperation(node, "vacuum")),
                ]
                : mysql
                ?
                [
                    new("Analizza tabella", TableOperationCommand, new TableOperation(node, "analyze")),
                    new("Controlla tabella", TableOperationCommand, new TableOperation(node, "check")),
                    new("Ottimizza tabella", TableOperationCommand, new TableOperation(node, "optimize")),
                    new("Ripara tabella", TableOperationCommand, new TableOperation(node, "repair")),
                ]
                :
                [
                    new("Analizza (ANALYZE)", TableOperationCommand, new TableOperation(node, "analyze")),
                    new("Vacuum", TableOperationCommand, new TableOperation(node, "vacuum")),
                    new("Vacuum completo", TableOperationCommand, new TableOperation(node, "vacuum-full")),
                    new("Ricostruisci indici (REINDEX)", TableOperationCommand, new TableOperation(node, "reindex")),
                ]),
            new("Reverse tabelle a modello…", TableOperationCommand, new TableOperation(node, "model"), "network"),
            ContextAction.Separator,
            new("Gestisci gruppo", Glyph: "folder", IsEnabled: !view, Children: view ? null : ManageGroupActions(node)),
            new("Aggiungi ai preferiti", AddFavoriteCommand, node, "star", !view),
            new("Copia", CopyTablesCommand, node, "copy", !view),
            new("Incolla", PasteTablesCommand, node, "download", HasClipboard),
            new("Rinomina", TableOperationCommand, new TableOperation(node, "rename"), "pencil"),
            new("Crea collegamento rapido sul desktop", CreateShortcutCommand, node, "link", !view),
            ContextAction.Separator,
            new("Aggiorna", RefreshNodeCommand, node.CategoryNode, "refresh"),
            new("Informazioni oggetto", TableOperationCommand, new TableOperation(node, "info"), "info"),
        ];
    }

    [RelayCommand]
    private async Task TableOperationAsync(TableOperation? operation)
    {
        if (operation?.Node is not { Profile: { } profile, Database: { } database, Schema: { } schema } node) return;
        var provider = _databaseAccess.Provider(profile);
        var mysql = provider.UsesMySqlScriptSyntax;
        var name = TableName(node);
        var view = IsViewNode(node);
        try
        {
            switch (operation.Kind)
            {
                case "new":
                    OpenDesigner(profile, database, schema, null);
                    return;
                case "drop":
                    if (!await _dialogs.ConfirmAsync(view ? "Elimina vista" : "Elimina tabella",
                            $"Eliminare definitivamente {(view ? "la vista" : "la tabella")} «{node.Name}» da «{database}»?\n\nL’operazione non può essere annullata.", "Elimina", danger: true)) return;
                    await RunTableSqlAsync(node, $"DROP {(view ? "VIEW" : "TABLE")} {name}");
                    CloseTabsForTable(profile.Id, database, node.Name);
                    StatusMessage = $"«{node.Name}» eliminata.";
                    break;
                case "empty":
                    if (!await _dialogs.ConfirmAsync("Svuota tabella", $"Eliminare tutti i record di «{node.Name}»? (DELETE: rispetta trigger e chiavi esterne)", "Svuota", danger: true)) return;
                    await RunTableSqlAsync(node, $"DELETE FROM {name}");
                    StatusMessage = $"«{node.Name}» svuotata.";
                    break;
                case "truncate":
                    if (!await _dialogs.ConfirmAsync("Tronca tabella", $"Troncare «{node.Name}»? Tutti i record vengono eliminati e il contatore AUTO_INCREMENT/identità riparte.", "Tronca", danger: true)) return;
                    await RunTableSqlAsync(node, profile.Kind switch
                    {
                        DatabaseKind.MySql or DatabaseKind.SqlServer => $"TRUNCATE TABLE {name}",
                        // SQLite: DELETE senza WHERE usa l'ottimizzazione «truncate»; si azzera anche il contatore AUTOINCREMENT.
                        DatabaseKind.Sqlite => $"DELETE FROM {name};\nDELETE FROM sqlite_sequence WHERE name = {provider.FormatLiteral(node.Name)}",
                        _ => $"TRUNCATE TABLE {name} RESTART IDENTITY",
                    });
                    StatusMessage = $"«{node.Name}» troncata.";
                    break;
                case "duplicate-data":
                case "duplicate-structure":
                    await DuplicateTablesAsync(new TableClipboard(profile, database, schema, [node.Name]), operation.Kind == "duplicate-data");
                    return;
                case "import":
                case "export":
                    await OpenTableAsync(profile, database, schema, node.Name);
                    if (SelectedTab is TableTabViewModel table && table.TableName == node.Name)
                    {
                        if (operation.Kind == "import") await table.ImportCommand.ExecuteAsync(null);
                        else await table.ExportCommand.ExecuteAsync(null);
                    }
                    return;
                case "dump-data":
                case "dump-structure":
                    await DumpTableAsync(node, operation.Kind == "dump-data");
                    return;
                case "analyze" or "check" or "optimize" or "repair" or "vacuum" or "vacuum-full" or "reindex":
                    await MaintainAsync(node, operation.Kind);
                    return;
                case "model":
                    await OpenModelAsync(node);
                    if (SelectedTab is ModelTabViewModel model)
                    {
                        for (var i = 0; i < 50 && model.Tables.Count == 0; i++) await Task.Delay(100);
                        model.SearchText = node.Name;
                    }
                    return;
                case "rename":
                {
                    var newName = await _dialogs.PromptAsync("Rinomina", $"Nuovo nome per «{node.Name}»:", node.Name, "Rinomina");
                    if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == node.Name) return;
                    newName = newName.Trim();
                    var target = mysql ? provider.QuoteIdentifier(newName) : $"{provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(newName)}";
                    await RunTableSqlAsync(node, profile.Kind switch
                    {
                        DatabaseKind.MySql => $"RENAME TABLE {name} TO {target}",
                        DatabaseKind.SqlServer => $"EXEC sp_rename {provider.FormatLiteral($"{schema}.{node.Name}")}, {provider.FormatLiteral(newName)}",
                        DatabaseKind.Sqlite when view => throw new NotSupportedException("SQLite non permette di rinominare una vista: modificala e salvala con un altro nome."),
                        DatabaseKind.Sqlite => $"ALTER TABLE {provider.QuoteIdentifier(node.Name)} RENAME TO {provider.QuoteIdentifier(newName)}",
                        _ => $"ALTER {(view ? "VIEW" : "TABLE")} {name} RENAME TO {provider.QuoteIdentifier(newName)}",
                    });
                    CloseTabsForTable(profile.Id, database, node.Name);
                    StatusMessage = $"«{node.Name}» rinominata in «{newName}».";
                    break;
                }
                case "info":
                    IsInfoVisible = true;
                    SelectedNode = node;
                    UpdateInfoTarget();
                    return;
            }
        }
        catch (Exception exception)
        {
            await ReportErrorAsync("Operazione non riuscita", exception);
            return;
        }
        if (node.Parent is { } category) await RefreshTablesOfAsync(profile, database, schema);
    }

    private async Task RunTableSqlAsync(TreeNodeViewModel node, string sql)
    {
        var results = await _databaseAccess.ExecuteBatchAsync(node.Profile!, node.Database, sql, 0, CancellationToken.None);
        if (results.FirstOrDefault(r => r.Error is not null) is { } failed) throw failed.Error!;
    }

    private void CloseTabsForTable(Guid profileId, string database, string table)
    {
        foreach (var tab in OpenTabs.OfType<TableTabViewModel>().Where(t => t.ProfileId == profileId && t.Database == database && t.TableName == table).ToArray())
            CloseTab(tab);
    }

    private static string LegacyMaintenanceSql(TreeNodeViewModel node, string kind, string name) => kind switch
    {
        "analyze" => $"ANALYZE {(node.Profile!.Kind == DatabaseKind.MySql ? "TABLE " : string.Empty)}{name}",
        "check" => $"CHECK TABLE {name}",
        "optimize" => $"OPTIMIZE TABLE {name}",
        "repair" => $"REPAIR TABLE {name}",
        "vacuum" => $"VACUUM {name}",
        "vacuum-full" => $"VACUUM FULL {name}",
        _ => $"REINDEX TABLE {name}"
    };

    private async Task MaintainAsync(TreeNodeViewModel node, string kind)
    {
        var name = TableName(node);
        var dialect = node.Profile!.Kind;
        var literal = _databaseAccess.Provider(node.Profile).FormatLiteral(dialect == DatabaseKind.SqlServer ? $"{node.Schema}.{node.Name}" : node.Name);
        var sql = (dialect, kind) switch
        {
            (DatabaseKind.SqlServer, "analyze") => $"UPDATE STATISTICS {name}",
            (DatabaseKind.SqlServer, "check") => $"DBCC CHECKTABLE ({literal})",
            (DatabaseKind.SqlServer, "optimize") => $"ALTER INDEX ALL ON {name} REORGANIZE",
            (DatabaseKind.SqlServer, _) => $"ALTER INDEX ALL ON {name} REBUILD",
            (DatabaseKind.Sqlite, "analyze") => $"ANALYZE {name}",
            (DatabaseKind.Sqlite, "check") => $"PRAGMA integrity_check({literal})",
            (DatabaseKind.Sqlite, "vacuum") => "VACUUM",
            (DatabaseKind.Sqlite, _) => $"REINDEX {name}",
            _ => LegacyMaintenanceSql(node, kind, name),
        };
        StatusMessage = $"{sql}…";
        var results = await _databaseAccess.ExecuteBatchAsync(node.Profile!, node.Database, sql, 50, CancellationToken.None);
        var result = results.LastOrDefault();
        if (result?.Error is { } error)
        {
            await _dialogs.ShowMessageAsync("Manutenzione non riuscita", error.Message, isError: true);
            return;
        }
        var report = new StringBuilder($"{sql}\n\n");
        if (result?.Result is { HasResultSet: true } table)
            foreach (var row in table.Rows) report.AppendLine(string.Join("  ·  ", row.Select(v => v?.ToString() ?? "NULL")));
        else report.AppendLine($"Completato in {result?.Elapsed.TotalSeconds:0.00} s.");
        await _dialogs.ShowMessageAsync("Manutenzione tabella", report.ToString().TrimEnd());
        StatusMessage = $"Manutenzione di «{node.Name}» completata.";
    }

    private async Task DumpTableAsync(TreeNodeViewModel node, bool includeData)
    {
        var profile = node.Profile!;
        var path = await _dialogs.PickSaveFileAsync(includeData ? "Dump tabella (struttura e dati)" : "Dump tabella (solo struttura)",
            $"{node.Name}_{DateTime.Now:yyyyMMdd_HHmm}.sql", "sql", new FileFilter("Script SQL", "*.sql"));
        if (path is null) return;
        var transfer = new DataTransferViewModel(_databaseAccess, Profiles.ToArray(), profile, node.Database, _dialogs, preselect: [node.Name])
        {
            TargetIsFile = true,
            TargetFile = path,
            CreateTables = true,
            CopyData = includeData
        };
        for (var i = 0; i < 50 && !transfer.Tables.Any(t => t.Name == node.Name); i++) await Task.Delay(100);
        await transfer.StartCommand.ExecuteAsync(null);
        StatusMessage = transfer.StatusMessage;
    }
}
