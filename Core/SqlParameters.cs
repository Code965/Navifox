using System.Globalization;
using System.Text;
using Navifox.Core.Models;

namespace Navifox.Core;

/// <summary>Parametro trovato nello script: nome e posizioni (per la sostituzione).</summary>
public sealed record SqlParameterRef(string Name, int Index, int Length);

/// <summary>
/// Parametri nelle query: <c>:nome</c> oppure <c>${nome}</c>. Prima dell'esecuzione Navifox chiede i valori e li inserisce
/// come letterali SQL (numeri così come sono, testi tra apici con escape, NULL). Ignora stringhe, commenti, identificatori
/// quotati, i cast PostgreSQL <c>::tipo</c> e l'assegnazione MySQL <c>:=</c>.
/// </summary>
public static class SqlParameters
{
    public static IReadOnlyList<SqlParameterRef> Find(string sql)
    {
        var result = new List<SqlParameterRef>();
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (c is '\'' or '"' or '`') { i = SkipQuoted(sql, i, c); continue; }
            if (c == '[') { i = SkipQuoted(sql, i, ']'); continue; }
            if (c == '-' && next == '-') { while (i < sql.Length && sql[i] != '\n') i++; continue; }
            if (c == '/' && next == '*') { var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? sql.Length : end + 1; continue; }
            if (c == '$' && next == '$') { var end = sql.IndexOf("$$", i + 2, StringComparison.Ordinal); i = end < 0 ? sql.Length : end + 1; continue; } // corpo di funzione PostgreSQL
            if (c == '$' && next == '{')
            {
                var close = sql.IndexOf('}', i + 2);
                if (close > i + 2 && IsName(sql.AsSpan(i + 2, close - i - 2)))
                {
                    result.Add(new SqlParameterRef(sql[(i + 2)..close], i, close - i + 1));
                    i = close;
                }
                continue;
            }
            if (c != ':' || next == ':' || next == '=') { if (c == ':' && next == ':') i++; continue; }
            if (i > 0 && (char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] is '_' or ':')) continue;
            if (!(char.IsLetter(next) || next == '_')) continue;
            var start = i + 1;
            var j = start;
            while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
            result.Add(new SqlParameterRef(sql[start..j], i, j - i));
            i = j - 1;
        }
        return result;
    }

    /// <summary>Nomi distinti, nell'ordine in cui compaiono.</summary>
    public static IReadOnlyList<string> Names(string sql) =>
        Find(sql).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Sostituisce i parametri con i valori (già formattati come SQL dal chiamante o da <see cref="Literal"/>).</summary>
    public static string Substitute(string sql, IReadOnlyDictionary<string, string> sqlValues)
    {
        var parameters = Find(sql);
        if (parameters.Count == 0) return sql;
        var builder = new StringBuilder(sql.Length);
        var last = 0;
        foreach (var parameter in parameters)
        {
            builder.Append(sql, last, parameter.Index - last);
            builder.Append(sqlValues.TryGetValue(parameter.Name, out var value) ? value : sql.Substring(parameter.Index, parameter.Length));
            last = parameter.Index + parameter.Length;
        }
        builder.Append(sql, last, sql.Length - last);
        return builder.ToString();
    }

    /// <summary>Valore digitato → letterale SQL: numeri invariati, NULL, altrimenti testo tra apici (o SQL grezzo se richiesto).</summary>
    public static string Literal(string value, bool raw, DatabaseKind kind)
    {
        if (raw) return value;
        var trimmed = value.Trim();
        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)) return "NULL";
        if (trimmed.Length > 0 && decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _)
            && !(trimmed.Length > 1 && trimmed[0] == '0' && trimmed[1] != '.')) return trimmed;
        var escaped = value.Replace("'", "''");
        if (kind == DatabaseKind.MySql) escaped = escaped.Replace("\\", "\\\\");
        return (kind == DatabaseKind.SqlServer ? "N'" : "'") + escaped + "'";
    }

    private static bool IsName(ReadOnlySpan<char> text)
    {
        if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_')) return false;
        foreach (var c in text) if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    private static int SkipQuoted(string sql, int start, char close)
    {
        for (var i = start + 1; i < sql.Length; i++)
        {
            if (sql[i] == '\\' && close == '\'' ) { i++; continue; }
            if (sql[i] != close) continue;
            if (i + 1 < sql.Length && sql[i + 1] == close && close != ']') { i++; continue; }
            return i;
        }
        return sql.Length - 1;
    }
}

