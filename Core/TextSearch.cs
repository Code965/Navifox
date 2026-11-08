using System.Text.RegularExpressions;

namespace Navifox.Core;

public sealed record TextMatch(int Index, int Length);

/// <summary>Trova e sostituisci dell'editor SQL: maiuscole/minuscole, parola intera, espressioni regolari.</summary>
public static class TextSearch
{
    public static Regex? Build(string pattern, bool matchCase, bool wholeWord, bool regex)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        var body = regex ? pattern : Regex.Escape(pattern);
        if (wholeWord) body = $@"(?<![\w]){body}(?![\w])";
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { return new Regex(body, options, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException) { return null; }
    }

    public static IReadOnlyList<TextMatch> FindAll(string text, Regex? regex) =>
        regex is null ? [] : regex.Matches(text).Where(m => m.Length > 0).Select(m => new TextMatch(m.Index, m.Length)).ToArray();

    /// <summary>Prossima corrispondenza dopo (o prima di) una posizione, ricominciando dall'altro capo.</summary>
    public static TextMatch? Next(IReadOnlyList<TextMatch> matches, int offset, bool backwards)
    {
        if (matches.Count == 0) return null;
        return backwards
            ? matches.LastOrDefault(m => m.Index < offset) ?? matches[^1]
            : matches.FirstOrDefault(m => m.Index >= offset) ?? matches[0];
    }

    /// <summary>Testo sostitutivo di una corrispondenza ($1, $2… con le espressioni regolari).</summary>
    public static string Replacement(Regex regex, string text, TextMatch match, string replacement, bool useRegex) =>
        useRegex ? regex.Match(text, match.Index, match.Length).Result(replacement) : replacement;

    public static (string Text, int Count) ReplaceAll(string text, Regex? regex, string replacement, bool useRegex)
    {
        if (regex is null) return (text, 0);
        var count = 0;
        var result = regex.Replace(text, m =>
        {
            if (m.Length == 0) return m.Value;
            count++;
            return useRegex ? m.Result(replacement) : replacement;
        });
        return (result, count);
    }
}
