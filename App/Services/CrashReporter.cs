using System.Diagnostics;
using Avalonia.Threading;
using Navifox.Core;

namespace Navifox.App.Services;

/// <summary>
/// Segnalazione errori facoltativa. Gli errori imprevisti vengono salvati (anonimi) in crash\ nella cartella dati;
/// Navifox propone di inviarli aprendo una segnalazione già compilata su GitHub. Nulla parte senza un clic dell'utente.
/// </summary>
public static class CrashReporter
{
    private static string Folder => AppPaths.File("crash");
    private static int _showing;

    /// <summary>Chiamato all'avvio (prima di Avalonia): registra i gestori degli errori del processo.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception) Save(exception, "chiusura imprevista");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Save(e.Exception, "operazione in background");
            e.SetObserved();
        };
    }

    /// <summary>Dopo l'avvio di Avalonia (il Dispatcher non va toccato prima): errori sull'interfaccia
    /// salvati e proposti per la segnalazione, senza chiudere il programma.</summary>
    public static void InstallUiHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            var path = Save(e.Exception, "interfaccia");
            e.Handled = true;
            if (path is not null) Dispatcher.UIThread.Post(() => _ = OfferAsync(e.Exception, path));
        };
    }

    /// <summary>All'avvio: se l'ultima sessione si è chiusa per un errore, propone di segnalarlo.</summary>
    public static async Task OfferPendingAsync()
    {
        try
        {
            var pending = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.txt").Where(f => !f.EndsWith(".sent.txt", StringComparison.Ordinal) && !f.EndsWith(".seen.txt", StringComparison.Ordinal)).OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null;
            if (pending is null) return;
            var report = await File.ReadAllTextAsync(pending);
            await AskAsync(report, pending, "Navifox si è chiuso per un errore durante l'ultima sessione.");
        }
        catch (Exception) { /* la segnalazione non deve mai impedire l'avvio */ }
    }

    private static string? Save(Exception exception, string context)
    {
        try
        {
            if (!AppSettings.Current.OfferCrashReports) return null;
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, $"errore-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, CrashReport.Build(exception, UpdateService.CurrentVersion, context));
            foreach (var old in Directory.GetFiles(Folder, "*.txt").OrderByDescending(File.GetLastWriteTime).Skip(20)) File.Delete(old);
            return path;
        }
        catch (Exception) { return null; }
    }

    private static async Task OfferAsync(Exception exception, string path)
    {
        try { await AskAsync(await File.ReadAllTextAsync(path), path, $"Si è verificato un errore imprevisto: {exception.Message}"); }
        catch (Exception) { }
    }

    private static async Task AskAsync(string report, string path, string intro)
    {
        if (Interlocked.Exchange(ref _showing, 1) == 1) return;
        try
        {
            if (App.Services?.GetService(typeof(IDialogService)) is not IDialogService dialogs) return;
            var window = new Views.CrashReportWindow(intro, report);
            var choice = await dialogs.ShowDialogAsync(window);
            if (choice)
            {
                var title = report.Split('\n').Skip(4).FirstOrDefault()?.Split(':')[0].Trim() ?? "Errore";
                Process.Start(new ProcessStartInfo(CrashReport.IssueUrl(title, report)) { UseShellExecute = true });
                File.Move(path, Path.ChangeExtension(path, ".sent.txt"), overwrite: true);
            }
            else File.Move(path, Path.ChangeExtension(path, ".seen.txt"), overwrite: true);
        }
        finally { Interlocked.Exchange(ref _showing, 0); }
    }
}
