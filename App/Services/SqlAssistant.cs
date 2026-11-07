using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Navifox.Core.Models;

namespace Navifox.App.Services;

/// <summary>Fornitore dell'assistente: Anthropic (SDK ufficiale) oppure un servizio compatibile con l'API OpenAI (anche in locale).</summary>
/// <param name="DefaultModel">Modello proposto (solo per Anthropic); per gli altri si sceglie con «Carica modelli».</param>
public sealed record AssistantProvider(string Name, string BaseUrl, bool NeedsKey, bool IsLocal, string? DefaultModel = null, string? KeyHint = null)
{
    public bool IsAnthropic => Name == SqlAssistant.AnthropicName;
    public bool IsCustom => Name == SqlAssistant.CustomName;
    public override string ToString() => Name;
}

/// <summary>
/// Assistente SQL: spiega query, genera SQL da una descrizione in italiano, suggerisce indici e corregge errori.
/// Funziona con Claude (API Anthropic) e con qualsiasi servizio compatibile OpenAI: OpenAI, Google Gemini, Mistral, Groq,
/// OpenRouter, DeepSeek, xAI e i modelli locali di Ollama o LM Studio. Le chiavi sono salvate cifrate (DPAPI), una per fornitore.
/// </summary>
public static class SqlAssistant
{
    public const string AnthropicName = "Anthropic (Claude)";
    public const string CustomName = "Personalizzato (compatibile OpenAI)";
    public const string AnthropicModel = "claude-opus-5-5";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Navifox/anthropic-key/v1");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static IReadOnlyList<AssistantProvider> Providers { get; } =
    [
        new(AnthropicName, "https://api.anthropic.com", true, false, AnthropicModel, "sk-ant-…"),
        new("OpenAI", "https://api.openai.com/v1", true, false, KeyHint: "sk-…"),
        new("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", true, false, KeyHint: "AIza…"),
        new("Mistral", "https://api.mistral.ai/v1", true, false),
        new("Groq", "https://api.groq.com/openai/v1", true, false, KeyHint: "gsk_…"),
        new("OpenRouter", "https://openrouter.ai/api/v1", true, false, KeyHint: "sk-or-…"),
        new("DeepSeek", "https://api.deepseek.com/v1", true, false),
        new("xAI (Grok)", "https://api.x.ai/v1", true, false, KeyHint: "xai-…"),
        new("Ollama (locale)", "http://localhost:11434/v1", false, true),
        new("LM Studio (locale)", "http://localhost:1234/v1", false, true),
        new(CustomName, "http://localhost:8080/v1", false, false),
    ];

    public static AssistantProvider Provider(string? name) => Providers.FirstOrDefault(p => p.Name == name) ?? Providers[0];
    public static AssistantProvider Current => Provider(AppSettings.Current.AssistantProvider);

    public static string BaseUrl(AssistantProvider provider) =>
        provider.IsAnthropic ? provider.BaseUrl
        : AppSettings.Current.AssistantBaseUrls.TryGetValue(provider.Name, out var url) && !string.IsNullOrWhiteSpace(url) ? url : provider.BaseUrl;

    public static string Model(AssistantProvider provider) =>
        AppSettings.Current.AssistantModels.TryGetValue(provider.Name, out var model) && !string.IsNullOrWhiteSpace(model) ? model : provider.DefaultModel ?? string.Empty;

