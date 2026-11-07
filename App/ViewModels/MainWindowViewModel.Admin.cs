using CommunityToolkit.Mvvm.Input;

namespace Navifox.App.ViewModels;

/// <summary>Sincronizzazione struttura/dati e Utenti e privilegi.</summary>
public partial class MainWindowViewModel
{
    [RelayCommand]
    private Task OpenStructureSyncAsync(TreeNodeViewModel? node) => OpenSyncAsync(node, data: false);

    [RelayCommand]
    private Task OpenDataSyncAsync(TreeNodeViewModel? node) => OpenSyncAsync(node, data: true);

    private Task OpenSyncAsync(TreeNodeViewModel? node, bool data)
    {
        if (Profiles.Count == 0)
        {
            StatusMessage = "Crea prima una connessione.";
            return Task.CompletedTask;
        }
        var context = CurrentDatabaseContext(node);
        var tab = new SyncTabViewModel(_databaseAccess, _dialogs, Profiles.ToArray(), data, context?.Profile, context?.Database);
        OpenTabs.Add(tab);
        SelectedTab = tab;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenUsers(TreeNodeViewModel? node)
    {
        var connection = (node ?? SelectedNode) is { } n ? ConnectionOf(n) : null;
        var profile = connection?.Profile ?? CurrentDatabaseContext()?.Profile ?? Profiles.FirstOrDefault();
        if (profile is null)
        {
            StatusMessage = "Seleziona una connessione.";
            return;
        }
        OpenOrSelect<UsersTabViewModel>(tab => tab.Profile.Id == profile.Id, () => new UsersTabViewModel(_databaseAccess, _dialogs, profile));
    }
}
