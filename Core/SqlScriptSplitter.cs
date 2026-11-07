using System.Text;

namespace Navifox.Core;

/// <summary>
/// Divide uno script SQL in istruzioni rispettando stringhe, identificatori quotati, commenti,
/// dollar-quoting PostgreSQL ($tag$…$tag$) e la direttiva DELIMITER dei client MySQL.
/// </summary>
public static class SqlScriptSplitter
{
    private static readonly System.Text.RegularExpressions.Regex GoLine = new(@"^[ \t]*GO[ \t]*(?:--.*)?\r?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ModuleStart = new(
        @"^(?:\s|--[^\n]*\n|/\*.*?\*/)*(?:CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+(?:PROC|PROCEDURE|FUNCTION|TRIGGER|VIEW)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SqliteTrigger = new(@"^CREATE\s+(?:TEMP\s+|TEMPORARY\s+)?TRIGGER\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool EndsWithEnd(string statement) =>
        System.Text.RegularExpressions.Regex.IsMatch(statement, @"\bEND\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Divisione secondo il dialetto. SQL Server: i batch separati da «GO» restano interi se definiscono una procedura,
    /// funzione, trigger o vista (il corpo contiene «;»); gli altri si dividono sui punti e virgola.
    /// </summary>
    public static IEnumerable<string> Split(string script, Models.DatabaseKind kind)
    {
        if (kind == Models.DatabaseKind.Sqlite)
        {
            // CREATE TRIGGER … BEGIN …; …; END: i punti e virgola interni appartengono al corpo del trigger.
            string? pending = null;
            foreach (var statement in Split(script, false))
            {
                if (pending is not null)
                {
                    pending += ";\n" + statement;
                    if (!EndsWithEnd(pending)) continue;
                    yield return pending + ";";
                    pending = null;
                    continue;
                }
                if (SqliteTrigger.IsMatch(statement) && !EndsWithEnd(statement)) { pending = statement; continue; }
                yield return SqliteTrigger.IsMatch(statement) ? statement + ";" : statement;
            }
            if (pending is not null) yield return pending;
            yield break;
        }
        if (kind != Models.DatabaseKind.SqlServer)
        {
            foreach (var statement in Split(script, kind == Models.DatabaseKind.MySql)) yield return statement;
            yield break;
        }
        foreach (var batch in GoLine.Split(script))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            if (ModuleStart.IsMatch(batch))
            {
                yield return batch.Trim();
                continue;
            }
            foreach (var statement in Split(batch, false)) yield return statement;
        }
    }

    public static IEnumerable<string> Split(string script, bool mySqlSyntax)
    {
        var delimiter = ";";
        var current = new StringBuilder();
        var i = 0;
        var atLineStart = true;
        while (i < script.Length)
        {
            var c = script[i];

            if (mySqlSyntax && atLineStart && IsDelimiterDirective(script, i, out var newDelimiter, out var end))
            {
                var pending = current.ToString().Trim();
                if (pending.Length > 0) yield return pending;
                current.Clear();
                delimiter = newDelimiter;
                i = end;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                current.Append(c);
                if (c == '\n') atLineStart = true;
                i++;
                continue;
            }
            atLineStart = false;

            if (string.CompareOrdinal(script, i, delimiter, 0, delimiter.Length) == 0)
            {
                var statement = current.ToString().Trim();
                if (statement.Length > 0) yield return statement;
                current.Clear();
                i += delimiter.Length;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                i = CopyQuoted(script, i, c, current, mySqlSyntax && c != '`');
                continue;
            }
            if ((c == '-' && Peek(script, i + 1) == '-') || (mySqlSyntax && c == '#'))
            {
                var stop = script.IndexOf('\n', i);
                i = stop < 0 ? script.Length : stop;
                continue;
            }
            if (c == '/' && Peek(script, i + 1) == '*')
            {
                var stop = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var next = stop < 0 ? script.Length : stop + 2;
                // I commenti condizionali MySQL /*! … */ sono codice eseguibile: si conservano.
                if (mySqlSyntax && Peek(script, i + 2) == '!') current.Append(script, i, next - i);
                i = next;
                continue;
            }
            if (!mySqlSyntax && c == '$' && TryReadDollarTag(script, i, out var tag))
            {
                var stop = script.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                var next = stop < 0 ? script.Length : stop + tag.Length;
                current.Append(script, i, next - i);
                i = next;
                continue;
            }
            current.Append(c);
            i++;
        }
        var last = current.ToString().Trim();
        if (last.Length > 0) yield return last;
    }

    private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';

    private static int CopyQuoted(string text, int start, char quote, StringBuilder target, bool backslashEscapes)
    {
        target.Append(quote);
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            target.Append(c);
            i++;
            if (backslashEscapes && c == '\\' && i < text.Length)
            {
                target.Append(text[i]);
                i++;
                continue;
            }
            if (c != quote) continue;
            if (Peek(text, i) == quote)
            {
                target.Append(quote);
                i++;
                continue;
            }
            break;
        }
        return i;
    }

    private static bool TryReadDollarTag(string text, int start, out string tag)
    {
        tag = string.Empty;
        var i = start + 1;
        if (i < text.Length && char.IsDigit(text[i])) return false; // $1 = parametro posizionale
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
        if (i >= text.Length || text[i] != '$') return false;
        tag = text.Substring(start, i - start + 1);
        return true;
    }

    private static bool IsDelimiterDirective(string text, int start, out string delimiter, out int end)
    {
        delimiter = string.Empty;
        end = start;
        const string keyword = "DELIMITER";
        if (start + keyword.Length >= text.Length || string.Compare(text, start, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        if (!char.IsWhiteSpace(text[start + keyword.Length])) return false;
        var lineEnd = text.IndexOf('\n', start);
        if (lineEnd < 0) lineEnd = text.Length;
        var value = text[(start + keyword.Length)..lineEnd].Trim();
        if (value.Length == 0) return false;
        delimiter = value;
        end = lineEnd;
        return true;
    }
}
