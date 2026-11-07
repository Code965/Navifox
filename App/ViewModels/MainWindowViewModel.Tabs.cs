using CommunityToolkit.Mvvm.Input;

namespace Navifox.App.ViewModels;

/// <summary>Menu delle schede: chiudi le altre / a destra, sposta, duplica la query.</summary>
public partial class MainWindowViewModel
{
    public IReadOnlyList<ContextAction> GetTabActions(WorkspaceTabViewModel tab)
    {
        var index = OpenTabs.IndexOf(tab);
        var actions = new List<ContextAction>();
        if (tab.CanClose) actions.Add(new("Chiudi", CloseTabCommand, tab, "x"));
        actions.Add(new("Chiudi le altre schede", CloseOtherTabsCommand, tab, null, OpenTabs.Count(t => t.CanClose && t != tab) > 0));
        actions.Add(new("Chiudi le schede a destra", CloseTabsToRightCommand, tab, null, OpenTabs.Skip(index + 1).Any(t => t.CanClose)));
        actions.Add(new("Chiudi tutte", CloseAllTabsCommand, null, null, OpenTabs.Any(t => t.CanClose)));
        actions.Add(ContextAction.Separator);
        actions.Add(new("Sposta a sinistra", MoveTabCommand, new TabMove(tab, -1), "arrow-up", index > 1));
        actions.Add(new("Sposta a destra", MoveTabCommand, new TabMove(tab, 1), "arrow-down", tab.CanClose && index < OpenTabs.Count - 1));
        if (tab is QueryTabViewModel { IsConsole: false })
        {
            actions.Add(ContextAction.Separator);
            actions.Add(new("Duplica query", DuplicateQueryTabCommand, tab, "copy"));
        }
        if (tab is TableTabViewModel or QueryTabViewModel)
            actions.Add(new("Aggiungi ai preferiti", AddFavoriteCommand, null, "star"));
        return actions;
    }

    public sealed record TabMove(WorkspaceTabViewModel Tab, int Delta);

    [RelayCommand]
    private void CloseOtherTabs(WorkspaceTabViewModel? keep)
    {
        foreach (var tab in OpenTabs.Where(t => t.CanClose && t != keep).ToArray()) CloseTab(tab);
        if (keep is not null) SelectedTab = keep;
    }

    [RelayCommand]
    private void CloseTabsToRight(WorkspaceTabViewModel? from)
    {
        if (from is null) return;
        foreach (var tab in OpenTabs.Skip(OpenTabs.IndexOf(from) + 1).Where(t => t.CanClose).ToArray()) CloseTab(tab);
        SelectedTab = from;
    }

    /// <summary>Riordina le schede (la scheda «Oggetti» resta sempre la prima). Anche con Ctrl+Maiusc+PagSu/PagGiù.</summary>
    [RelayCommand]
    private void MoveTab(TabMove? move)
    {
        if (move is null || !move.Tab.CanClose) return;
        var index = OpenTabs.IndexOf(move.Tab);
        var target = Math.Clamp(index + move.Delta, 1, OpenTabs.Count - 1);
        if (target == index) return;
        OpenTabs.Move(index, target);
        SelectedTab = move.Tab;
    }

    [RelayCommand]
    private void MoveSelectedTab(string? direction)
    {
        if (SelectedTab is { } tab) MoveTab(new TabMove(tab, direction == "left" ? -1 : 1));
    }

    [RelayCommand]
    private void DuplicateQueryTab(WorkspaceTabViewModel? tab)
    {
        if (tab is not QueryTabViewModel { SelectedProfile: { } profile } query) return;
        var copy = new QueryTabViewModel(_databaseAccess, profile, query.SelectedDatabase, false, Profiles.ToArray(), _dialogs) { SqlText = query.SqlText };
        OpenTabs.Insert(OpenTabs.IndexOf(query) + 1, copy);
        SelectedTab = copy;
    }

    /// <summary>Ctrl+Tab / Ctrl+Maiusc+Tab: scheda successiva / precedente.</summary>
    [RelayCommand]
    private void NextTab(string? direction)
    {
        if (OpenTabs.Count < 2 || SelectedTab is null) return;
        var index = OpenTabs.IndexOf(SelectedTab) + (direction == "prev" ? -1 : 1);
        SelectedTab = OpenTabs[(index + OpenTabs.Count) % OpenTabs.Count];
    }
}
