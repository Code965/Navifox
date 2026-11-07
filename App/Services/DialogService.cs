using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Navifox.App.ViewModels;
using Navifox.App.Views;

namespace Navifox.App.Services;

public sealed record FileFilter(string Name, params string[] Patterns);

/// <summary>Finestre modali e selettori di file in stile Navifox.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false);
    Task ShowMessageAsync(string title, string message, bool isError = false);
    Task<bool> ShowDatabaseDialogAsync(DatabaseDialogViewModel viewModel);
    Task<string?> PickOpenFileAsync(string title, params FileFilter[] filters);
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string defaultExtension, params FileFilter[] filters);
    Task<bool> ShowDialogAsync(Window dialog);
    Task ShowExportWizardAsync(ExportWizardViewModel viewModel);
    Task<string?> PickFolderAsync(string title);
    Task<string?> PromptAsync(string title, string message, string value, string confirmText = "OK", bool isPassword = false);
    Task<bool> ShowQueryBuilderAsync(QueryBuilderViewModel viewModel);
    Task ShowDataTransferAsync(DataTransferViewModel viewModel);
    Task<bool> ShowOptionsAsync(OptionsViewModel viewModel);
    Task<bool> ShowImportWizardAsync(ImportWizardViewModel viewModel);
}

public sealed class DialogService(IWindowLocator windowLocator) : IDialogService
{
    private Window Owner => windowLocator.MainWindow ?? throw new InvalidOperationException("La finestra principale non è pronta.");

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false)
    {
        var dialog = new MessageDialog { DataContext = new MessageDialogModel { Title = title, Message = message, ConfirmText = confirmText, CancelText = "Annulla", IsDanger = danger } };
        return await dialog.ShowDialog<bool>(Owner);
    }

    public async Task ShowMessageAsync(string title, string message, bool isError = false)
    {
        var dialog = new MessageDialog { DataContext = new MessageDialogModel { Title = title, Message = message, IsError = isError } };
        await dialog.ShowDialog<bool>(Owner);
    }

    public async Task<bool> ShowDatabaseDialogAsync(DatabaseDialogViewModel viewModel)
    {
        var dialog = new DatabaseDialog { DataContext = viewModel };
        viewModel.CloseRequested += result => dialog.Close(result);
        return await dialog.ShowDialog<bool>(Owner);
    }

    public async Task<bool> ShowDialogAsync(Window dialog) => await dialog.ShowDialog<bool>(Owner);

    public async Task ShowExportWizardAsync(ExportWizardViewModel viewModel)
    {
        var dialog = new ExportWizard { DataContext = viewModel };
        viewModel.CloseRequested += () => dialog.Close();
        dialog.Closing += (_, _) => { if (viewModel.IsRunning) viewModel.CloseCommand.Execute(null); };
        await dialog.ShowDialog(Owner);
    }

    public async Task<bool> ShowImportWizardAsync(ImportWizardViewModel viewModel)
    {
        var dialog = new ImportWizard { DataContext = viewModel };
        viewModel.CloseRequested += imported => dialog.Close(imported);
        await dialog.ShowDialog<bool>(Owner);
        return viewModel.ImportedRows > 0;
    }

    public async Task<bool> ShowQueryBuilderAsync(QueryBuilderViewModel viewModel)
    {
        var dialog = new QueryBuilderWindow { DataContext = viewModel };
        viewModel.CloseRequested += result => dialog.Close(result);
        return await dialog.ShowDialog<bool>(Owner);
    }

    public async Task ShowDataTransferAsync(DataTransferViewModel viewModel)
    {
        var dialog = new DataTransferWindow { DataContext = viewModel };
        viewModel.CloseRequested += () => dialog.Close();
        await dialog.ShowDialog(Owner);
    }

    public async Task<bool> ShowOptionsAsync(OptionsViewModel viewModel)
    {
        var dialog = new OptionsWindow { DataContext = viewModel };
        viewModel.CloseRequested += result => dialog.Close(result);
        return await dialog.ShowDialog<bool>(Owner);
    }

    public async Task<string?> PromptAsync(string title, string message, string value, string confirmText = "OK", bool isPassword = false) =>
        await new PromptDialog(title, message, value, confirmText, isPassword).ShowDialog<string?>(Owner);

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(string title, params FileFilter[] filters)
    {
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ToFileTypes(filters)
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string defaultExtension, params FileFilter[] filters)
    {
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = defaultExtension,
            ShowOverwritePrompt = true,
            FileTypeChoices = ToFileTypes(filters)
        });
        return file?.TryGetLocalPath();
    }

    private static List<FilePickerFileType> ToFileTypes(IEnumerable<FileFilter> filters) =>
        filters.Select(filter => new FilePickerFileType(filter.Name) { Patterns = filter.Patterns }).ToList();
}
