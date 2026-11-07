using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MosaicDB.App.Services;
using MosaicDB.Core.Models;

namespace MosaicDB.App.ViewModels;

public enum InfoKind { Connection, Database, Schema, Table, View }

/// <summary>Oggetto mostrato nel pannello informazioni.</summary>
public sealed record InfoTarget(ConnectionProfile Profile, InfoKind Kind, string? Database = null, string? Schema = null, string? Name = null, int? ObjectCount = null, bool IsConnected = true)
{
    public string Key => $"{Profile.Id}|{Kind}|{Database}|{Schema}|{Name}";
}

public sealed record PropertyItem(string Label, string Value);

/// <summary>Pannello laterale destro: proprietà (ⓘ) e DDL dell'oggetto selezionato.</summary>
public partial class InfoPanelViewModel(IDatabaseAccessService databaseAccess) : ObservableObject
{
    private InfoTarget? _target;
    private CancellationTokenSource? _loading;
    private string? _ddlKey;

    public ObservableCollection<PropertyItem> Properties { get; } = [];

    [ObservableProperty] private string _title = "Nessun oggetto selezionato";
    [ObservableProperty] private string _subtitle = "Seleziona una connessione, un database o una tabella.";
    [ObservableProperty] private string _iconKind = "info";
    [ObservableProperty] private bool _hasTarget;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInfoTab))]
    private bool _isDdlTab;
    [ObservableProperty] private string _ddl = string.Empty;
    [ObservableProperty] private bool _hasDdl;
    [ObservableProperty] private bool _isDdlLoading;

    public bool IsInfoTab => !IsDdlTab;

    [RelayCommand]
    private void ShowInfo() => IsDdlTab = false;

    [RelayCommand]
    private async Task ShowDdlAsync()
    {
        IsDdlTab = true;
        await LoadDdlAsync();
    }

    public async Task ShowAsync(InfoTarget? target)
    {
        if (target == _target) return;
        _target = target;
        _loading?.Cancel();
        var source = _loading = new CancellationTokenSource();
        Properties.Clear();
        Message = string.Empty;
        Ddl = string.Empty;
        _ddlKey = null;
        if (target is null)
        {
            HasTarget = false;
            HasDdl = false;
            IconKind = "info";
            Title = "Nessun oggetto selezionato";
            Subtitle = "Seleziona una connessione, un database o una tabella.";
            return;
        }

        HasTarget = true;
        HasDdl = target.Kind is InfoKind.Table or InfoKind.View;
        if (!HasDdl) IsDdlTab = false;
        (IconKind, Title, Subtitle) = target.Kind switch
        {
            InfoKind.Connection => ("server", target.Profile.Name, MosaicDB.App.Controls.DialectVisuals.Summary(target.Profile)),
            InfoKind.Database => ("database", target.Database!, target.ObjectCount is { } n ? $"{n} {(n == 1 ? "tabella" : "tabelle")}" : "Database"),
            InfoKind.Schema => ("layers", target.Schema!, target.ObjectCount is { } n ? $"Schema · {n} {(n == 1 ? "tabella" : "tabelle")}" : "Schema"),
            InfoKind.View => ("eye", target.Name!, "Vista"),
            _ => ("table", target.Name!, "Tabella"),
        };

        if (target.Kind == InfoKind.Connection)
        {
            var profile = target.Profile;
            Properties.Add(new("Tipo", MosaicDB.App.Controls.DialectVisuals.DialectName(profile)));
            Properties.Add(new("Host", profile.Host));
            Properties.Add(new("Porta", profile.EffectivePort.ToString()));
            Properties.Add(new("Utente", string.IsNullOrWhiteSpace(profile.UserName) ? "—" : profile.UserName));
            Properties.Add(new("Database iniziale", string.IsNullOrWhiteSpace(profile.DefaultDatabase) ? "—" : profile.DefaultDatabase));
            Properties.Add(new("SSL", profile.SslMode));
            Properties.Add(new("Tunnel SSH", profile.UseSshTunnel ? $"{profile.SshUserName}@{profile.SshHost}:{profile.SshPort}" : "No"));
            if (!target.IsConnected)
            {
                Message = "Apri la connessione per vedere la versione del server.";
                return;
            }
        }

        // Breve attesa: evita interrogazioni inutili quando si scorre rapidamente l'albero.
        try { await Task.Delay(150, source.Token); }
        catch (OperationCanceledException) { return; }

        IsLoading = true;
        try
        {
            var properties = target.Kind switch
            {
                InfoKind.Connection => await databaseAccess.GetPropertiesAsync(target.Profile, null, null, null, source.Token),
                InfoKind.Database or InfoKind.Schema => await databaseAccess.GetPropertiesAsync(target.Profile, target.Database, null, null, source.Token),
                _ => await databaseAccess.GetPropertiesAsync(target.Profile, target.Database, target.Schema, target.Name, source.Token),
            };
            if (source.IsCancellationRequested) return;
            if (target.Kind == InfoKind.Schema) Properties.Add(new("Database", target.Database!));
            foreach (var (label, value) in properties) Properties.Add(new(label, value));
            if (target.Kind == InfoKind.Database && target.ObjectCount is null && properties.FirstOrDefault(p => p.Key == "Tabelle").Value is { } count)
                Subtitle = $"{count} tabelle";
            if (IsDdlTab) await LoadDdlAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!source.IsCancellationRequested) Message = $"Informazioni non disponibili: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loading, source)) IsLoading = false;
        }
    }

    public Task RefreshAsync()
    {
        var target = _target;
        _target = null;
        return ShowAsync(target);
    }

    private async Task LoadDdlAsync()
    {
        var target = _target;
        if (target is null || !HasDdl || _ddlKey == target.Key) return;
        _ddlKey = target.Key;
        IsDdlLoading = true;
        try
        {
            var ddl = await databaseAccess.GetDdlTextAsync(target.Profile, target.Database!, target.Schema!, target.Name!, target.Kind == InfoKind.View, CancellationToken.None);
            if (_target?.Key == target.Key) Ddl = ddl;
        }
        catch (Exception exception)
        {
            if (_target?.Key == target.Key) Ddl = $"-- DDL non disponibile: {exception.Message}";
            _ddlKey = null;
        }
        finally { IsDdlLoading = false; }
    }
}
