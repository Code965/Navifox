using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MosaicDB.App.Services;
using MosaicDB.Core;
using MosaicDB.Core.Models;

namespace MosaicDB.App.ViewModels;

/// <summary>
/// «Progetta tabella»: campi, indici, chiavi esterne, trigger, opzioni, commento e anteprima SQL.
/// Salva con CREATE TABLE (tabella nuova) o con le ALTER TABLE calcolate rispetto alla struttura caricata.
/// </summary>
public partial class TableDesignerViewModel : WorkspaceTabViewModel
{
    private static readonly string[] MySqlTypes =
    [
        "int", "bigint", "smallint", "tinyint", "mediumint", "decimal", "float", "double", "bit", "bool",
        "varchar", "char", "text", "tinytext", "mediumtext", "longtext", "json",
        "date", "datetime", "timestamp", "time", "year",
        "blob", "tinyblob", "mediumblob", "longblob", "binary", "varbinary", "enum('a','b')", "set('a','b')",
        "geometry", "point", "linestring", "polygon",
    ];

    private static readonly string[] PostgresTypes =
    [
        "integer", "bigint", "smallint", "serial", "bigserial", "numeric", "real", "double precision", "boolean",
        "varchar", "char", "text", "uuid", "json", "jsonb", "bytea",
        "date", "timestamp", "timestamptz", "time", "timetz", "interval",
        "inet", "cidr", "macaddr", "money", "xml", "tsvector", "point", "integer[]", "text[]",
    ];

    private readonly IDatabaseAccessService _databaseAccess;
    private readonly IDialogService _dialogs;
    private readonly Func<TableDesignerViewModel, Task>? _saved;
    private TableDesign? _original;
    private bool _loading;
    private IReadOnlyList<CharsetInfo> _charsets = [];

    public TableDesignerViewModel(IDatabaseAccessService databaseAccess, IDialogService dialogs, ConnectionProfile profile, string database, string schema, string? table,
        Func<TableDesignerViewModel, Task>? saved = null)
    {
        _databaseAccess = databaseAccess;
        _dialogs = dialogs;
        _saved = saved;
        Profile = profile;
        Database = database;
        Schema = schema;
        _tableName = table ?? string.Empty;
        IsMySql = databaseAccess.Provider(profile).UsesMySqlScriptSyntax;
        DataTypes = IsMySql ? MySqlTypes : PostgresTypes;
        IndexKinds = IsMySql ? ["NORMAL", "UNIQUE", "FULLTEXT", "SPATIAL"] : ["NORMAL", "UNIQUE"];
        IndexMethods = IsMySql ? ["", "BTREE", "HASH"] : ["btree", "hash", "gin", "gist", "brin", "spgist"];
        TriggerEvents = IsMySql ? ["INSERT", "UPDATE", "DELETE"]
            : ["INSERT", "UPDATE", "DELETE", "INSERT OR UPDATE", "INSERT OR DELETE", "UPDATE OR DELETE", "INSERT OR UPDATE OR DELETE", "TRUNCATE"];
        TriggerTimings = IsMySql ? ["BEFORE", "AFTER"] : ["BEFORE", "AFTER", "INSTEAD OF"];
        DefaultSuggestions = IsMySql ? ["NULL", "''", "0", "CURRENT_TIMESTAMP", "(UUID())"] : ["NULL", "''", "0", "now()", "CURRENT_DATE", "gen_random_uuid()", "true", "false"];
        Fields.CollectionChanged += (_, _) => MarkDirty();
        Indexes.CollectionChanged += (_, _) => MarkDirty();
        ForeignKeys.CollectionChanged += (_, _) => MarkDirty();
        Triggers.CollectionChanged += (_, _) => MarkDirty();
    }

    public ConnectionProfile Profile { get; }
    public string Database { get; }
    public string Schema { get; }
    public bool IsMySql { get; }
    public bool IsPostgres => !IsMySql;
    public bool IsNew => _original is null;

