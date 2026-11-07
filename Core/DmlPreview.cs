using System.Text;
using System.Text.RegularExpressions;
using Navifox.Core.Models;

namespace Navifox.Core;

/// <summary>Conteggio delle righe toccate da un UPDATE/DELETE: query di sola lettura e limite (LIMIT/TOP) da applicare al risultato.</summary>
public sealed record DmlCount(string Statement, string CountSql, long? Limit);

/// <summary>
/// «Quante righe toccherai?»: da un UPDATE o DELETE ricava la SELECT COUNT(*) con lo stesso FROM/JOIN/WHERE.
/// Non esegue mai l'istruzione originale (nemmeno in una transazione annullata, che non sarebbe sicura con tabelle
/// MySQL non transazionali). Restituisce null per le forme non riconosciute (CTE, OUTPUT, sottoquery nel bersaglio…).
/// </summary>
public static class DmlPreview
{
    private static readonly Regex Word = new(@"[A-Za-z_][A-Za-z0-9_$]*", RegexOptions.Compiled);

    public static bool IsUpdateOrDelete(string statement) =>
        Regex.IsMatch(Strip(statement), @"^(UPDATE|DELETE)\b", RegexOptions.IgnoreCase);

    public static DmlCount? Build(string statement, DatabaseKind kind)
    {
        var sql = Strip(statement).TrimEnd(';').Trim();
        var words = TopLevelWords(sql);
        if (words.Count == 0) return null;
        var first = words[0].Text.ToUpperInvariant();
        if (first is not ("UPDATE" or "DELETE")) return null;
        if (words.Any(w => w.Is("OUTPUT"))) return null; // SQL Server: OUTPUT … non si converte in modo affidabile

        // Coda: RETURNING (PostgreSQL/SQLite), ORDER BY … LIMIT n (MySQL/SQLite).
        long? limit = null;
        var end = sql.Length;
        if (words.FirstOrDefault(w => w.Is("RETURNING")) is { } returning) end = Math.Min(end, returning.Index);
        if (words.LastOrDefault(w => w.Is("LIMIT") && w.Index < end) is { } lim)
        {
            var m = Regex.Match(sql[(lim.Index + 5)..end], @"^\s*(\d+)\s*$");
            if (!m.Success) return null;
            limit = long.Parse(m.Groups[1].Value);
            end = lim.Index;
        }
        if (OrderBy(words, end) is { } order) end = order;

        // TOP (n) di SQL Server subito dopo UPDATE/DELETE.
        var afterVerb = words[0].Index + words[0].Text.Length;
        var top = Regex.Match(sql[afterVerb..], @"^\s*TOP\s*\(\s*(\d+)\s*\)(\s*PERCENT)?", RegexOptions.IgnoreCase);
        if (top.Success)
        {
            if (top.Groups[2].Success) return null;
            limit = long.Parse(top.Groups[1].Value);
            afterVerb += top.Length;
        }

        string? from;
        string where;
        var whereWord = words.FirstOrDefault(w => w.Is("WHERE") && w.Index < end);
        where = whereWord is null ? string.Empty : " " + sql[whereWord.Index..end].Trim();
        var bodyEnd = whereWord?.Index ?? end;

        if (first == "DELETE")
        {
            var fromWord = words.FirstOrDefault(w => w.Is("FROM") && w.Index >= afterVerb && w.Index < bodyEnd);
            if (fromWord is null) return null;
            from = sql[(fromWord.Index + 4)..bodyEnd].Trim();
            // PostgreSQL: DELETE FROM t USING x → FROM t, x
            if (words.FirstOrDefault(w => w.Is("USING") && w.Index > fromWord.Index && w.Index < bodyEnd) is { } usingWord)
                from = sql[(fromWord.Index + 4)..usingWord.Index].Trim() + ", " + sql[(usingWord.Index + 5)..bodyEnd].Trim();
        }
        else
        {
            var setWord = words.FirstOrDefault(w => w.Is("SET") && w.Index > afterVerb);
            if (setWord is null) return null;
            var target = Regex.Replace(sql[afterVerb..setWord.Index], @"^\s*(LOW_PRIORITY\s+|IGNORE\s+|ONLY\s+|OR\s+\w+\s+)*", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (target.Length == 0) return null;
            var fromWord = words.FirstOrDefault(w => w.Is("FROM") && w.Index > setWord.Index && w.Index < bodyEnd);
            if (fromWord is null) from = target;
            else
            {
                var extra = sql[(fromWord.Index + 4)..bodyEnd].Trim();
                // SQL Server: UPDATE t SET … FROM t JOIN … → il FROM contiene già la tabella da aggiornare.
                from = kind == DatabaseKind.SqlServer ? extra : $"{target}, {extra}";
            }
        }
        if (string.IsNullOrWhiteSpace(from)) return null;
        return new DmlCount(statement.Trim(), $"SELECT COUNT(*) FROM {from}{where}", limit);
    }

    private static int? OrderBy(IReadOnlyList<Token> words, int end)
    {
        for (var i = 0; i < words.Count - 1; i++)
            if (words[i].Is("ORDER") && words[i + 1].Is("BY") && words[i].Index < end) return words[i].Index;
        return null;
    }

    private sealed record Token(string Text, int Index)
    {
        public bool Is(string word) => Text.Equals(word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parole al livello più esterno (fuori da parentesi, stringhe, identificatori quotati e commenti).</summary>
    private static List<Token> TopLevelWords(string sql)
    {
        var result = new List<Token>();
        var depth = 0;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            switch (c)
            {
                case '\'' or '"' or '`':
                    i = SkipQuoted(sql, i, c);
                    continue;
                case '[':
                    i = SkipQuoted(sql, i, ']');
                    continue;
                case '(':
                    depth++;
                    continue;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    continue;
            }
            if (depth == 0 && (char.IsLetter(c) || c == '_') && (i == 0 || !(char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] is '_' or '.' or '@' or '$')))
            {
                var m = Word.Match(sql, i);
                result.Add(new Token(m.Value, i));
                i += m.Length - 1;
            }
        }
        return result;
    }

    private static int SkipQuoted(string sql, int start, char close)
    {
        var closing = close == '[' ? ']' : close;
        for (var i = start + 1; i < sql.Length; i++)
        {
            if (sql[i] != closing) continue;
            if (i + 1 < sql.Length && sql[i + 1] == closing && closing != ']') { i++; continue; }
            return i;
        }
        return sql.Length - 1;
    }

    /// <summary>Toglie i commenti (-- e /* */) fuori dalle stringhe.</summary>
    private static string Strip(string sql)
    {
        var builder = new StringBuilder(sql.Length);
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c is '\'' or '"' or '`')
            {
                var end = SkipQuoted(sql, i, c);
                builder.Append(sql, i, end - i + 1);
                i = end;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                builder.Append(' ');
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 1;
                builder.Append(' ');
            }
            else builder.Append(c);
        }
        return builder.ToString().Trim();
    }
}