    /// <summary>Chiave del fornitore (Anthropic: anche dalla vecchia impostazione o da ANTHROPIC_API_KEY).</summary>
    public static string? ApiKey(AssistantProvider provider)
    {
        var stored = AppSettings.Current.AssistantKeys.TryGetValue(provider.Name, out var value) ? value
            : provider.IsAnthropic ? AppSettings.Current.AnthropicApiKeyProtected : null;
        if (!string.IsNullOrEmpty(stored))
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser)); }
            catch { /* chiave illeggibile: si ripiega sulla variabile d'ambiente */ }
        }
        var env = Environment.GetEnvironmentVariable(provider.IsAnthropic ? "ANTHROPIC_API_KEY" : provider.Name == "OpenAI" ? "OPENAI_API_KEY" : "NAVIFOX_ASSISTANT_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    public static void Save(AssistantProvider provider, string? key, string? model, string? baseUrl)
    {
        var settings = AppSettings.Current;
        settings.AssistantProvider = provider.Name;
        if (string.IsNullOrWhiteSpace(key)) settings.AssistantKeys.Remove(provider.Name);
        else settings.AssistantKeys[provider.Name] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
        if (provider.IsAnthropic) settings.AnthropicApiKeyProtected = null;
        settings.AssistantModels[provider.Name] = model?.Trim() ?? string.Empty;
        if (!provider.IsAnthropic) settings.AssistantBaseUrls[provider.Name] = string.IsNullOrWhiteSpace(baseUrl) ? provider.BaseUrl : baseUrl.Trim().TrimEnd('/');
    }

    /// <summary>Cosa manca per usare l'assistente (null = pronto).</summary>
    public static string? MissingConfiguration(AssistantProvider provider)
    {
        if (provider.NeedsKey && string.IsNullOrWhiteSpace(ApiKey(provider))) return $"manca la chiave API di {provider.Name}";
        if (string.IsNullOrWhiteSpace(Model(provider))) return $"scegli il modello di {provider.Name} (pulsante «Carica modelli»)";
        return null;
    }

    public static bool IsConfigured => MissingConfiguration(Current) is null;

    /// <summary>Etichetta breve per l'interfaccia, es. «Ollama (locale) · llama3».</summary>
    public static string Description => $"{Current.Name} · {Model(Current)}";

    private static string DialectName(DatabaseKind kind) => kind switch
    {
        DatabaseKind.MySql => "MySQL/MariaDB",
        DatabaseKind.SqlServer => "Microsoft SQL Server (T-SQL)",
        DatabaseKind.Sqlite => "SQLite",
        _ => "PostgreSQL",
    };

    private static string System(DatabaseKind kind, string? serverVersion) => $"""
        Sei l'assistente SQL integrato in Navifox, un client per database relazionali.
        Il database dell'utente è {DialectName(kind)}{(string.IsNullOrWhiteSpace(serverVersion) ? string.Empty : " " + serverVersion)}: usa esattamente questo dialetto.
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
    /// Invia la richiesta al fornitore scelto in streaming e chiama <paramref name="onText"/> con il testo man mano che arriva.
    /// Gli errori sono tradotti in messaggi leggibili (<see cref="SqlAssistantException"/>).
    /// </summary>
    public static Task<string> AskAsync(string prompt, DatabaseKind kind, string? serverVersion, Action<string> onText, CancellationToken cancellationToken)
    {
        var provider = Current;
        if (MissingConfiguration(provider) is { } missing)
            throw new SqlAssistantException($"Assistente non configurato: {missing}. Apri Strumenti › Opzioni › Assistente SQL.");
        var system = System(kind, serverVersion);
        return provider.IsAnthropic
            ? AskAnthropicAsync(ApiKey(provider)!, Model(provider), system, prompt, onText, cancellationToken)
            : AskOpenAiCompatibleAsync(provider, system, prompt, onText, cancellationToken);
    }

    // ───────────── Anthropic (SDK ufficiale) ─────────────

    private static async Task<string> AskAnthropicAsync(string key, string model, string system, string prompt, Action<string> onText, CancellationToken cancellationToken)
    {
        var client = new AnthropicClient { ApiKey = key };
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = system,
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
        catch (AnthropicNotFoundException) { throw new SqlAssistantException($"Modello «{model}» non trovato: controlla il nome in Opzioni."); }
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

    // ───────────── Servizi compatibili OpenAI (OpenAI, Gemini, Mistral, Groq, OpenRouter, Ollama, LM Studio…) ─────────────

    private static HttpRequestMessage Request(HttpMethod method, AssistantProvider provider, string path, string? baseUrl = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, (baseUrl ?? BaseUrl(provider)).TrimEnd('/') + path);
        key ??= ApiKey(provider);
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (provider.Name == "OpenRouter")
        {
            request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/Code965/Navifox");
            request.Headers.TryAddWithoutValidation("X-Title", "Navifox");
        }
        return request;
    }

    private static async Task<string> AskOpenAiCompatibleAsync(AssistantProvider provider, string system, string prompt, Action<string> onText, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = Model(provider),
            ["stream"] = true,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = prompt },
            },
        };
        using var request = Request(HttpMethod.Post, provider, "/chat/completions");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException exception)
        {
            throw new SqlAssistantException(provider.IsLocal
                ? $"{provider.Name} non risponde su {BaseUrl(provider)}: avvialo (es. «ollama serve») e riprova. ({exception.Message})"
                : $"Impossibile raggiungere {provider.Name}: {exception.Message}");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new SqlAssistantException(await DescribeErrorAsync(provider, response, cancellationToken));
            var text = new StringBuilder();
            string? finish = null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                try
                {
                    var choice = JsonNode.Parse(data)?["choices"]?[0];
                    if (choice?["delta"]?["content"]?.GetValue<string>() is { Length: > 0 } chunk)
                    {
                        text.Append(chunk);
                        onText(text.ToString());
                    }
                    if (choice?["finish_reason"]?.GetValue<string>() is { } reason) finish = reason;
                }
                catch (JsonException) { /* riga non JSON (commenti keep-alive): si ignora */ }
            }
            if (finish == "length") text.AppendLine().Append("… (risposta troncata)");
            if (finish == "content_filter") throw new SqlAssistantException("Il fornitore ha bloccato la risposta (filtro dei contenuti).");
            if (text.Length == 0) throw new SqlAssistantException($"{provider.Name} non ha restituito testo: controlla il modello scelto.");
            return text.ToString();
        }
    }

    private static async Task<string> DescribeErrorAsync(AssistantProvider provider, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = string.Empty;
        try
        {
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            detail = json?["error"]?["message"]?.GetValue<string>() ?? json?["error"]?.ToString() ?? json?["message"]?.GetValue<string>() ?? string.Empty;
        }
        catch { /* corpo non JSON */ }
        var status = (int)response.StatusCode;
        var reason = status switch
        {
            401 or 403 => "chiave API non valida o senza accesso al modello",
            404 => $"modello «{Model(provider)}» o indirizzo non trovato",
            429 => "troppe richieste o credito esaurito",
            >= 500 => "servizio temporaneamente non disponibile",
            _ => $"errore {status}",
        };
        return $"{provider.Name}: {reason}.{(detail.Length == 0 ? string.Empty : $"\n{detail}")}";
    }

    /// <summary>Modelli disponibili sul fornitore (GET /models); usati dal pulsante «Carica modelli» delle Opzioni.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(AssistantProvider provider, string? key, string? baseUrl, CancellationToken cancellationToken)
    {
        if (provider.IsAnthropic)
        {
            try
            {
                var client = new AnthropicClient { ApiKey = string.IsNullOrWhiteSpace(key) ? ApiKey(provider) : key };
                var page = await client.Models.List();
                return page.Items.Select(m => m.ID).ToArray();
            }
            catch (AnthropicApiException exception) { throw new SqlAssistantException($"Anthropic: {exception.Message}"); }
        }
        using var request = Request(HttpMethod.Get, provider, "/models", string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl, string.IsNullOrWhiteSpace(key) ? null : key);
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, cancellationToken); }
        catch (HttpRequestException exception)
        {
            throw new SqlAssistantException(provider.IsLocal ? $"{provider.Name} non risponde: è avviato? ({exception.Message})" : $"Impossibile raggiungere {provider.Name}: {exception.Message}");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new SqlAssistantException(await DescribeErrorAsync(provider, response, cancellationToken));
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var items = json?["data"] as JsonArray ?? json?["models"] as JsonArray ?? [];
            return items.Select(item => item?["id"]?.GetValue<string>() ?? item?["name"]?.GetValue<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id!)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
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