    public IReadOnlyList<string> DataTypes { get; }
    public IReadOnlyList<string> IndexKinds { get; }
    public IReadOnlyList<string> IndexMethods { get; }
    public IReadOnlyList<string> TriggerEvents { get; }
    public IReadOnlyList<string> TriggerTimings { get; }
    public IReadOnlyList<string> DefaultSuggestions { get; }
    public IReadOnlyList<string> ForeignKeyRules { get; } = ["RESTRICT", "CASCADE", "SET NULL", "NO ACTION", "SET DEFAULT"];
    public IReadOnlyList<string> Engines { get; } = ["InnoDB", "MyISAM", "MEMORY", "CSV", "ARCHIVE", "BLACKHOLE", "Aria"];
    public ObservableCollection<string> Charsets { get; } = [];
    public ObservableCollection<string> Collations { get; } = [];
    public ObservableCollection<string> ReferencedTables { get; } = [];

    public ObservableCollection<DesignFieldViewModel> Fields { get; } = [];
    public ObservableCollection<DesignIndexViewModel> Indexes { get; } = [];
    public ObservableCollection<DesignForeignKeyViewModel> ForeignKeys { get; } = [];
    public ObservableCollection<DesignTriggerViewModel> Triggers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private string _tableName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private bool _isDirty;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _sqlPreview = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFieldsSection), nameof(IsIndexesSection), nameof(IsKeysSection), nameof(IsTriggersSection), nameof(IsOptionsSection), nameof(IsCommentSection), nameof(IsPreviewSection))]
    private int _selectedSection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedField))]
    [NotifyCanExecuteChangedFor(nameof(DeleteFieldCommand), nameof(PrimaryKeyCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private DesignFieldViewModel? _selectedField;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DeleteIndexCommand))] private DesignIndexViewModel? _selectedIndex;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DeleteForeignKeyCommand))] private DesignForeignKeyViewModel? _selectedForeignKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTrigger))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTriggerCommand))]
    private DesignTriggerViewModel? _selectedTrigger;

    // Opzioni (MySQL) e commento
    [ObservableProperty] private string? _engine;
    [ObservableProperty] private string? _charset;
    [ObservableProperty] private string? _collation;
    [ObservableProperty] private string? _autoIncrement;
    [ObservableProperty] private string? _tableComment;

    public bool HasSelectedField => SelectedField is not null;
    public bool HasSelectedTrigger => SelectedTrigger is not null;
    public bool IsFieldsSection => SelectedSection == 0;
    public bool IsIndexesSection => SelectedSection == 1;
    public bool IsKeysSection => SelectedSection == 2;
    public bool IsTriggersSection => SelectedSection == 3;
    public bool IsOptionsSection => SelectedSection == 4;
    public bool IsCommentSection => SelectedSection == 5;
    public bool IsPreviewSection => SelectedSection == 6;

    public string TriggerHint => IsMySql
        ? "Istruzione eseguita FOR EACH ROW, es.\nSET NEW.updated_at = NOW()\noppure BEGIN … END"
        : "FOR EACH ROW EXECUTE FUNCTION nome_funzione()";

    /// <summary>PostgreSQL non permette di cambiare la posizione delle colonne esistenti.</summary>
    public bool CanReorder => IsMySql || IsNew;

    public override string TabTitle => $"{(IsDirty ? "● " : string.Empty)}{(string.IsNullOrWhiteSpace(TableName) ? "Senza titolo" : TableName)} @{Database} ({Profile.Name}) – Tabella";
    public override string TabGlyph => "pencil";

    partial void OnSelectedSectionChanged(int value)
    {
        if (value == 6) RefreshPreview();
    }

    partial void OnCharsetChanged(string? value)
    {
        Collations.Clear();
        foreach (var collation in _charsets.FirstOrDefault(c => c.Name == value)?.Collations ?? []) Collations.Add(collation);
        MarkDirty();
    }

    partial void OnEngineChanged(string? value) => MarkDirty();
    partial void OnCollationChanged(string? value) => MarkDirty();
    partial void OnAutoIncrementChanged(string? value) => MarkDirty();
    partial void OnTableCommentChanged(string? value) => MarkDirty();

    private void MarkDirty()
    {
        if (_loading) return;
        IsDirty = true;
        if (SelectedSection == 6) RefreshPreview();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        _loading = true;
        try
        {
            if (IsMySql)
            {
                _charsets = await _databaseAccess.ListCharsetsAsync(Profile, CancellationToken.None);
                Charsets.Clear();
                foreach (var charset in _charsets) Charsets.Add(charset.Name);
            }
            var tables = await _databaseAccess.ListTablesAsync(Profile, Database, Schema, CancellationToken.None);
            ReferencedTables.Clear();
            foreach (var table in tables.Where(t => !t.Kind.Contains("VIEW", StringComparison.OrdinalIgnoreCase))) ReferencedTables.Add(table.Name);

            var design = _original is not null || !string.IsNullOrWhiteSpace(TableName)
                ? await _databaseAccess.GetTableDesignAsync(Profile, Database, Schema, _original?.Name ?? TableName, CancellationToken.None)
                : NewTableDesign();
            Apply(design, design.OriginalName is null ? null : design);
            StatusMessage = IsNew ? "Nuova tabella: aggiungi i campi e premi Salva." : $"{Fields.Count} campi · {Indexes.Count} indici · {ForeignKeys.Count} chiavi esterne · {Triggers.Count} trigger";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Impossibile leggere la struttura: {exception.Message}";
        }
        finally
        {
            _loading = false;
            IsLoading = false;
            IsDirty = IsNew;
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(CanReorder));
            if (SelectedSection == 6) RefreshPreview();
        }
    }

    private TableDesign NewTableDesign() => TableDesign.Empty(TableName) with
    {
        Columns = [new DesignColumn(null, "id", IsMySql ? "int" : "integer", NotNull: true, KeyOrder: 1, AutoIncrement: true)],
        Options = IsMySql ? new TableDesignOptions("InnoDB", "utf8mb4") : new TableDesignOptions(),
    };

    private void Apply(TableDesign design, TableDesign? original)
    {
        _original = original;
        Fields.Clear();
        foreach (var column in design.Columns) Fields.Add(Track(new DesignFieldViewModel(this, column)));
        Indexes.Clear();
        foreach (var index in design.Indexes) Indexes.Add(Track(new DesignIndexViewModel(index)));
        ForeignKeys.Clear();
        foreach (var key in design.ForeignKeys) ForeignKeys.Add(Track(new DesignForeignKeyViewModel(key)));
        Triggers.Clear();
        foreach (var trigger in design.Triggers) Triggers.Add(Track(new DesignTriggerViewModel(trigger)));
        Engine = design.Options.Engine;
        Charset = design.Options.Charset;
        Collation = design.Options.Collation;
        AutoIncrement = design.Options.AutoIncrement?.ToString();
        TableComment = design.Comment;
        SelectedField = Fields.FirstOrDefault();
        SelectedIndex = Indexes.FirstOrDefault();
        SelectedForeignKey = ForeignKeys.FirstOrDefault();
        SelectedTrigger = Triggers.FirstOrDefault();
    }

    private T Track<T>(T item) where T : INotifyPropertyChanged
    {
        item.PropertyChanged += (_, _) => MarkDirty();
        return item;
    }

    /// <summary>Quando un campo viene rinominato aggiorna i riferimenti in indici e chiavi esterne.</summary>
    internal void OnFieldRenamed(string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName) || oldName == newName) return;
        foreach (var index in Indexes) index.Columns = RenameIn(index.Columns, oldName, newName);
        foreach (var key in ForeignKeys) key.Columns = RenameIn(key.Columns, oldName, newName);
    }

    private static string RenameIn(string list, string oldName, string newName) =>
        string.Join(", ", Split(list).Select(part =>
        {
            var name = part.Split('(', ' ')[0];
            return name == oldName ? newName + part[name.Length..] : part;
        }));

    internal static IReadOnlyList<string> Split(string? list) =>
        (list ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public TableDesign BuildDesign()
    {
        var name = TableName.Trim();
        return new TableDesign(
            _original?.OriginalName, name,
            Fields.Select(f => f.ToModel()).ToArray(),
            Indexes.Select(i => i.ToModel()).ToArray(),
            ForeignKeys.Select(k => k.ToModel(Schema)).ToArray(),
            Triggers.Select(t => t.ToModel()).ToArray(),
            new TableDesignOptions(NullIfBlank(Engine), NullIfBlank(Charset), NullIfBlank(Collation),
                long.TryParse(AutoIncrement, out var ai) ? ai : null, _original?.Options.RowFormat),
            NullIfBlank(TableComment),
            _original?.PrimaryKeyName);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public IReadOnlyList<string> BuildStatements() => TableDesignSql.Build(IsMySql, Schema, _original, BuildDesign());

    private void RefreshPreview()
    {
        try
        {
            var statements = BuildStatements();
            SqlPreview = statements.Count == 0 ? "-- Nessuna modifica da salvare." : string.Join(";\n\n", statements) + ";";
        }
        catch (Exception exception)
        {
            SqlPreview = $"-- {exception.Message}";
        }
    }

    private string? Validate()
    {
        if (Fields.Count == 0) return "La tabella deve avere almeno un campo.";
        if (Fields.FirstOrDefault(f => string.IsNullOrWhiteSpace(f.Name)) is not null) return "Ogni campo deve avere un nome.";
        if (Fields.FirstOrDefault(f => string.IsNullOrWhiteSpace(f.Type)) is { } noType) return $"Il campo «{noType.Name}» non ha un tipo.";
        if (Fields.GroupBy(f => f.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicate) return $"Il campo «{duplicate.Key}» è presente più volte.";
        if (Indexes.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Name) || Split(i.Columns).Count == 0) is { } index) return $"L’indice «{index.Name}» deve avere un nome e almeno un campo.";
        if (ForeignKeys.FirstOrDefault(k => string.IsNullOrWhiteSpace(k.Name) || string.IsNullOrWhiteSpace(k.ReferencedTable) || Split(k.Columns).Count == 0 || Split(k.Columns).Count != Split(k.ReferencedColumns).Count) is { } key)
            return $"La chiave esterna «{key.Name}» è incompleta: servono nome, tabella di riferimento e lo stesso numero di campi da entrambe le parti.";
        if (Triggers.FirstOrDefault(t => string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Body)) is { } trigger) return $"Il trigger «{trigger.Name}» deve avere un nome e una definizione.";
        return null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Validate() is { } problem)
        {
            await _dialogs.ShowMessageAsync("Impossibile salvare", problem, isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(TableName))
        {
            var name = await _dialogs.PromptAsync("Salva tabella", "Nome della nuova tabella:", string.Empty, "Salva");
            if (string.IsNullOrWhiteSpace(name)) return;
            TableName = name.Trim();
        }
        var statements = BuildStatements();
        if (statements.Count == 0)
        {
            StatusMessage = "Nessuna modifica da salvare.";
            IsDirty = false;
            return;
        }
        IsLoading = true;
        try
        {
            await _databaseAccess.ExecuteStatementsAsync(Profile, Database, statements, CancellationToken.None);
        }
        catch (Exception exception)
        {
            IsLoading = false;
            var detail = exception is ScriptExecutionException script ? $"{script.InnerException?.Message}\n\nIstruzione:\n{script.Statement}" : exception.Message;
            await _dialogs.ShowMessageAsync("Salvataggio non riuscito", detail +
                (statements.Count > 1 ? "\n\nLe istruzioni precedenti sono già state eseguite: la struttura viene ricaricata dal server." : string.Empty), isError: true);
            if (_original is not null) await ReloadAfterSaveAsync();
            return;
        }
        var created = IsNew;
        await ReloadAfterSaveAsync();
        StatusMessage = created ? $"Tabella «{TableName}» creata." : $"Tabella «{TableName}» salvata ({statements.Count} istruzioni).";
        if (_saved is not null) await _saved(this);
    }

    private async Task ReloadAfterSaveAsync()
    {
        var design = await _databaseAccess.GetTableDesignAsync(Profile, Database, Schema, TableName.Trim(), CancellationToken.None);
        _loading = true;
        try { Apply(design, design); }
        finally
        {
            _loading = false;
            IsLoading = false;
            IsDirty = false;
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(CanReorder));
            if (SelectedSection == 6) RefreshPreview();
        }
    }

    // ───────────── Campi ─────────────

    private DesignFieldViewModel NewField()
    {
        var name = "campo";
        for (var i = 1; Fields.Any(f => f.Name == name); i++) name = $"campo{i}";
        return Track(new DesignFieldViewModel(this, new DesignColumn(null, name, IsMySql ? "varchar" : "varchar", 255)));
    }

    [RelayCommand]
    private void AddField()
    {
        var field = NewField();
        Fields.Add(field);
        SelectedField = field;
        SelectedSection = 0;
    }

    [RelayCommand]
    private void InsertField()
    {
        var field = NewField();
        var at = SelectedField is null ? 0 : Fields.IndexOf(SelectedField);
        if (!CanReorder) at = Fields.Count;
        Fields.Insert(Math.Max(0, at), field);
        SelectedField = field;
        SelectedSection = 0;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedField))]
    private void DeleteField()
    {
        if (SelectedField is not { } field) return;
        var at = Fields.IndexOf(field);
        Fields.Remove(field);
        RenumberKey();
        // Indici e chiavi esterne che usavano il campo: lo si toglie dall'elenco (e si elimina l'elemento se resta vuoto).
        foreach (var index in Indexes.ToArray())
        {
            var remaining = Split(index.Columns).Where(part => part.Split('(', ' ')[0] != field.Name).ToArray();
            if (remaining.Length == 0) Indexes.Remove(index);
            else index.Columns = string.Join(", ", remaining);
        }
        foreach (var key in ForeignKeys.Where(k => Split(k.Columns).Contains(field.Name)).ToArray()) ForeignKeys.Remove(key);
        SelectedField = Fields.Count == 0 ? null : Fields[Math.Min(at, Fields.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(HasSelectedField))]
    private void PrimaryKey()
    {
        if (SelectedField is not { } field) return;
        if (field.KeyOrder > 0) field.KeyOrder = 0;
        else
        {
            field.KeyOrder = Fields.Max(f => f.KeyOrder) + 1;
            field.NotNull = true;
        }
        RenumberKey();
    }

    private void RenumberKey()
    {
        var order = 1;
        foreach (var field in Fields.Where(f => f.KeyOrder > 0).OrderBy(f => f.KeyOrder).ToArray()) field.KeyOrder = order++;
    }

    private bool CanMoveUp() => SelectedField is not null && Fields.IndexOf(SelectedField) > 0 && (CanReorder || SelectedField.IsNewField);
    private bool CanMoveDown() => SelectedField is not null && Fields.IndexOf(SelectedField) < Fields.Count - 1 && (CanReorder || SelectedField.IsNewField);

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (SelectedField is not { } field) return;
        var at = Fields.IndexOf(field);
        Fields.Move(at, at + delta);
        SelectedField = field;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedFieldChanged(DesignFieldViewModel? value)
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    // ───────────── Indici, chiavi esterne, trigger ─────────────

    [RelayCommand]
    private void AddIndex()
    {
        var columns = SelectedField?.Name ?? string.Empty;
        var index = Track(new DesignIndexViewModel(new DesignIndex(null, UniqueName("idx_" + (columns.Length > 0 ? columns : TableName), Indexes.Select(i => i.Name)), columns.Length > 0 ? [columns] : [],
            "NORMAL", IsMySql ? "BTREE" : "btree")));
        Indexes.Add(index);
        SelectedIndex = index;
        SelectedSection = 1;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteIndex))]
    private void DeleteIndex()
    {
        if (SelectedIndex is { } index) Indexes.Remove(index);
        SelectedIndex = Indexes.FirstOrDefault();
    }

    private bool CanDeleteIndex() => SelectedIndex is not null;

    [RelayCommand]
    private void AddForeignKey()
    {
        var column = SelectedField?.Name ?? string.Empty;
        var key = Track(new DesignForeignKeyViewModel(new DesignForeignKey(null, UniqueName($"fk_{TableName}_{column}".TrimEnd('_'), ForeignKeys.Select(k => k.Name)),
            column.Length > 0 ? [column] : [], IsMySql ? Database : Schema, string.Empty, IsMySql ? ["id"] : ["id"])));
        ForeignKeys.Add(key);
        SelectedForeignKey = key;
        SelectedSection = 2;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteForeignKey))]
    private void DeleteForeignKey()
    {
        if (SelectedForeignKey is { } key) ForeignKeys.Remove(key);
        SelectedForeignKey = ForeignKeys.FirstOrDefault();
    }

    private bool CanDeleteForeignKey() => SelectedForeignKey is not null;

    [RelayCommand]
    private void AddTrigger()
    {
        var trigger = Track(new DesignTriggerViewModel(new DesignTrigger(null, UniqueName($"tr_{TableName}", Triggers.Select(t => t.Name)), "BEFORE", "INSERT",
            IsMySql ? "BEGIN\n  -- es. SET NEW.created_at = NOW();\nEND" : "FOR EACH ROW EXECUTE FUNCTION nome_funzione()")));
        Triggers.Add(trigger);
        SelectedTrigger = trigger;
        SelectedSection = 3;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTrigger))]
    private void DeleteTrigger()
    {
        if (SelectedTrigger is { } trigger) Triggers.Remove(trigger);
        SelectedTrigger = Triggers.FirstOrDefault();
    }

    private static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = baseName;
        for (var i = 2; taken.Contains(name); i++) name = $"{baseName}_{i}";
        return name;
    }

    [RelayCommand]
    private void ShowSection(string? section)
    {
        if (int.TryParse(section, out var index)) SelectedSection = index;
    }
}

