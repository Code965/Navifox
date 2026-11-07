using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Strumenti: Modello, Server Monitor, Trova, Cronologia, Trasferimento dati, Opzioni, Automazione.</summary>
public partial class MainWindowViewModel
{
    /// <summary>Connessione, database e schema del contesto corrente (scheda tabella aperta o nodo selezionato).</summary>
    private (ConnectionProfile Profile, string Database, string Schema)? CurrentDatabaseContext(TreeNodeViewModel? node = null)
    {
        if (node is null && SelectedTab is TableTabViewModel table && Profiles.FirstOrDefault(p => p.Id == table.ProfileId) is { } tableProfile)
            return (tableProfile, table.Database, table.Schema);
        node ??= SelectedNode;
        if (node?.Profile is not { } profile || node.Database is not { } database) return null;
        var schema = node.Schema ?? profile.Kind.DefaultSchema(database);
        return (profile, database, schema);
    }

    private void OpenOrSelect<T>(Func<T, bool> match, Func<T> create) where T : WorkspaceTabViewModel
    {
        var existing = OpenTabs.OfType<T>().FirstOrDefault(match);
        if (existing is null)
        {
            existing = create();
            OpenTabs.Add(existing);
        }
        SelectedTab = existing;
    }

    [RelayCommand]
    private async Task OpenModelAsync(TreeNodeViewModel? node)
    {
        if (CurrentDatabaseContext(node) is not { } context)
        {
            StatusMessage = "Seleziona un database nell’albero per aprire il modello relazionale.";
            return;
        }
        var created = false;
        OpenOrSelect<ModelTabViewModel>(
            tab => tab.Database == context.Database && tab.Schema == context.Schema && tab.TabTitle.EndsWith($"({context.Profile.Name})", StringComparison.Ordinal),
            () =>
            {
                created = true;
                return new ModelTabViewModel(_databaseAccess, context.Profile, context.Database, context.Schema,
                    table => OpenTableAsync(context.Profile, context.Database, context.Schema, table))
                {
                    Dialogs = _dialogs,
                    DesignTable = table => OpenDesigner(context.Profile, context.Database, context.Schema, table),
                    OpenScript = sql => OpenQueryFor(context.Profile, context.Database)(sql, false),
                };
            });
        if (created && SelectedTab is ModelTabViewModel model) await model.LoadAsync();
    }

    [RelayCommand]
    private void OpenServerMonitor(TreeNodeViewModel? node)
    {
        var focus = (node ?? SelectedNode) is { Profile: { } profile } ? profile.Id : (Guid?)null;
        OpenOrSelect<ServerMonitorViewModel>(_ => true, () => new ServerMonitorViewModel(_databaseAccess, _dialogs, Profiles.ToArray(), focus)
        {
            SelectedInterval = AppSettings.Current.MonitorInterval
        });
    }

    [RelayCommand]
    private void OpenFind(TreeNodeViewModel? node)
    {
        if (CurrentDatabaseContext(node) is not { } context)
        {
            StatusMessage = "Seleziona un database nell’albero per cercare.";
            return;
        }
        OpenOrSelect<FindTabViewModel>(tab => tab.Database == context.Database && tab.Schema == context.Schema,
            () => new FindTabViewModel(_databaseAccess, context.Profile, context.Database, context.Schema, async (result, text) =>
            {
                await OpenTableAsync(context.Profile, context.Database, context.Schema, result.Table);
                if (result.Kind == "dati" && SelectedTab is TableTabViewModel table) await table.FilterContainsAsync(result.Column, text);
            }));
    }

    [RelayCommand]
    private void OpenHistory()
    {
        OpenOrSelect<HistoryTabViewModel>(_ => true, () => new HistoryTabViewModel(entry =>
        {
            var profile = Profiles.FirstOrDefault(p => p.Id == entry.ProfileId) ??
                (entry.ProfileId == Guid.Empty ? Profiles.FirstOrDefault(p => p.Name == entry.Connection) : null);
            if (profile is null) { StatusMessage = "La connessione originale non è disponibile. Puoi copiare o esportare l’attività."; return; }
            var tab = new QueryTabViewModel(_databaseAccess, profile, entry.Database, false, Profiles.ToArray(), _dialogs) { SqlText = entry.Sql };
            OpenTabs.Add(tab);
            SelectedTab = tab;
        }, _dialogs));
    }

    [RelayCommand]
    private async Task OpenDataTransferAsync(TreeNodeViewModel? node)
    {
        if (Profiles.Count == 0) return;
        var context = CurrentDatabaseContext(node);
        await _dialogs.ShowDataTransferAsync(new DataTransferViewModel(_databaseAccess, Profiles.ToArray(), context?.Profile, context?.Database, _dialogs));
    }

    [RelayCommand]
    private async Task OpenOptionsAsync()
    {
        if (!await _dialogs.ShowOptionsAsync(new OptionsViewModel())) return;
        ApplySettings();
        StatusMessage = "Opzioni salvate.";
    }

    /// <summary>Applica le preferenze salvate (all'avvio e dopo «Opzioni»).</summary>
    public void ApplySettings()
    {
        var settings = AppSettings.Current;
        _themeService.Apply(settings.Theme);
        IsDarkTheme = _themeService.IsDark;
        TableTabViewModel.SetDefaults(settings.LimitRecords, settings.PageSize);
    }

    [RelayCommand]
    private void OpenAutomation() =>
        OpenOrSelect<AutomationTabViewModel>(_ => true, () => new AutomationTabViewModel(_automation, _databaseAccess, _dialogs, Profiles.ToArray()));

    [RelayCommand]
    private void CloseCurrentTab()
    {
        if (SelectedTab is { CanClose: true } tab) CloseTab(tab);
    }

    [RelayCommand]
    private void CloseAllTabs()
    {
        foreach (var tab in OpenTabs.Where(t => t.CanClose).ToArray()) CloseTab(tab);
        SelectedTab = ObjectsTab;
    }
}
