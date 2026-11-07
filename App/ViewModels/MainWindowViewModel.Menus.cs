using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navifox.App.Services;
using Navifox.Core.Models;

namespace Navifox.App.ViewModels;

/// <summary>Comandi dei menu File, Visualizza e Aiuto.</summary>
public partial class MainWindowViewModel
{
    private const string ProjectUrl = "https://github.com/Code965/Navifox";

    // ───────────── File ─────────────

    [RelayCommand]
    private async Task AddConnectionWithDriverAsync(string? driver)
    {
        var profile = await _connectionDialog.ShowAsync(driver, AllFolders(), CurrentFolder());
        if (profile is null) return;
        Profiles.Add(profile);
        RebuildConnectionTree();
        StatusMessage = $"Connessione «{profile.Name}» aggiunta.";
        await SaveProfilesAsync();
    }

    public IReadOnlyList<string> RecentFiles => AppSettings.Current.RecentFiles;

    [RelayCommand]
    private async Task OpenExternalSqlAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Apri file SQL", new FileFilter("Script SQL", "*.sql"), new FileFilter("Testo", "*.txt"), new FileFilter("Tutti i file", "*.*"));
        if (path is not null) await OpenSqlFileAsync(path);
    }

    [RelayCommand]
    private Task OpenRecentAsync(string? path) => string.IsNullOrEmpty(path) ? Task.CompletedTask : OpenSqlFileAsync(path);

    private async Task OpenSqlFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            AppSettings.Current.RecentFiles.Remove(path);
            AppSettings.Current.Save();
            await _dialogs.ShowMessageAsync("File non trovato", $"«{path}» non esiste più ed è stato tolto dai file recenti.", isError: true);
            return;
        }
        var context = CurrentDatabaseContext();
        var profile = context?.Profile ?? Profiles.FirstOrDefault();
        if (profile is null)
        {
            await _dialogs.ShowMessageAsync("Apri file SQL", "Crea prima una connessione: il file viene aperto in una scheda query collegata a una connessione.");
            return;
        }
        var tab = new QueryTabViewModel(_databaseAccess, profile, context?.Database, false, Profiles.ToArray(), _dialogs)
        {
            SqlText = await File.ReadAllTextAsync(path),
            FilePath = path,
        };
        OpenTabs.Add(tab);
        SelectedTab = tab;
        RememberRecent(path);
        StatusMessage = $"Aperto «{Path.GetFileName(path)}».";
    }

    private void RememberRecent(string path)
    {
        var recent = AppSettings.Current.RecentFiles;
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, path);
        if (recent.Count > 10) recent.RemoveRange(10, recent.Count - 10);
        AppSettings.Current.Save();
        OnPropertyChanged(nameof(RecentFiles));
    }

    [RelayCommand]
    private void ClearRecent()
    {
        AppSettings.Current.RecentFiles.Clear();
        AppSettings.Current.Save();
        OnPropertyChanged(nameof(RecentFiles));
    }

    private static readonly JsonSerializerOptions ExportJson = new() { WriteIndented = true };

    [RelayCommand]
    private async Task ExportConnectionsAsync()
    {
        if (Profiles.Count == 0)
        {
            StatusMessage = "Nessuna connessione da esportare.";
            return;
        }
        var path = await _dialogs.PickSaveFileAsync("Esporta connessioni", "navifox-connessioni.json", "json", new FileFilter("Connessioni Navifox", "*.json"));
        if (path is null) return;
        var withPasswords = await _dialogs.ConfirmAsync("Esporta connessioni",
            $"Esportare {Profiles.Count} connessioni in «{Path.GetFileName(path)}».\n\nIncludere anche le password? Nel file saranno in chiaro: conservalo in un posto sicuro.",
            "Includi password");
        var profiles = Profiles.Select(p => withPasswords ? p : p with { Password = string.Empty, SshPassword = string.Empty, SshPrivateKeyPassphrase = string.Empty }).ToArray();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ConnectionExport("navifox-connections", 1, profiles), ExportJson));
        StatusMessage = $"{profiles.Length} connessioni esportate{(withPasswords ? " (con password)" : " (senza password)")} in {Path.GetFileName(path)}.";
    }

    [RelayCommand]
    private async Task ImportConnectionsAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Importa connessioni", new FileFilter("Connessioni Navifox", "*.json"), new FileFilter("Tutti i file", "*.*"));
        if (path is null) return;
        try
        {
            var data = JsonSerializer.Deserialize<ConnectionExport>(await File.ReadAllTextAsync(path));
            if (data?.Connections is not { Count: > 0 } imported) throw new InvalidDataException("Il file non contiene connessioni Navifox.");
            var added = 0;
            foreach (var profile in imported)
            {
                var copy = Profiles.Any(p => p.Id == profile.Id) ? profile with { Id = Guid.NewGuid() } : profile;
                if (Profiles.Any(p => p.Name == copy.Name && p.Host == copy.Host && p.Port == copy.Port && p.UserName == copy.UserName && p.Kind == copy.Kind)) continue;
                Profiles.Add(copy);
                added++;
            }
            RebuildConnectionTree();
            await SaveProfilesAsync();
            StatusMessage = added == imported.Count ? $"{added} connessioni importate." : $"{added} connessioni importate ({imported.Count - added} già presenti).";
        }
        catch (Exception exception) { await ReportErrorAsync("Impossibile importare le connessioni", exception); }
    }

    // ───────────── Visualizza ─────────────

    [ObservableProperty] private bool _isFlatObjectList;

    partial void OnIsFlatObjectListChanged(bool value) => _ = RefreshFlatListAsync();

    private async Task RefreshFlatListAsync()
    {
        if (ObjectsTab.Source is not { } category) return;
        if (IsFlatObjectList && category.Parent is { } parent)
            foreach (var sibling in parent.Children.Where(child => child.IsTableCategory && !child.IsLoaded).ToArray()) await EnsureLoadedAsync(sibling);
        ShowCategoryObjects(category, bringToFront: false);
    }

    [RelayCommand]
    private void SetObjectsView(string? mode)
    {
        if (mode == "er")
        {
            _ = OpenModelAsync(ObjectsTab.Source ?? SelectedNode);
            return;
        }
        ObjectsTab.IsListMode = mode == "list";
        SelectedTab = ObjectsTab;
    }

    [RelayCommand]
    private void SortObjects(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        ObjectsTab.SortBy(key);
        SelectedTab = ObjectsTab;
        StatusMessage = $"Oggetti ordinati per {key.ToLowerInvariant()} ({(ObjectsTab.SortDescending ? "decrescente" : "crescente")}).";
    }

    [RelayCommand]
    private void ToggleObjectColumn(string? header)
    {
        if (header is null) return;
        ObjectsTab.ToggleColumn(header);
        SelectedTab = ObjectsTab;
    }

    [RelayCommand]
    private void ShowInfoSection(string? section)
    {
        IsInfoVisible = true;
        if (section == "ddl") Info.ShowDdlCommand.Execute(null);
        else Info.ShowInfoCommand.Execute(null);
    }

    // ───────────── Aiuto ─────────────

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception exception) { StatusMessage = $"Impossibile aprire il browser: {exception.Message}"; }
    }

    public string HelpUrl => ProjectUrl + "#readme";
    public string IssuesUrl => ProjectUrl + "/issues/new";
    public string ReleasesUrl => ProjectUrl + "/releases";

    [RelayCommand]
    private Task ShowShortcutsAsync() => _dialogs.ShowMessageAsync("Scorciatoie da tastiera",
        """
        Generali
          Ctrl+W            Chiudi scheda
          Ctrl+Maiusc+L     Mostra/nascondi pannello connessioni
          Ctrl+Maiusc+I     Mostra/nascondi pannello informazioni
          F6                Console
          Ctrl+L            Registro cronologia
          Ctrl+Maiusc+F     Trova nel database

        Albero ed elenco oggetti
          Ctrl+C / Ctrl+V   Copia / incolla tabelle
          Invio             Apri tabella
          Ctrl+F            Cerca negli oggetti

        Query
          Ctrl+Invio / F5   Esegui (o la selezione)
          Ctrl+Maiusc+Invio Esegui l’istruzione corrente
          Ctrl+Spazio       Completamento automatico

        Tabella e Progetta tabella
          Ctrl+S            Salva modifiche / struttura
          F5                Ricarica
          Ctrl+F            Filtro (dati)
        """);
}

/// <summary>Formato del file di esportazione connessioni.</summary>
public sealed record ConnectionExport(string Format, int Version, IReadOnlyList<ConnectionProfile> Connections);
