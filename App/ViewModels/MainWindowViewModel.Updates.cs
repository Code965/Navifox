using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MosaicDB.App.Services;

namespace MosaicDB.App.ViewModels;

/// <summary>Aggiornamenti: controllo all'avvio, Aiuto › Verifica aggiornamenti, pulsante «Riavvia e aggiorna».</summary>
public partial class MainWindowViewModel
{
    private UpdateService Updates => App.Services.GetRequiredService<UpdateService>();

    public string AppVersion => UpdateService.CurrentVersion;

    [ObservableProperty] private bool _isUpdateReady;
    [ObservableProperty] private string _updateLabel = string.Empty;
    [ObservableProperty] private bool _isCheckingUpdates;

    /// <summary>Controllo silenzioso all'avvio: se c'è una versione nuova la scarica e mostra il pulsante nella barra di stato.</summary>
    public async Task CheckUpdatesOnStartupAsync()
    {
        if (!AppSettings.Current.CheckUpdatesOnStartup || !Updates.IsInstalled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (await Task.Run(() => Updates.CheckAndDownloadAsync()) is not { } version) return;
            ShowUpdateReady(version);
            // Installazioni non presidiate: NAVIFOX_UPDATE_AUTOAPPLY=1 applica subito l'aggiornamento e riavvia.
            if (Environment.GetEnvironmentVariable("NAVIFOX_UPDATE_AUTOAPPLY") == "1") Updates.ApplyAndRestart();
        }
        catch
        {
            // Sorgente non raggiungibile (offline, VPN, condivisione assente): si riprova al prossimo avvio.
        }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsCheckingUpdates) return;
        if (IsUpdateReady)
        {
            await ApplyUpdateAsync();
            return;
        }
        if (!Updates.IsInstalled)
        {
            await _dialogs.ShowMessageAsync("Aggiornamenti",
                $"Questa copia di Navifox (v{AppVersion}) non è stata installata con il setup, quindi non può aggiornarsi da sola.\n\n" +
                "Installa Navifox con «Navifox-win-Setup.exe» per ricevere gli aggiornamenti automatici.");
            return;
        }
        IsCheckingUpdates = true;
        StatusMessage = "Ricerca aggiornamenti…";
        try
        {
            var version = await Task.Run(() => Updates.CheckAndDownloadAsync(p => Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusMessage = $"Download aggiornamento… {p}%")));
            if (version is null)
            {
                StatusMessage = $"Navifox è aggiornato (v{AppVersion}).";
                await _dialogs.ShowMessageAsync("Aggiornamenti", $"Stai usando l'ultima versione di Navifox (v{AppVersion}).");
                return;
            }
            ShowUpdateReady(version);
            await ApplyUpdateAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "Impossibile verificare gli aggiornamenti.";
            await _dialogs.ShowMessageAsync("Aggiornamenti",
                $"Non è stato possibile raggiungere la sorgente degli aggiornamenti:\n{UpdateService.EffectiveSource}\n\n{ex.Message}\n\n" +
                "Puoi cambiarla in Strumenti › Opzioni › Aggiornamenti.", isError: true);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (!IsUpdateReady) return;
        if (!await _dialogs.ConfirmAsync("Aggiornamento disponibile",
                $"Navifox {Updates.PendingVersion} è pronto (versione attuale {AppVersion}).\n\n" +
                "Navifox verrà chiuso, aggiornato e riaperto. Le query non salvate andranno perse.", "Riavvia e aggiorna")) return;
        Updates.ApplyAndRestart();
    }

    [RelayCommand]
    private Task ShowAboutAsync() => _dialogs.ShowMessageAsync("Informazioni su Navifox",
        $"NaviFox Database Studio\nVersione {AppVersion}\n\n" +
        $"Aggiornamenti: {(Updates.IsInstalled ? "automatici" : "non disponibili (copia non installata)")}\n" +
        $"Sorgente: {(string.IsNullOrWhiteSpace(UpdateService.EffectiveSource) ? "—" : UpdateService.EffectiveSource)}");

    private void ShowUpdateReady(string version)
    {
        UpdateLabel = $"Navifox {version} pronto · Riavvia e aggiorna";
        IsUpdateReady = true;
        StatusMessage = $"È disponibile Navifox {version}.";
    }
}
