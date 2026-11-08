using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Navifox.Core;

/// <summary>
/// Rapporto di errore anonimo: versione, sistema e traccia dell'eccezione, senza dati personali.
/// Nome utente e cartelle personali, password, stringhe di connessione, indirizzi email e IP vengono oscurati;
/// testi tra apici (valori, SQL) sostituiti con «…». L'utente vede il testo prima di inviarlo.
/// </summary>
public static class CrashReport
{
    public const string Repository = "https://github.com/Code965/Navifox";

    public static string Build(Exception exception, string version, string context)
    {
        var text = new StringBuilder();
        text.AppendLine($"Navifox {version}");
        text.AppendLine($"Sistema: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) · .NET {Environment.Version}");
        text.AppendLine($"Contesto: {context}");
        text.AppendLine();
        var depth = 0;
        for (var current = exception; current is not null && depth < 4; current = current.InnerException, depth++)
        {
            if (depth > 0) text.AppendLine().AppendLine("— causato da —");
            text.AppendLine($"{current.GetType().FullName}: {current.Message}");
            if (current.StackTrace is { } stack) text.AppendLine(stack);
        }
        return Sanitize(text.ToString());
    }

    public static string Sanitize(string text)
    {
        var user = Environment.UserName;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (user.Length >= 3) text = Regex.Replace(text, Regex.Escape(user), "<utente>", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?i)\b(password|pwd|passphrase|user id|uid|username|user|token|api[_-]?key|secret)\s*[=:]\s*[^;\s,'""]+", "$1=***");
        text = Regex.Replace(text, @"[\w.+-]+@[\w-]+\.[\w.-]+", "<email>");
        text = Regex.Replace(text, @"\b\d{1,3}(\.\d{1,3}){3}\b", "<ip>");
        text = Regex.Replace(text, @"'[^'\r\n]{0,400}'", "'…'");
        text = Regex.Replace(text, "(?<![\\w])\"[^\"\\r\\n]{0,400}\"", "\"…\"");
        return text;
    }

    /// <summary>Indirizzo per aprire una segnalazione già compilata su GitHub (il testo è limitato per la lunghezza dell'URL).</summary>
    public static string IssueUrl(string exceptionName, string report)
    {
        var title = $"Errore: {exceptionName.Split('.').Last()}";
        var body = $"**Cosa stavo facendo** (facoltativo):\n\n\n**Dettagli tecnici** (generati da Navifox, senza dati personali):\n```\n{report}\n```";
        if (body.Length > 5500) body = body[..5500] + "\n…\n```";
        return $"{Repository}/issues/new?labels=crash&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }
}