public partial class DesignFieldViewModel : ObservableObject
{
    private readonly TableDesignerViewModel _owner;

    public DesignFieldViewModel(TableDesignerViewModel owner, DesignColumn column)
    {
        _owner = owner;
        OriginalName = column.OriginalName;
        _name = column.Name;
        _type = column.Type;
        _length = column.Length?.ToString();
        _decimals = column.Decimals?.ToString();
        _notNull = column.NotNull;
        _isVirtual = column.IsVirtual;
        _expression = column.Expression;
        _keyOrder = column.KeyOrder;
        _default = column.Default;
        _comment = column.Comment;
        _autoIncrement = column.AutoIncrement;
        _unsigned = column.Unsigned;
        _zerofill = column.Zerofill;
        _onUpdate = column.OnUpdate;
        _stored = column.StoredGenerated;
        Collation = column.Collation;
    }

    public string? OriginalName { get; }
    public string? Collation { get; }
    public bool IsNewField => OriginalName is null;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _type;
    [ObservableProperty] private string? _length;
    [ObservableProperty] private string? _decimals;
    [ObservableProperty] private bool _notNull;
    [ObservableProperty] private bool _isVirtual;
    [ObservableProperty] private string? _expression;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyLabel), nameof(IsKey))]
    private int _keyOrder;

    [ObservableProperty] private string? _default;
    [ObservableProperty] private string? _comment;
    [ObservableProperty] private bool _autoIncrement;
    [ObservableProperty] private bool _unsigned;
    [ObservableProperty] private bool _zerofill;
    [ObservableProperty] private string? _onUpdate;
    [ObservableProperty] private bool _stored;

    public bool IsKey => KeyOrder > 0;
    public string KeyLabel => KeyOrder > 0 ? KeyOrder.ToString() : string.Empty;

    partial void OnNameChanged(string? oldValue, string newValue) => _owner.OnFieldRenamed(oldValue ?? string.Empty, newValue);

    partial void OnIsVirtualChanged(bool value)
    {
        if (value && _owner.IsPostgres) Stored = true;
    }

    public DesignColumn ToModel() => new(
        OriginalName, Name.Trim(), Type.Trim(),
        int.TryParse(Length, out var length) && length > 0 ? length : null,
        int.TryParse(Decimals, out var decimals) && (decimals > 0 || int.TryParse(Length, out _)) ? decimals : null,
        NotNull, IsVirtual, IsVirtual ? Expression?.Trim() : null, KeyOrder,
        IsVirtual || string.IsNullOrWhiteSpace(Default) || Default.Trim().Equals("NULL", StringComparison.OrdinalIgnoreCase) && !NotNull ? null : Default.Trim(),
        string.IsNullOrEmpty(Comment) ? null : Comment,
        AutoIncrement, Unsigned, Zerofill,
        string.IsNullOrWhiteSpace(OnUpdate) ? null : OnUpdate.Trim(),
        IsVirtual && Stored, Collation);
}

