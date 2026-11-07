using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.Services;

/// <summary>
/// Modalità sicura: prima di eseguire chiede conferma per le istruzioni distruttive (UPDATE/DELETE senza WHERE, DROP, TRUNCATE)
/// e, sulle connessioni di produzione, per qualsiasi modifica.
/// </summary>
public static class SqlGuard
{
    /// <summary>True se si può procedere (nessun rischio o conferma data).</summary>
    public static async Task<bool> ConfirmAsync(IDialogService? dialogs, ConnectionProfile profile, string sql, string action = "Eseguire")
    {
        if (dialogs is null) return true;
        var mysql = profile.Kind == DatabaseKind.MySql;
        var risks = SqlSafety.Analyze(sql, mysql).Where(r => r.Risk != SqlRisk.Read).ToArray();
        if (risks.Length == 0) return true;
        var dangerous = risks.Where(r => r.Risk == SqlRisk.Dangerous).ToArray();
        if (!profile.IsProduction && (dangerous.Length == 0 || !AppSettings.Current.SafeMode)) return true;
        return await AskAsync(dialogs, profile, profile.IsProduction ? risks : dangerous, action);
    }

    /// <summary>Conferma per modifiche già pronte (dati della griglia, struttura, trasferimenti) su una connessione di produzione.</summary>
    public static async Task<bool> ConfirmProductionAsync(IDialogService? dialogs, ConnectionProfile profile, string what)
    {
        if (dialogs is null || !profile.IsProduction) return true;
        return await dialogs.ConfirmAsync("Connessione di PRODUZIONE",
            $"«{profile.Name}» è una connessione di PRODUZIONE ({profile.Host}).\n\n{what}\n\nProcedere?", "Sì, procedi in produzione", danger: true);
    }

    private static Task<bool> AskAsync(IDialogService dialogs, ConnectionProfile profile, IReadOnlyList<SqlRiskItem> risks, string action)
    {
        var lines = risks.Take(8).Select(r => $"  • {r.Reason}\n      {Preview(r.Statement)}");
        var more = risks.Count > 8 ? $"\n  … e altre {risks.Count - 8} istruzioni" : string.Empty;
        var title = profile.IsProduction ? "Connessione di PRODUZIONE" : "Istruzione distruttiva";
        var header = profile.IsProduction
            ? $"«{profile.Name}» è una connessione di PRODUZIONE ({profile.Host}). Le istruzioni seguenti modificano i dati o la struttura:"
            : "Queste istruzioni possono cancellare o modificare molti dati:";
        return dialogs.ConfirmAsync(title, $"{header}\n\n{string.Join("\n", lines)}{more}\n\n{action} comunque?", $"{action}", danger: true);
    }

    private static string Preview(string statement)
    {
        var line = string.Join(' ', statement.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 110 ? line[..110] + "…" : line;
    }
}
