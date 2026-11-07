using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>
/// Editor di funzioni, procedure, trigger, eventi (definizione CREATE completa) e viste (nome + SELECT).
/// Il salvataggio usa <see cref="SchemaObjectSql"/>: su MySQL elimina e ricrea ripristinando la versione precedente in caso di errore,
/// su PostgreSQL usa CREATE OR REPLACE o una transazione.
/// </summary>
public partial class SchemaObjectEditorViewModel : WorkspaceTabViewModel
{
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IDialogService _dialogs;
    private readonly Func<SchemaObjectEditorViewModel, Task>? _saved;
    private readonly Action<string, bool>? _openQuery;
    private string? _originalDefinition;
    private bool _loading;

    public SchemaObjectEditorViewModel(IDatabaseAccessService databaseAccess, IDialogService dialogs, ConnectionProfile profile, string database, string schema,
        SchemaObjectKind kind, DatabaseObjectInfo? item, string? template = null, Func<SchemaObjectEditorViewModel, Task>? saved = null, Action<string, bool>? openQuery = null)
    {
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        _saved = saved;
        _openQuery = openQuery;
        Profile = profile;
        Database = database;
        Schema = schema;
        Kind = kind;
        Item = item;
        IsMySql = databaseAccess.Provider(profile).UsesMySqlScriptSyntax;
        _viewName = item?.ObjectName ?? (kind == SchemaObjectKind.View ? "nuova_vista" : string.Empty);
        _definition = template ?? string.Empty;
        _isDirty = item is null;
    }

    public ConnectionProfile Profile { get; }
    public string Database { get; }
    public string Schema { get; }
    public SchemaObjectKind Kind { get; }
    public bool IsMySql { get; }
    public bool IsView => Kind == SchemaObjectKind.View;
    public bool IsRoutine => Kind == SchemaObjectKind.Routine;
    public bool IsNew => Item is null;

