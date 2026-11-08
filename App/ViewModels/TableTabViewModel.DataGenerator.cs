using CommunityToolkit.Mvvm.Input;
using Navifox.App.Views;

namespace Navifox.App.ViewModels;

public partial class TableTabViewModel
{
    [RelayCommand]
    private async Task GenerateDataAsync()
    {
        if (IsView || IsBusy) return;
        if (await Services.SqlGuard.BlockIfReadOnlyAsync(_dialogs, _profile, "Generazione di dati di prova.")) return;
        if (HasPendingChanges || IsTransaction)
        {
            await _dialogs.ShowMessageAsync("Genera dati di prova", "Salva o annulla le modifiche e concludi l’eventuale transazione prima di generare nuovi dati.");
            return;
        }
        var model = new DataGeneratorViewModel(_databaseAccess, _dialogs, _profile, Database, Schema, TableName);
        await model.InitializeAsync();
        await _dialogs.ShowDialogAsync(new DataGeneratorWindow { DataContext = model });
        if (model.GeneratedRows > 0) await LoadAsync();
    }
}
