using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

public partial class MainWindowViewModel
{
    private bool _importWizardOpen;
    [RelayCommand]
    private Task ImportIntoNewTableAsync(TreeNodeViewModel? node) => OpenImportWizardAsync(node ?? ObjectsTab.Source);

    public async Task ImportFileIntoObjectsAsync(ObjectRowViewModel? row, string path)
    {
        if (ObjectsTab.Source is not { } category) return;
        var table = row is null ? null : category.Parent?.SelfAndDescendants().FirstOrDefault(n => n.Kind == TreeNodeKind.Table && n.Name == row.Name && n.Category != "Viste");
        if (row is not null && table is null) { StatusMessage = "Trascina il file su una tabella, non su una vista."; return; }
        await OpenImportWizardAsync(table ?? category, path);
    }

    public async Task OpenImportWizardAsync(TreeNodeViewModel? node, string? path = null)
    {
        node ??= ObjectsTab.Source;
        if (_importWizardOpen || CurrentDatabaseContext(node) is not { } context) return;
        if (node?.Category == "Viste") { StatusMessage = "Non è possibile importare in una vista."; return; }
        if (await SqlGuard.BlockIfReadOnlyAsync(_dialogs, context.Profile, "Importazione da file.")) return;
        _importWizardOpen = true;
        try
        {
            var table = node?.Kind == TreeNodeKind.Table ? node.Name : null;
            var columns = table is null ? null : await _databaseAccess.GetColumnsAsync(context.Profile, context.Database, context.Schema, table, CancellationToken.None);
            var wizard = new ImportWizardViewModel(_databaseAccess, _dialogs, context.Profile, context.Database, context.Schema, table, columns);
            await wizard.LoadTargetsAsync();
            if (path is not null) wizard.SetSourceFile(path);
            await _dialogs.ShowImportWizardAsync(wizard);
            if (wizard.ImportedRows > 0 || wizard.TableCreated)
            {
                await RefreshTablesOfAsync(context.Profile, context.Database, context.Schema);
                foreach (var open in OpenTabs.OfType<TableTabViewModel>().Where(t => t.ProfileId == context.Profile.Id && t.Database == context.Database && t.Schema == context.Schema && t.TableName == wizard.TargetTable.Trim()).ToArray())
                    await open.LoadAsync();
            }
            StatusMessage = wizard.ProgressText.Length > 0 ? wizard.ProgressText : wizard.ErrorMessage;
        }
        catch (Exception error) { await ReportErrorAsync("Importazione da file", error); }
        finally { _importWizardOpen = false; }
    }
}