public partial class DesignIndexViewModel : ObservableObject
{
    public DesignIndexViewModel(DesignIndex index)
    {
        OriginalName = index.OriginalName;
        IsConstraint = index.IsConstraint;
        _name = index.Name;
        _columns = string.Join(", ", index.Columns);
        _kind = index.Kind;
        _method = index.Method ?? string.Empty;
        _comment = index.Comment;
    }

    public string? OriginalName { get; }
    public bool IsConstraint { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _columns;
    [ObservableProperty] private string _kind;
    [ObservableProperty] private string _method;
    [ObservableProperty] private string? _comment;

    public DesignIndex ToModel() => new(OriginalName, Name.Trim(), TableDesignerViewModel.Split(Columns), Kind, string.IsNullOrWhiteSpace(Method) ? null : Method,
        string.IsNullOrEmpty(Comment) ? null : Comment, IsConstraint);
}

public partial class DesignForeignKeyViewModel : ObservableObject
{
    public DesignForeignKeyViewModel(DesignForeignKey key)
    {
        OriginalName = key.OriginalName;
        _name = key.Name;
        _columns = string.Join(", ", key.Columns);
        _referencedSchema = key.ReferencedSchema;
        _referencedTable = key.ReferencedTable;
        _referencedColumns = string.Join(", ", key.ReferencedColumns);
        _onDelete = key.OnDelete;
        _onUpdate = key.OnUpdate;
    }

    public string? OriginalName { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _columns;
    [ObservableProperty] private string _referencedSchema;
    [ObservableProperty] private string _referencedTable;
    [ObservableProperty] private string _referencedColumns;
    [ObservableProperty] private string _onDelete;
    [ObservableProperty] private string _onUpdate;

    public DesignForeignKey ToModel(string schema) => new(OriginalName, Name.Trim(), TableDesignerViewModel.Split(Columns),
        string.IsNullOrWhiteSpace(ReferencedSchema) ? schema : ReferencedSchema.Trim(), ReferencedTable.Trim(), TableDesignerViewModel.Split(ReferencedColumns), OnDelete, OnUpdate);
}

public partial class DesignTriggerViewModel : ObservableObject
{
    public DesignTriggerViewModel(DesignTrigger trigger)
    {
        OriginalName = trigger.OriginalName;
        _name = trigger.Name;
        _timing = trigger.Timing;
        _events = trigger.Events;
        _body = trigger.Body;
    }

    public string? OriginalName { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _timing;
    [ObservableProperty] private string _events;
    [ObservableProperty] private string _body;

    public DesignTrigger ToModel() => new(OriginalName, Name.Trim(), Timing, Events, Body);
}
