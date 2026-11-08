using System.Text.RegularExpressions;

namespace Navifox.Core;

/// <summary>
/// Traduzione dei testi dell'interfaccia: dizionario italiano → altra lingua.
/// Le voci con segnaposto ({0}, {1}…) sono modelli per i messaggi con valori variabili
/// («{0} righe» → «{0} rows»): i valori vengono riportati nella traduzione così come sono.
/// </summary>
public sealed class Translator
{
    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly List<(Regex Pattern, string Template, int Literal)> _patterns = [];
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);

    public Translator(IReadOnlyDictionary<string, string> entries)
    {
        foreach (var (source, target) in entries)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || source == target) continue;
            if (Regex.IsMatch(source, @"\{\d+\}"))
            {
                var literal = Regex.Replace(source, @"\{\d+\}", string.Empty);
                // Modelli quasi vuoti («{0}: {1}») catturerebbero qualsiasi testo: si ignorano.
                if (Regex.Matches(literal, @"[A-Za-zÀ-ú]").Count < 4) continue;
                var body = Regex.Replace(Regex.Escape(source), @"\\\{(\d+)\}", m => $"(?<p{m.Groups[1].Value}>.*?)");
                _patterns.Add((new Regex("^" + body + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant), target, literal.Length));
            }
            else _exact[source] = target;
        }
        // Prima i modelli più specifici (più testo fisso).
        _patterns.Sort((a, b) => b.Literal.CompareTo(a.Literal));
    }

    public int Count => _exact.Count + _patterns.Count;

    /// <summary>Testo tradotto, oppure null se non c'è traduzione.</summary>
    public string? Translate(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000) return null;
        if (_exact.TryGetValue(text, out var exact)) return exact;
        lock (_cache)
            if (_cache.TryGetValue(text, out var cached)) return cached;
        string? result = null;
        // Testo con spazi iniziali/finali: si traduce il contenuto e si rimettono gli spazi.
        var trimmed = text.Trim();
        if (trimmed.Length != text.Length && trimmed.Length > 0 && _exact.TryGetValue(trimmed, out var inner))
            result = text[..text.IndexOf(trimmed[0])] + inner + text[(text.IndexOf(trimmed[0]) + trimmed.Length)..];
        else
            foreach (var (pattern, template, _) in _patterns)
            {
                var match = pattern.Match(text);
                if (!match.Success) continue;
                // I valori (nomi di tabelle, numeri…) restano come sono: potrebbero essere dati dell'utente.
                result = Regex.Replace(template, @"\{(\d+)\}", m => match.Groups["p" + m.Groups[1].Value].Value);
                break;
            }
        lock (_cache)
        {
            if (_cache.Count > 5000) _cache.Clear();
            _cache[text] = result;
        }
        return result;
    }
}
