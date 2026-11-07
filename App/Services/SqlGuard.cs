using System.Globalization;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.Services;

/// <summary>
/// Modalità sicura: prima di eseguire chiede conferma per le istruzioni distruttive (UPDATE/DELETE senza WHERE, DROP, TRUNCATE)
/// e, sulle connessioni di produzione, per qualsiasi modifica. Sulle connessioni in sola lettura blocca ogni modifica.
/// Per UPDATE e DELETE può mostrare in anticipo quante righe verranno toccate («Quante righe toccherai?»).
/// </summary>
public static class SqlGuard
{
    /// <summary>Valori di <see cref="AppSettings.RowPreview"/>.</summary>
    public const string PreviewAlways = "Sempre";
    public const string PreviewWithConfirm = "Conferme";
    public const string PreviewNever = "Mai";

    /// <summary>Conta le righe di una SELECT COUNT(*); null se non disponibile.</summary>
    public delegate Task<long?> RowCounter(string countSql, CancellationToken cancellationToken);

    private static readonly TimeSpan CountTimeout = TimeSpan.FromSeconds(8);
    private const int MaxCounted = 10;

    /// <summary>True se si può procedere (nessun rischio o conferma data).</summary>
    public static async Task<bool> ConfirmAsync(IDialogService? dialogs, ConnectionProfile profile, string sql, string action = "Eseguire", RowCounter? countRows = null)
    {
        var mysql = profile.Kind == DatabaseKind.MySql;
        var risks = SqlSafety.Analyze(sql, mysql).Where(r => r.Risk != SqlRisk.Read).ToArray();
        if (risks.Length == 0) return true;
        if (profile.ReadOnly)
        {
            await BlockAsync(dialogs, profile, $"Lo script contiene istruzioni che modificano dati o struttura:\n\n{string.Join("\n", risks.Take(5).Select(r => "  • " + Preview(r.Statement)))}");
            return false;
        }
        if (dialogs is null) return true;
        var dangerous = risks.Where(r => r.Risk == SqlRisk.Dangerous).ToArray();
        var mustConfirm = profile.IsProduction || (dangerous.Length > 0 && AppSettings.Current.SafeMode);
        var preview = AppSettings.Current.RowPreview;
        var wantCounts = countRows is not null && preview != PreviewNever && (mustConfirm || preview == PreviewAlways)
            && risks.Any(r => DmlPreview.IsUpdateOrDelete(r.Statement));
        if (!mustConfirm && !wantCounts) return true;

        IReadOnlyList<SqlRiskItem> shown = profile.IsProduction || wantCounts || !mustConfirm ? risks : dangerous;
        var counts = wantCounts ? await CountAsync(profile, shown, countRows!) : null;
        return await AskAsync(dialogs, profile, shown, action, counts, mustConfirm);
    }

    /// <summary>Conferma per modifiche già pronte (dati della griglia, struttura, trasferimenti) su una connessione di produzione.
    /// Sulle connessioni in sola lettura la modifica è bloccata.</summary>
    public static async Task<bool> ConfirmProductionAsync(IDialogService? dialogs, ConnectionProfile profile, string what)
    {
        if (profile.ReadOnly)
        {
            await BlockAsync(dialogs, profile, what);
            return false;
        }
        if (dialogs is null || !profile.IsProduction) return true;
        return await dialogs.ConfirmAsync("Connessione di PRODUZIONE",
            $"«{profile.Name}» è una connessione di PRODUZIONE ({profile.Host}).\n\n{what}\n\nProcedere?", "Sì, procedi in produzione", danger: true);
    }

    /// <summary>True (e avviso all'utente) se la connessione è in sola lettura: l'operazione non va eseguita.</summary>
    public static async Task<bool> BlockIfReadOnlyAsync(IDialogService? dialogs, ConnectionProfile? profile, string what)
    {
        if (profile?.ReadOnly != true) return false;
        await BlockAsync(dialogs, profile, what);
        return true;
    }

