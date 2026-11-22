using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public partial class DataTransferViewModel
{
    public ObservableCollection<string> SourceSchemas { get; } = [];
    public ObservableCollection<string> TargetSchemas { get; } = [];
    public bool SourceHasSchemas => SourceProfile?.Kind == DatabaseKind.PostgreSql;
    public bool TargetHasSchemas => TargetProfile?.Kind == DatabaseKind.PostgreSql;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SourceSubtitle))] private string? _sourceSchema;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TargetSubtitle))] private string? _targetSchema;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSourceLoading), nameof(CanStart))] private bool _isSourceDatabasesLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSourceLoading), nameof(CanStart))] private bool _isSourceSchemasLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSourceLoading), nameof(CanStart))] private bool _isTablesLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsTargetLoading), nameof(CanStart))] private bool _isTargetDatabasesLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsTargetLoading), nameof(CanStart))] private bool _isTargetSchemasLoading;
    public bool IsSourceLoading => IsSourceDatabasesLoading || IsSourceSchemasLoading || IsTablesLoading;
    public bool IsTargetLoading => IsTargetDatabasesLoading || IsTargetSchemasLoading;

    private string? _pendingSourceSchema;
    private string? _pendingTargetSchema;
    private int _sourceSchemaLoads;
    private int _targetSchemaLoads;
    private int _tableLoads;
    private string? _loadedTableScope;
    private string TableScope() => $"{SourceProfile?.Id}|{SourceDatabase}|{SourceSchema}";

    private static string DatabaseLabel(ConnectionProfile? profile, string? database, string? schema) =>
        profile?.Kind == DatabaseKind.PostgreSql && schema is not null ? $"{database} / {schema}" : database ?? "Database";

    private static string ObjectLabel(ConnectionProfile? profile, string? schema, string table) =>
        profile?.Kind == DatabaseKind.PostgreSql ? $"{schema}.{table}" : table;

    partial void OnSourceSchemaChanged(string? value) => _ = LoadTablesAsync(SourceDatabase);

    private async Task LoadSchemasAsync(bool source)
    {
        var profile = source ? SourceProfile : TargetProfile;
        var database = source ? SourceDatabase : TargetDatabase;
        var keep = source ? _pendingSourceSchema ?? SourceSchema : _pendingTargetSchema ?? TargetSchema;
        var list = source ? SourceSchemas : TargetSchemas;
        var generation = source ? ++_sourceSchemaLoads : ++_targetSchemaLoads;
        if (source) { IsSourceSchemasLoading = true; SourceSchema = null; }
        else { IsTargetSchemasLoading = true; TargetSchema = null; }
        list.Clear();
        try
        {
            if (profile is null || database is null) return;
            var names = profile.Kind == DatabaseKind.PostgreSql
                ? await _databaseAccess.ListSchemasAsync(profile, database, CancellationToken.None)
                : [profile.Kind.DefaultSchema(database)];
            if (generation != (source ? _sourceSchemaLoads : _targetSchemaLoads)) return;
            foreach (var schema in names) list.Add(schema);
            var selected = keep is not null && list.Contains(keep) ? keep
                : list.Contains(profile.Kind.DefaultSchema(database)) ? profile.Kind.DefaultSchema(database) : list.FirstOrDefault();
            if (source) { _pendingSourceSchema = null; SourceSchema = selected; }
            else { _pendingTargetSchema = null; TargetSchema = selected; }
            if (selected is null) StatusMessage = "Nessuno schema disponibile nel database scelto.";
        }
        catch (Exception exception)
        {
            if (generation == (source ? _sourceSchemaLoads : _targetSchemaLoads)) StatusMessage = $"{profile?.Name}/{database}: {exception.Message}";
        }
        finally
        {
            if (generation == (source ? _sourceSchemaLoads : _targetSchemaLoads))
            {
                if (source) IsSourceSchemasLoading = false; else IsTargetSchemasLoading = false;
                TryJumpToSummary();
            }
        }
    }

    private bool ValidateEndpoints()
    {
        if (IsSourceLoading || (!TargetIsFile && IsTargetLoading)) { StatusMessage = "Attendi il caricamento di database, schemi e tabelle."; return false; }
        if (SourceProfile is null || SourceDatabase is null) { StatusMessage = "Scegli la connessione e il database di origine."; return false; }
        if (SourceSchema is null || !SourceSchemas.Contains(SourceSchema)) { StatusMessage = "Scegli lo schema di origine."; return false; }
        if (TargetIsFile)
        {
            if (!string.IsNullOrWhiteSpace(TargetFile)) return true;
            StatusMessage = "Scegli il file di destinazione.";
            return false;
        }
        if (TargetProfile is null || TargetDatabase is null) { StatusMessage = "Scegli la connessione e il database di destinazione."; return false; }
        if (TargetSchema is null || !TargetSchemas.Contains(TargetSchema)) { StatusMessage = "Scegli lo schema di destinazione."; return false; }
        if (SourceProfile.Kind != TargetProfile.Kind) { StatusMessage = "Origine e destinazione devono essere dello stesso tipo di database."; return false; }
        if (!SourceProfile.IsSameDatabase(SourceDatabase, SourceSchema, TargetProfile, TargetDatabase, TargetSchema)) return true;
        StatusMessage = "Origine e destinazione coincidono. Scegli un altro database o schema.";
        return false;
    }

    private void TryJumpToSummary()
    {
        if (!_jumpToSummary || IsSourceLoading || (!TargetIsFile && IsTargetLoading) || _loadedTableScope != TableScope()) return;
        if (!ValidateEndpoints()) return;
        _jumpToSummary = false;
        Step = 3;
    }
}
