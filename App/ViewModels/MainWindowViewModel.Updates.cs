using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Navifox.App.Services;

namespace Navifox.App.ViewModels;

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
            if (Environment.GetEnvironmentVariable("NAVIFOX_UPDATE_AUTOAPPLY") == "1")
            {
                Updates.ApplyAndRestart();
                return;
            }
            // Finestra con le novità, salvo che l'utente abbia scelto di saltare proprio questa versione
            // (il pulsante nella barra di stato resta comunque disponibile).
            if (AppSettings.Current.SkippedUpdateVersion != version) await AskUpdateAsync();
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
            await AskUpdateAsync();
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
    private Task ApplyUpdateAsync() => AskUpdateAsync();

    /// <summary>Finestra «Aggiornamento disponibile»: novità della versione e scelta tra Aggiorna ora, Più tardi, Salta questa versione.</summary>
    private async Task AskUpdateAsync()
    {
        if (!IsUpdateReady || Updates.PendingVersion is not { } version) return;
        var dialog = new Navifox.App.Views.UpdateDialog
        {
            DataContext = new Navifox.App.Views.UpdateDialogModel { NewVersion = version, CurrentVersion = AppVersion, NotesMarkdown = Updates.PendingNotes },
        };
        var owner = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        switch (await dialog.ShowDialog<Navifox.App.Views.UpdateChoice>(owner))
        {
            case Navifox.App.Views.UpdateChoice.Update:
                SaveSession();
                Updates.ApplyAndRestart();
                break;
            case Navifox.App.Views.UpdateChoice.Skip:
                AppSettings.Current.SkippedUpdateVersion = version;
                AppSettings.Current.Save();
                StatusMessage = $"Versione {version} saltata: puoi installarla quando vuoi dal pulsante nella barra di stato.";
                break;
            default:
                StatusMessage = $"Navifox {version} è pronto: installalo quando vuoi dal pulsante nella barra di stato.";
                break;
        }
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