    /// <summary>Contatore che esegue la SELECT COUNT(*) su una connessione separata del database indicato.</summary>
    public static RowCounter Counter(IDatabaseAccessService access, ConnectionProfile profile, string? database) => async (countSql, cancellationToken) =>
    {
        await using var connection = await access.OpenAsync(profile, database, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = countSql;
        command.CommandTimeout = (int)Math.Ceiling(CountTimeout.TotalSeconds);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    };

    private static Task BlockAsync(IDialogService? dialogs, ConnectionProfile profile, string what) =>
        dialogs?.ShowMessageAsync("Connessione in sola lettura",
            $"«{profile.Name}» è in sola lettura: Navifox non esegue modifiche a dati o struttura.\n\n{what}\n\nPer modificare, togli «Sola lettura» nelle proprietà della connessione.", isError: true)
        ?? Task.CompletedTask;

    /// <summary>Per ogni UPDATE/DELETE: righe interessate, oppure null se il conteggio non è disponibile.</summary>
    private static async Task<Dictionary<SqlRiskItem, long?>> CountAsync(ConnectionProfile profile, IReadOnlyList<SqlRiskItem> risks, RowCounter countRows)
    {
        var result = new Dictionary<SqlRiskItem, long?>();
        using var timeout = new CancellationTokenSource(CountTimeout);
        foreach (var risk in risks.Where(r => DmlPreview.IsUpdateOrDelete(r.Statement)).Take(MaxCounted))
        {
            long? count = null;
            if (DmlPreview.Build(risk.Statement, profile.Kind) is { } plan && !timeout.IsCancellationRequested)
            {
                try
                {
                    count = await countRows(plan.CountSql, timeout.Token);
                    if (count is { } c && plan.Limit is { } limit) count = Math.Min(c, limit);
                }
                catch (Exception) { count = null; } // conteggio non disponibile (timeout, permessi, sintassi particolare)
            }
            result[risk] = count;
        }
        return result;
    }

    private static Task<bool> AskAsync(IDialogService dialogs, ConnectionProfile profile, IReadOnlyList<SqlRiskItem> risks, string action,
        IReadOnlyDictionary<SqlRiskItem, long?>? counts, bool mustConfirm)
    {
        var lines = risks.Take(8).Select(r => $"  • {r.Reason}\n      {Preview(r.Statement)}{CountLine(r, counts)}");
        var more = risks.Count > 8 ? $"\n  … e altre {risks.Count - 8} istruzioni" : string.Empty;
        var total = counts?.Values.All(v => v is not null) == true && counts.Count > 1
            ? $"\n\nTotale: {Rows(counts.Values.Sum(v => v!.Value))}." : string.Empty;
        string title, header;
        if (profile.IsProduction)
        {
            title = "Connessione di PRODUZIONE";
            header = $"«{profile.Name}» è una connessione di PRODUZIONE ({profile.Host}). Le istruzioni seguenti modificano i dati o la struttura:";
        }
        else if (mustConfirm)
        {
            title = "Istruzione distruttiva";
            header = "Queste istruzioni possono cancellare o modificare molti dati:";
        }
        else
        {
            title = "Anteprima delle modifiche";
            header = "Righe che verranno modificate:";
        }
        return dialogs.ConfirmAsync(title, $"{header}\n\n{string.Join("\n", lines)}{more}{total}\n\n{action}{(mustConfirm ? " comunque" : string.Empty)}?",
            action, danger: mustConfirm);
    }

    private static string CountLine(SqlRiskItem risk, IReadOnlyDictionary<SqlRiskItem, long?>? counts)
    {
        if (counts is null || !counts.TryGetValue(risk, out var count)) return string.Empty;
        return count is { } n ? $"\n      → {Rows(n)}" : "\n      → conteggio non disponibile";
    }

    private static string Rows(long n) => n == 1 ? "1 riga" : $"{n.ToString("N0", CultureInfo.GetCultureInfo("it-IT"))} righe";

    private static string Preview(string statement)
    {
        var line = string.Join(' ', statement.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 110 ? line[..110] + "…" : line;
    }
}
