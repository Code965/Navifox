using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Dialects;

namespace Navifox.App.ViewModels;

public partial class HistoryTabViewModel : WorkspaceTabViewModel
{
    private readonly Action<ActivityEntry> _openInQuery;
    private readonly IDialogService _dialogs;
    private readonly ActivityJournal _journal;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private int _limit = 200;
    private Guid? _operation;
    private bool _closed;

    public HistoryTabViewModel(Action<ActivityEntry> openInQuery, IDialogService dialogs)
    {
        _openInQuery = openInQuery;
        _dialogs = dialogs;
        _journal = ActivityRecording.Journal ?? throw new InvalidOperationException("Registro attività non disponibile.");
        _journal.Changed += QueueRefresh;
        _refresh.Tick += (_, _) => { _refresh.Stop(); ApplyFilter(); };
        ApplyFilter();
    }

    public override string TabTitle => "Registro attività";
    public override string TabGlyph => "history";
    public ObservableCollection<ActivityEntry> Visible { get; } = [];
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _onlyErrors;
    [ObservableProperty] private bool _onlyTransfers;
    [ObservableProperty] private string _countLabel = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInQueryCommand), nameof(CopyCommand), nameof(ShowOperationCommand), nameof(ExportSqlCommand), nameof(ExportDatabaseSqlCommand))]
    private ActivityEntry? _selected;
    public event Action<string>? CopyRequested;

    partial void OnFilterTextChanged(string value) { _limit = 200; QueueRefresh(); }
    partial void OnOnlyErrorsChanged(bool value) { _limit = 200; QueueRefresh(); }
    partial void OnOnlyTransfersChanged(bool value) { _limit = 200; QueueRefresh(); }

    private void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closed && !_refresh.IsEnabled) _refresh.Start(); });

    private void ApplyFilter()
    {
        if (_closed) return;
        try
        {
            var selectedSequence = Selected?.Sequence;
            var entries = _journal.Read(FilterText, OnlyErrors, _operation, _limit, onlyTransfers: OnlyTransfers);
            Visible.Clear();
            foreach (var entry in entries) Visible.Add(entry);
            Selected = Visible.FirstOrDefault(e => e.Sequence == selectedSequence);
            CountLabel = $"{Visible.Count:N0} attività visualizzate" + (_operation is null ? " · archivio permanente" : " · operazione selezionata") +
                (Visible.Count == _limit ? " · usa «Mostra altre» per le precedenti" : "");
        }
        catch (Exception exception) { CountLabel = "Impossibile leggere il registro: " + exception.Message; }
    }

    private bool HasSelection() => Selected is not null;
    private bool HasSql() => Selected is { Sql.Length: > 0 };

    [RelayCommand(CanExecute = nameof(HasSql))]
    private void OpenInQuery()
    {
        if (Selected is not null) _openInQuery(Selected);
    }
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy() => CopyRequested?.Invoke(Selected?.Details ?? "");
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowOperation()
    {
        _operation = Selected!.OperationId;
        FilterText = ""; OnlyErrors = false; OnlyTransfers = false; _limit = 200;
        ApplyFilter();
    }
    [RelayCommand]
    private void ShowAll() { _operation = null; FilterText = ""; OnlyErrors = false; OnlyTransfers = false; _limit = 200; ApplyFilter(); }
    [RelayCommand]
    private void LoadMore() { _limit += 200; ApplyFilter(); }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExportSqlAsync()
    {
        if (Selected is not { } selected) return;
        var path = await _dialogs.PickSaveFileAsync("Esporta script dell’operazione per il database selezionato", $"operazione_{selected.OperationLabel}.sql", "sql", new FileFilter("Script SQL", "*.sql"));
        if (path is null) return;
        await WriteExportAsync(path, writer => _journal.WriteScript(writer, selected.OperationId, selected.ProfileId, selected.Database));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExportDatabaseSqlAsync()
    {
        if (Selected is not { } selected) return;
        var path = await _dialogs.PickSaveFileAsync($"Esporta tutte le attività di {selected.Target} in ordine", "registro-database.sql", "sql", new FileFilter("Script SQL", "*.sql"));
        if (path is null) return;
        await WriteExportAsync(path, writer => _journal.WriteScript(writer, null, selected.ProfileId, selected.Database));
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        var path = await _dialogs.PickSaveFileAsync("Esporta registro completo dei filtri correnti", "registro-attivita.json", "json", new FileFilter("Registro JSON", "*.json"));
        if (path is null) return;
        var search = FilterText; var problems = OnlyErrors; var operation = _operation; var transfers = OnlyTransfers;
        await WriteExportAsync(path, writer =>
        {
            writer.WriteLine("[");
            var first = true;
            foreach (var entry in _journal.Enumerate(search, problems, operation, onlyTransfers: transfers))
            {
                if (!first) writer.WriteLine(",");
                writer.Write(JsonSerializer.Serialize(entry));
                first = false;
            }
            writer.WriteLine("\n]");
        });
    }

    private async Task WriteExportAsync(string path, Action<TextWriter> write)
    {
        try
        {
            await Task.Run(() =>
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false))) write(writer);
                    File.Move(temporary, path, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
            CountLabel = "Esportato in " + path;
        }
        catch (Exception exception) { await _dialogs.ShowMessageAsync("Esportazione non riuscita", exception.Message, isError: true); }
    }

    public override ValueTask CloseAsync()
    {
        _closed = true;
        _journal.Changed -= QueueRefresh;
        _refresh.Stop();
        return ValueTask.CompletedTask;
    }
}
