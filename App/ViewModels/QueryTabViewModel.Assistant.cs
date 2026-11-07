using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Scheda «Assistente» dell'editor: Claude spiega, genera, ottimizza e corregge le query.</summary>
public partial class QueryTabViewModel
{
    public const int AssistantTabIndex = 3;

    [ObservableProperty] private string _assistantText = string.Empty;
    [ObservableProperty] private string _assistantTitle = "Assistente SQL";
    [ObservableProperty] private bool _assistantBusy;
    [ObservableProperty] private string? _assistantSql;

    private string? _lastErrorSql;
    private string? _lastError;
    private CancellationTokenSource? _assistantRun;

    public bool AssistantConfigured => SqlAssistant.IsConfigured;
    public bool HasAssistantSql => !string.IsNullOrWhiteSpace(AssistantSql);
    partial void OnAssistantSqlChanged(string? value) => OnPropertyChanged(nameof(HasAssistantSql));

    private void RememberError(string sql, string error)
    {
        _lastErrorSql = sql;
        _lastError = error;
    }

    private bool IsMySql => _profile.Kind == DatabaseKind.MySql;

    private string? TargetSql() =>
        !string.IsNullOrWhiteSpace(SelectedSql) ? SelectedSql
        : !string.IsNullOrWhiteSpace(CurrentStatementSql) ? CurrentStatementSql
        : string.IsNullOrWhiteSpace(SqlText) ? null : SqlText;

    private async Task<string> SchemaTextAsync()
    {
        if (Schema is null && SelectedDatabase is not null) await LoadSchemaAsync();
        return SqlAssistant.DescribeSchema(Schema);
    }

    private async Task RunAssistantAsync(string title, Func<Task<string?>> buildPrompt)
    {
        if (AssistantBusy) return;
        ResultTabIndex = AssistantTabIndex;
        AssistantTitle = title;
        AssistantSql = null;
        if (!SqlAssistant.IsConfigured)
        {
            AssistantText = "Per usare l'assistente serve una chiave API di Anthropic.\n\nApri Strumenti › Opzioni › Assistente SQL e incollala (viene salvata cifrata solo su questo computer), oppure imposta la variabile d'ambiente ANTHROPIC_API_KEY.\n\nLe richieste includono la query e la struttura del database (nomi di tabelle e colonne), mai i dati delle righe.";
            return;
        }
        AssistantBusy = true;
        AssistantText = "Sto pensando…";
        _assistantRun = new CancellationTokenSource();
        try
        {
            var prompt = await buildPrompt();
            if (prompt is null) { AssistantText = string.Empty; return; }
            var answer = await SqlAssistant.AskAsync(prompt, IsMySql, null,
                partial => Avalonia.Threading.Dispatcher.UIThread.Post(() => AssistantText = partial), _assistantRun.Token);
            AssistantText = answer;
            AssistantSql = SqlAssistant.ExtractSql(answer);
        }
        catch (OperationCanceledException) { AssistantText += "\n\n(richiesta interrotta)"; }
        catch (SqlAssistantException exception) { AssistantText = exception.Message; }
        catch (Exception exception) { AssistantText = $"Errore: {exception.Message}"; }
        finally
        {
            AssistantBusy = false;
            _assistantRun.Dispose();
            _assistantRun = null;
        }
    }

    [RelayCommand]
    private Task AssistExplainAsync() => RunAssistantAsync("Spiegazione della query", async () =>
    {
        var sql = TargetSql();
        if (sql is null) { StatusMessage = "Scrivi o seleziona una query da spiegare."; return null; }
        return SqlAssistant.ExplainPrompt(sql, await SchemaTextAsync());
    });

    [RelayCommand]
    private Task AssistGenerateAsync() => RunAssistantAsync("SQL generato", async () =>
    {
        if (_dialogs is null) return null;
        var request = await _dialogs.PromptAsync("Genera SQL", "Descrivi in italiano cosa vuoi ottenere (es. «i 10 clienti con più ordini nel 2025»):", string.Empty, "Genera");
        if (string.IsNullOrWhiteSpace(request)) return null;
        AssistantTitle = $"SQL generato · {request}";
        return SqlAssistant.GeneratePrompt(request, await SchemaTextAsync());
    });

    [RelayCommand]
    private Task AssistOptimizeAsync() => RunAssistantAsync("Ottimizzazione e indici", async () =>
    {
        var sql = TargetSql();
        if (sql is null) { StatusMessage = "Scrivi o seleziona una query da ottimizzare."; return null; }
        var statement = Navifox.Core.SqlScriptSplitter.Split(sql, _profile.Kind).FirstOrDefault() ?? sql;
        string? plan = null;
        if (statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || statement.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var results = await _databaseAccess.ExecuteBatchAsync(_profile, SelectedDatabase, "EXPLAIN " + statement, 200, CancellationToken.None);
                if (results.FirstOrDefault()?.Result is { } explain)
                    plan = string.Join("\n", new[] { string.Join(" | ", explain.Columns.Select(c => c.Name)) }
                        .Concat(explain.Rows.Select(r => string.Join(" | ", r.Select(v => v?.ToString() ?? "NULL")))));
            }
            catch { /* senza piano si analizza solo il testo */ }
        }
        return SqlAssistant.OptimizePrompt(statement, plan, await SchemaTextAsync());
    });

    [RelayCommand]
    private Task AssistFixAsync() => RunAssistantAsync("Correzione dell'errore", async () =>
    {
        if (_lastError is null || _lastErrorSql is null) { AssistantText = "Nessun errore recente da correggere: esegui prima la query."; return null; }
        return SqlAssistant.FixPrompt(_lastErrorSql, _lastError, await SchemaTextAsync());
    });

    [RelayCommand]
    private void StopAssistant() => _assistantRun?.Cancel();

    [RelayCommand]
    private void InsertAssistantSql()
    {
        if (string.IsNullOrWhiteSpace(AssistantSql)) return;
        if (string.IsNullOrWhiteSpace(SqlText)) SqlText = AssistantSql;
        else InsertRequested?.Invoke(Environment.NewLine + AssistantSql + Environment.NewLine);
    }

    [RelayCommand]
    private void ReplaceWithAssistantSql()
    {
        if (!string.IsNullOrWhiteSpace(AssistantSql)) SqlText = AssistantSql;
    }

    [RelayCommand]
    private async Task CopyAssistantAsync()
    {
        if (string.IsNullOrEmpty(AssistantText)) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
            await clipboard.SetTextAsync(AssistantText);
        StatusMessage = "Risposta dell'assistente copiata.";
    }
}