    /// <summary>Oggetto com'è sul server (null finché un oggetto nuovo non viene salvato).</summary>
    public DatabaseObjectInfo? Item { get; private set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TabTitle))] private string _definition;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TabTitle))] private string _viewName;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TabTitle))] private bool _isDirty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public override ConnectionProfile? TabConnection => Profile;

    public string KindLabel => Kind switch
    {
        SchemaObjectKind.Routine => Item?.Kind == "PROCEDURE" || (Item is null && Definition.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase)) ? "Procedura" : "Funzione",
        SchemaObjectKind.Trigger => "Trigger",
        SchemaObjectKind.Event => "Evento",
        SchemaObjectKind.View => "Vista",
        _ => "Oggetto"
    };

    public string DisplayName => IsView ? ViewName : Item?.Name ?? SchemaObjectSql.DefinedName(Definition) ?? "Senza titolo";

    public override string TabTitle => $"{(IsDirty ? "● " : string.Empty)}{DisplayName} @{Database} ({Profile.Name}) – {KindLabel}";
    public override string TabGlyph => Kind switch
    {
        SchemaObjectKind.Routine => "function",
        SchemaObjectKind.Trigger => "zap",
        SchemaObjectKind.Event => "clock",
        _ => "eye"
    };

    public string Hint => Kind switch
    {
        SchemaObjectKind.View => "Scrivi la SELECT della vista. «Anteprima» la esegue in una nuova query.",
        SchemaObjectKind.Routine when IsMySql => "Definizione completa (CREATE FUNCTION/PROCEDURE). Al salvataggio l'oggetto viene ricreato; se la nuova versione ha errori si ripristina quella precedente.",
        SchemaObjectKind.Routine => "Definizione completa (CREATE OR REPLACE FUNCTION/PROCEDURE). Cambiare nome o argomenti crea un nuovo oggetto.",
        SchemaObjectKind.Trigger when IsMySql => "Definizione completa (CREATE TRIGGER … FOR EACH ROW). In caso di errore si ripristina la versione precedente.",
        SchemaObjectKind.Trigger => "CREATE TRIGGER … EXECUTE FUNCTION f(): il trigger viene ricreato in una transazione.",
        _ => "Definizione completa (CREATE EVENT … ON SCHEDULE … DO …). Richiede event_scheduler attivo sul server."
    };

    partial void OnDefinitionChanged(string value)
    {
        if (!_loading) IsDirty = true;
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnViewNameChanged(string value)
    {
        if (!_loading) IsDirty = true;
        OnPropertyChanged(nameof(DisplayName));
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (Item is null)
        {
            StatusMessage = $"Nuova {KindLabel.ToLowerInvariant()}: modifica la definizione e premi Salva.";
            return;
        }
        IsLoading = true;
        try
        {
            var definition = await _databaseAccess.GetSchemaObjectDefinitionAsync(Profile, Database, Schema, Kind, Item, CancellationToken.None);
            if (IsView && IsMySql) definition = SqlFormatter.Format(definition, mySql: true);
            _loading = true;
            Definition = definition;
            ViewName = Item.ObjectName;
            _originalDefinition = definition;
            IsDirty = false;
            StatusMessage = Item.Detail ?? string.Empty;
        }
        catch (Exception exception) { StatusMessage = $"Impossibile leggere la definizione: {exception.Message}"; }
        finally
        {
            _loading = false;
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Definition) || IsView && string.IsNullOrWhiteSpace(ViewName))
        {
            await _dialogs.ShowMessageAsync("Impossibile salvare", IsView ? "Indica il nome della vista e la sua SELECT." : "La definizione è vuota.", isError: true);
            return;
        }
        if (!await SqlGuard.ConfirmProductionAsync(_dialogs, Profile, $"Stai per salvare {KindLabel.ToLowerInvariant()} «{DisplayName}».")) return;
        var plan = IsView
            ? SchemaObjectSql.BuildViewSave(IsMySql, Schema, Item?.ObjectName, ViewName.Trim(), Definition)
            : SchemaObjectSql.BuildSave(IsMySql, Schema, Kind, Item, _originalDefinition, Definition);
        IsLoading = true;
        try
        {
            await _databaseAccess.ExecuteSavePlanAsync(Profile, Database, plan, CancellationToken.None);
        }
        catch (Exception exception)
        {
            IsLoading = false;
            var message = exception switch
            {
                SchemaObjectSaveException restore => $"{Inner(restore)}\n\n{restore.Outcome}",
                _ => Inner(exception) + (IsMySql ? string.Empty : "\n\nNessuna modifica è stata applicata (transazione annullata).")
            };
            await _dialogs.ShowMessageAsync("Salvataggio non riuscito", message, isError: true);
            return;
        }
        await RefreshItemAsync();
        StatusMessage = $"{KindLabel} «{DisplayName}» salvat{(Kind is SchemaObjectKind.Trigger or SchemaObjectKind.Event ? "o" : "a")}.";
        if (_saved is not null) await _saved(this);
    }

    private static string Inner(Exception exception) =>
        exception.InnerException is ScriptExecutionException script ? script.InnerException?.Message ?? script.Message
        : exception is ScriptExecutionException direct ? direct.InnerException?.Message ?? direct.Message
        : exception.InnerException?.Message ?? exception.Message;

    /// <summary>Dopo il salvataggio rilegge l'oggetto dal server (nome nuovo compreso) e la sua definizione.</summary>
    private async Task RefreshItemAsync()
    {
        try
        {
            if (IsView)
            {
                var name = ViewName.Trim();
                Item = new DatabaseObjectInfo(name, "VIEW");
            }
            else
            {
                var name = SchemaObjectSql.DefinedName(Definition) ?? Item?.ObjectName;
                var items = await _databaseAccess.ListSchemaObjectsAsync(Profile, Database, Schema, Kind, CancellationToken.None);
                Item = items.FirstOrDefault(i => string.Equals(i.ObjectName, name, StringComparison.OrdinalIgnoreCase)
                                                && (Item?.Identity is null || Kind != SchemaObjectKind.Routine || i.Identity == Item.Identity))
                       ?? items.FirstOrDefault(i => string.Equals(i.ObjectName, name, StringComparison.OrdinalIgnoreCase))
                       ?? Item;
            }
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(KindLabel));
            await LoadAsync();
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (_openQuery is null) return;
        if (IsView)
        {
            _openQuery(Definition.Trim().TrimEnd(';'), true);
            return;
        }
        if (Item is null)
        {
            StatusMessage = "Salva prima la funzione o la procedura.";
            return;
        }
        try
        {
            var parameters = await _databaseAccess.GetRoutineParametersAsync(Profile, Database, Schema, Item, CancellationToken.None);
            _openQuery(SchemaObjectSql.RunTemplate(IsMySql, Schema, Item, parameters), parameters.Count == 0);
        }
        catch (Exception exception) { StatusMessage = $"Impossibile leggere i parametri: {exception.Message}"; }
    }

    [RelayCommand]
    private void Format()
    {
        if (IsView) Definition = SqlFormatter.Format(Definition, IsMySql);
    }
}

/// <summary>Progetta sequenza (PostgreSQL).</summary>
public partial class SequenceEditorViewModel : WorkspaceTabViewModel
{
    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IDialogService _dialogs;
    private readonly Func<SequenceEditorViewModel, Task>? _saved;
    private SequenceInfo? _original;
    private bool _loading;

    public SequenceEditorViewModel(IDatabaseAccessService databaseAccess, IDialogService dialogs, ConnectionProfile profile, string database, string schema, string? name,
        Func<SequenceEditorViewModel, Task>? saved = null)
    {
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        _saved = saved;
        Profile = profile;
        Database = database;
        Schema = schema;
        _name = name ?? "nuova_sequenza";
        _originalName = name;
        _isDirty = name is null;
    }

    private readonly string? _originalName;

