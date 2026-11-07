using System.Security.Cryptography;
using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Navifox.Core.Models;

namespace Navifox.App.Services;

/// <summary>
/// Assistente SQL basato su Claude (API Anthropic): spiega query, genera SQL da una descrizione in italiano,
/// suggerisce indici/ottimizzazioni e corregge errori. La chiave API è salvata cifrata (DPAPI) nelle impostazioni,
/// oppure letta dalla variabile d'ambiente ANTHROPIC_API_KEY.
/// </summary>
public static class SqlAssistant
{
    public const string Model = "claude-opus-5-5";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Navifox/anthropic-key/v1");

    public static string? ApiKey
    {
        get
        {
            var stored = AppSettings.Current.AnthropicApiKeyProtected;
            if (!string.IsNullOrEmpty(stored))
            {
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser)); }
                catch { /* chiave illeggibile: si ripiega sulla variabile d'ambiente */ }
            }
            var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            return string.IsNullOrWhiteSpace(env) ? null : env;
        }
    }

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public static void SetApiKey(string? key)
    {
        AppSettings.Current.AnthropicApiKeyProtected = string.IsNullOrWhiteSpace(key)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }

    private static string System(bool mysql, string? serverVersion) => $"""
        Sei l'assistente SQL integrato in Navifox, un client per database relazionali.
        Il database dell'utente è {(mysql ? "MySQL/MariaDB" : "PostgreSQL")}{(string.IsNullOrWhiteSpace(serverVersion) ? string.Empty : " " + serverVersion)}: usa esattamente questo dialetto.
        Rispondi sempre in italiano, in modo conciso e pratico. Quando proponi SQL, mettilo in un unico blocco ```sql eseguibile così com'è.
        Usa solo tabelle e colonne presenti nello schema fornito; se manca qualcosa dillo invece di inventare.
        Non proporre mai istruzioni distruttive (DROP, TRUNCATE, DELETE/UPDATE senza WHERE) a meno che l'utente non le chieda esplicitamente, e in quel caso segnala il rischio.
        """;

    /// <summary>Riassunto compatto dello schema (tabelle, colonne, chiavi) da includere nella richiesta.</summary>
    public static string DescribeSchema(SchemaModel? schema, int maxChars = 60_000)
    {
        if (schema is null || schema.Tables.Count == 0) return "(schema non disponibile)";
        var builder = new StringBuilder();
        foreach (var (table, columns) in schema.Tables.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(schema.Views.Contains(table) ? "VIEW " : "TABLE ").Append(table).Append('(');
            builder.Append(string.Join(", ", columns.Select(c => $"{c.Name} {c.DataType}{(c.IsPrimaryKey ? " PK" : string.Empty)}{(c.IsNullable ? string.Empty : " NOT NULL")}")));
            builder.AppendLine(")");
            if (builder.Length > maxChars) { builder.AppendLine("… (schema troncato)"); break; }
        }
        foreach (var fk in schema.ForeignKeys)
        {
            if (builder.Length > maxChars) break;
            builder.AppendLine($"FK {fk.Table}({string.Join(", ", fk.Columns)}) -> {fk.ReferencedTable}({string.Join(", ", fk.ReferencedColumns)})");
        }
        return builder.ToString();
    }

    public static string ExplainPrompt(string sql, string schema) =>
        $"Spiega cosa fa questa query, passo per passo, e segnala eventuali problemi (prestazioni, NULL, join mancanti, risultati inattesi).\n\nSchema:\n{schema}\n\nQuery:\n```sql\n{sql}\n```";

    public static string GeneratePrompt(string request, string schema) =>
        $"Scrivi la query SQL che risponde a questa richiesta: «{request}».\nDopo il blocco SQL aggiungi una o due righe di spiegazione.\n\nSchema:\n{schema}";

    public static string OptimizePrompt(string sql, string? explain, string schema) =>
        $"Analizza le prestazioni di questa query. Suggerisci gli indici da creare (con le istruzioni CREATE INDEX) e un'eventuale riscrittura più efficiente.\n\nSchema:\n{schema}\n\nQuery:\n```sql\n{sql}\n```" +
        (string.IsNullOrWhiteSpace(explain) ? string.Empty : $"\n\nPiano di esecuzione (EXPLAIN):\n```\n{explain}\n```");

    public static string FixPrompt(string sql, string error, string schema) =>
        $"Questa query restituisce un errore. Spiega la causa in breve e fornisci la query corretta.\n\nErrore:\n{error}\n\nSchema:\n{schema}\n\nQuery:\n```sql\n{sql}\n```";

    /// <summary>
    /// Invia la richiesta in streaming e chiama <paramref name="onText"/> con il testo man mano che arriva.
    /// Restituisce il testo completo; gli errori sono tradotti in messaggi leggibili (<see cref="SqlAssistantException"/>).
    /// </summary>
    public static async Task<string> AskAsync(string prompt, bool mysql, string? serverVersion, Action<string> onText, CancellationToken cancellationToken)
    {
        var key = ApiKey ?? throw new SqlAssistantException("Configura la chiave API di Anthropic in Strumenti › Opzioni › Assistente SQL (oppure imposta ANTHROPIC_API_KEY).");
        var client = new AnthropicClient { ApiKey = key };
        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 16000,
            System = System(mysql, serverVersion),
            // Se la richiesta viene rifiutata dai filtri di sicurezza, l'API la ripete su un modello di riserva.
            Fallbacks = new Default(),
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Messages = [new BetaUserMessageParam { Content = prompt }],
        };
        var text = new StringBuilder();
        string? stopReason = null;
        try
        {
            await foreach (var streamEvent in client.Beta.Messages.CreateStreaming(parameters, cancellationToken))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var chunk))
                {
                    text.Append(chunk.Text);
                    onText(text.ToString());
                }
                else if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason is { } reason)
                {
                    stopReason = reason.ToString();
                }
            }
        }
        catch (AnthropicUnauthorizedException) { throw new SqlAssistantException("Chiave API non valida: controllala in Strumenti › Opzioni › Assistente SQL."); }
        catch (AnthropicForbiddenException) { throw new SqlAssistantException("La chiave API non ha accesso a questo modello."); }
        catch (AnthropicRateLimitException) { throw new SqlAssistantException("Troppe richieste in poco tempo: riprova tra qualche secondo."); }
        catch (Anthropic5xxException) { throw new SqlAssistantException("Il servizio Anthropic è temporaneamente non disponibile: riprova più tardi."); }
        catch (AnthropicIOException exception) { throw new SqlAssistantException($"Impossibile raggiungere l'API Anthropic: {exception.Message}"); }
        catch (AnthropicApiException exception) { throw new SqlAssistantException($"Errore dell'API Anthropic: {exception.Message}"); }

        if (stopReason is not null && stopReason.Contains("refusal", StringComparison.OrdinalIgnoreCase))
            throw new SqlAssistantException("L'assistente ha rifiutato questa richiesta.");
        if (stopReason is not null && stopReason.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
            text.AppendLine().Append("… (risposta troncata)");
        return text.ToString();
    }

    /// <summary>Primo blocco ```sql della risposta (o null).</summary>
    public static string? ExtractSql(string answer)
    {
        var start = answer.IndexOf("```sql", StringComparison.OrdinalIgnoreCase);
        var offset = 6;
        if (start < 0) { start = answer.IndexOf("```", StringComparison.Ordinal); offset = 3; }
        if (start < 0) return null;
        var bodyStart = answer.IndexOf('\n', start + offset);
        if (bodyStart < 0) return null;
        var end = answer.IndexOf("```", bodyStart, StringComparison.Ordinal);
        return (end < 0 ? answer[(bodyStart + 1)..] : answer[(bodyStart + 1)..end]).Trim();
    }
}

public sealed class SqlAssistantException(string message) : Exception(message);
