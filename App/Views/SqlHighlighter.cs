using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Navifox.App.Views;

/// <summary>Evidenziazione sintattica minima per mostrare DDL (parole chiave, stringhe, identificatori, numeri, commenti).</summary>
public static class SqlHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "TABLE", "VIEW", "OR", "REPLACE", "AS", "SELECT", "FROM", "WHERE", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "ON", "AND", "NOT", "NULL",
        "DEFAULT", "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "UNIQUE", "INDEX", "CONSTRAINT", "CHECK", "ENGINE", "CHARSET", "CHARACTER", "SET", "COLLATE",
        "AUTO_INCREMENT", "COMMENT", "ALTER", "ADD", "SEQUENCE", "IF", "EXISTS", "USING", "BTREE", "HASH", "CASCADE", "RESTRICT", "UPDATE", "DELETE",
        "GENERATED", "ALWAYS", "BY", "IDENTITY", "ROW_FORMAT", "ALGORITHM", "DEFINER", "SQL", "SECURITY", "INVOKER", "WITH", "OPTION", "UNSIGNED",
        "ZEROFILL", "CURRENT_TIMESTAMP", "ON", "NO", "ACTION", "GROUP", "ORDER", "LIMIT", "UNION", "ALL", "DISTINCT", "CASE", "WHEN", "THEN", "ELSE", "END",
        "INT", "INTEGER", "BIGINT", "SMALLINT", "TINYINT", "MEDIUMINT", "VARCHAR", "CHAR", "TEXT", "LONGTEXT", "MEDIUMTEXT", "TINYTEXT", "DATETIME",
        "TIMESTAMP", "DATE", "TIME", "YEAR", "DECIMAL", "NUMERIC", "FLOAT", "DOUBLE", "REAL", "BOOLEAN", "BOOL", "BLOB", "LONGBLOB", "JSON", "JSONB",
        "UUID", "SERIAL", "BIGSERIAL", "BYTEA", "ENUM", "ZONE", "VARYING", "PRECISION", "TRUE", "FALSE", "STORED", "VIRTUAL", "SELECT", "IN", "IS",
    };

    public static IEnumerable<Inline> Highlight(string sql, IBrush keyword, IBrush text, IBrush literal, IBrush number, IBrush comment, IBrush identifier)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            int start = i;
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                yield return new Run(sql[start..i]) { Foreground = comment, FontStyle = FontStyle.Italic };
                continue;
            }
            if (c is '\'' or '`' or '"')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == c)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == c) { i += 2; continue; }
                        i++;
                        break;
                    }
                    if (c == '\'' && sql[i] == '\\') i++;
                    i++;
                }
                i = Math.Min(i, sql.Length);
                yield return new Run(sql[start..i]) { Foreground = c == '\'' ? literal : identifier };
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.')) i++;
                yield return new Run(sql[start..i]) { Foreground = number };
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                var word = sql[start..i];
                yield return Keywords.Contains(word) ? new Run(word) { Foreground = keyword } : new Run(word) { Foreground = text };
                continue;
            }
            while (i < sql.Length && !char.IsLetterOrDigit(sql[i]) && sql[i] is not ('\'' or '`' or '"' or '_') && !(sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')) i++;
            if (i == start) i++;
            yield return new Run(sql[start..i]) { Foreground = text };
        }
    }
}