    public ConnectionProfile Profile { get; }
    public string Database { get; }
    public string Schema { get; }
    public IReadOnlyList<string> DataTypes { get; } = ["smallint", "integer", "bigint"];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TabTitle))] private string _name;
    [ObservableProperty] private string _dataType = "bigint";
    [ObservableProperty] private string _start = "1";
    [ObservableProperty] private string _increment = "1";
    [ObservableProperty] private string? _min;
    [ObservableProperty] private string? _max;
    [ObservableProperty] private string _cache = "1";
    [ObservableProperty] private bool _cycle;
    [ObservableProperty] private string? _current;
    [ObservableProperty] private string? _ownedBy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TabTitle))] private bool _isDirty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _sqlPreview = string.Empty;

    public override string TabTitle => $"{(IsDirty ? "● " : string.Empty)}{Name} @{Database} ({Profile.Name}) – Sequenza";
    public override string TabGlyph => "hash";
    public override ConnectionProfile? TabConnection => Profile;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(IsDirty) or nameof(TabTitle) or nameof(IsLoading) or nameof(StatusMessage) or nameof(SqlPreview)) return;
        IsDirty = true;
        RefreshPreview();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_original is null && _originalName is null)
        {
            RefreshPreview();
            StatusMessage = "Nuova sequenza: imposta i valori e premi Salva.";
            return;
        }
        IsLoading = true;
        try
        {
            var sequence = await _databaseAccess.GetSequenceAsync(Profile, Database, Schema, _original?.Name ?? _originalName!, CancellationToken.None);
            Apply(sequence);
            StatusMessage = sequence.Current is null ? "La sequenza non è ancora stata usata." : $"Valore attuale {sequence.Current}";
        }
        catch (Exception exception) { StatusMessage = $"Impossibile leggere la sequenza: {exception.Message}"; }
        finally { IsLoading = false; }
    }

    private void Apply(SequenceInfo sequence)
    {
        _loading = true;
        try
        {
            _original = sequence;
            Name = sequence.Name;
            DataType = sequence.DataType;
            Start = sequence.Start.ToString();
            Increment = sequence.Increment.ToString();
            Min = sequence.Min?.ToString();
            Max = sequence.Max?.ToString();
            Cache = sequence.Cache.ToString();
            Cycle = sequence.Cycle;
            Current = sequence.Current?.ToString();
            OwnedBy = sequence.OwnedBy;
            IsDirty = false;
        }
        finally { _loading = false; }
        RefreshPreview();
    }

    private SequenceInfo Build()
    {
        static long Parse(string? value, string label) => long.TryParse(value?.Trim(), out var number) ? number : throw new FormatException($"«{label}» deve essere un numero intero.");
        static long? Optional(string? value, string label) => string.IsNullOrWhiteSpace(value) ? null : Parse(value, label);
        return new SequenceInfo(Name.Trim(), Parse(Start, "Inizio"), Parse(Increment, "Incremento"), Optional(Min, "Minimo"), Optional(Max, "Massimo"),
            Parse(Cache, "Cache"), Cycle, Optional(Current, "Valore attuale"), string.IsNullOrWhiteSpace(OwnedBy) ? null : OwnedBy.Trim(), DataType);
    }

    private void RefreshPreview()
    {
        try
        {
            var statements = SchemaObjectSql.SequenceSql(Schema, _original, Build());
            SqlPreview = statements.Count == 0 ? "-- Nessuna modifica." : string.Join(";\n", statements) + ";";
        }
        catch (Exception exception) { SqlPreview = "-- " + exception.Message; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IReadOnlyList<string> statements;
        try { statements = SchemaObjectSql.SequenceSql(Schema, _original, Build()); }
        catch (Exception exception)
        {
            await _dialogs.ShowMessageAsync("Impossibile salvare", exception.Message, isError: true);
            return;
        }
        if (statements.Count == 0)
        {
            StatusMessage = "Nessuna modifica da salvare.";
            IsDirty = false;
            return;
        }
        if (!await SqlGuard.ConfirmProductionAsync(_dialogs, Profile, $"Stai per modificare la sequenza «{Name}».")) return;
        IsLoading = true;
        try
        {
            await _databaseAccess.ExecuteStatementsAsync(Profile, Database, statements, CancellationToken.None, atomic: true);
            Apply(await _databaseAccess.GetSequenceAsync(Profile, Database, Schema, Name.Trim(), CancellationToken.None));
            StatusMessage = $"Sequenza «{Name}» salvata.";
        }
        catch (Exception exception)
        {
            var detail = exception is ScriptExecutionException script ? script.InnerException?.Message ?? script.Message : exception.Message;
            await _dialogs.ShowMessageAsync("Salvataggio non riuscito", detail + "\n\nNessuna modifica è stata applicata (transazione annullata).", isError: true);
            return;
        }
        finally { IsLoading = false; }
        if (_saved is not null) await _saved(this);
    }
}