/// <summary>Regione ripiegabile dell'editor (offset di inizio e fine nel testo).</summary>
public sealed record SqlFold(int Start, int End, string Title);

/// <summary>
/// Regioni ripiegabili dell'editor SQL: istruzioni su più righe, blocchi BEGIN…END, commenti /* */ su più righe
/// e regioni esplicite <c>-- region nome</c> … <c>-- endregion</c>.
/// </summary>
public static class SqlFolding
{
    public static IReadOnlyList<SqlFold> Find(string text)
    {
        var folds = new List<SqlFold>();
        var regions = new Stack<(int Start, string Title)>();
        var blocks = new Stack<int>();
        var statementStart = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c is '\'' or '"' or '`') { if (statementStart < 0) statementStart = i; i = Skip(text, i, c); continue; }
            if (c == '-' && next == '-')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                var comment = text[(i + 2)..end].Trim();
                if (comment.StartsWith("region", StringComparison.OrdinalIgnoreCase) || comment.StartsWith("#region", StringComparison.OrdinalIgnoreCase))
                    regions.Push((i, comment.TrimStart('#')[6..].Trim() is { Length: > 0 } t ? t : "region"));
                else if ((comment.StartsWith("endregion", StringComparison.OrdinalIgnoreCase) || comment.StartsWith("#endregion", StringComparison.OrdinalIgnoreCase)) && regions.Count > 0)
                {
                    var (start, title) = regions.Pop();
                    folds.Add(new SqlFold(start, TrimEnd(text, end), title));
                }
                i = end - 1;
                continue;
            }
            if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                if (text.IndexOf('\n', i, end - i) >= 0) folds.Add(new SqlFold(i, end, "/* … */"));
                i = end - 1;
                continue;
            }
            if (char.IsWhiteSpace(c)) continue;
            if (statementStart < 0) statementStart = i;
            if (char.IsLetter(c) && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '_'))
            {
                var j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                var word = text[i..j];
                if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) || word.Equals("CASE", StringComparison.OrdinalIgnoreCase)) blocks.Push(LineStart(text, i));
                else if (word.Equals("END", StringComparison.OrdinalIgnoreCase) && blocks.Count > 0 && !EndsOtherBlock(text, j))
                {
                    var start = blocks.Pop();
                    var after = j;
                    if (text.IndexOf('\n', start, after - start) >= 0 && blocks.Count > 0) folds.Add(new SqlFold(start, after, "BEGIN … END"));
                }
                i = j - 1;
                continue;
            }
            if (c == ';' && blocks.Count == 0)
            {
                if (statementStart >= 0 && text.IndexOf('\n', statementStart, i - statementStart) >= 0)
                    folds.Add(new SqlFold(statementStart, i + 1, Title(text, statementStart)));
                statementStart = -1;
            }
        }
        if (statementStart >= 0)
        {
            var end = TrimEnd(text, text.Length);
            if (end > statementStart && text.IndexOf('\n', statementStart, end - statementStart) >= 0) folds.Add(new SqlFold(statementStart, end, Title(text, statementStart)));
        }
        return folds.Where(f => f.End > f.Start).OrderBy(f => f.Start).ToArray();
    }

    /// <summary>END IF / END LOOP / END WHILE…: chiudono blocchi che non abbiamo aperto (non BEGIN né CASE).</summary>
    private static bool EndsOtherBlock(string text, int after)
    {
        var i = after;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        var j = i;
        while (j < text.Length && char.IsLetter(text[j])) j++;
        return text[i..j].ToUpperInvariant() is "IF" or "LOOP" or "WHILE" or "REPEAT" or "FOR";
    }

    private static string Title(string text, int start)
    {
        var end = text.IndexOf('\n', start);
        var line = text[start..(end < 0 ? text.Length : end)].Trim();
        return (line.Length > 60 ? line[..60] : line) + " …";
    }

    private static int LineStart(string text, int index)
    {
        var newline = text.LastIndexOf('\n', Math.Max(0, index - 1));
        var start = newline + 1;
        while (start < index && text[start] is ' ' or '\t') start++;
        return start;
    }

    private static int TrimEnd(string text, int end)
    {
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        return end;
    }

    private static int Skip(string text, int start, char quote)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] != quote) continue;
            if (i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
            return i;
        }
        return text.Length - 1;
    }
}
