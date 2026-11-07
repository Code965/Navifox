using System.Text;

namespace MosaicDB.App.Services;

/// <summary>Formattatore SQL («Formatta SQL»): parole chiave maiuscole, una clausola per riga, campi e condizioni indentati.</summary>
public static class SqlFormatter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "NULL", "IS", "IN", "LIKE", "ILIKE", "BETWEEN", "EXISTS", "AS", "ON", "USING", "JOIN", "INNER", "LEFT",
        "RIGHT", "FULL", "OUTER", "CROSS", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "DISTINCT", "UNION", "ALL", "INSERT", "INTO", "VALUES",
        "UPDATE", "SET", "DELETE", "CREATE", "ALTER", "DROP", "TABLE", "INDEX", "VIEW", "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "DEFAULT", "CONSTRAINT",
        "UNIQUE", "CASE", "WHEN", "THEN", "ELSE", "END", "ASC", "DESC", "WITH", "RETURNING", "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "CAST",
        "TRUE", "FALSE", "IF", "ADD", "COLUMN", "EXPLAIN", "SHOW", "BEGIN", "COMMIT", "ROLLBACK", "TRUNCATE", "INTERVAL", "NOW", "CONCAT", "LOWER", "UPPER",
    };

    private static readonly string[][] Clauses =
    [
        ["GROUP", "BY"], ["ORDER", "BY"], ["UNION", "ALL"], ["INSERT", "INTO"], ["DELETE", "FROM"],
        ["LEFT", "OUTER", "JOIN"], ["RIGHT", "OUTER", "JOIN"], ["FULL", "OUTER", "JOIN"],
        ["LEFT", "JOIN"], ["RIGHT", "JOIN"], ["INNER", "JOIN"], ["CROSS", "JOIN"], ["FULL", "JOIN"],
        ["SELECT"], ["FROM"], ["WHERE"], ["HAVING"], ["LIMIT"], ["OFFSET"], ["UNION"], ["VALUES"], ["UPDATE"], ["SET"], ["JOIN"], ["RETURNING"], ["WITH"],
    ];

    private enum Kind { Word, Symbol, Text, Comment, Space }

    private sealed record Token(Kind Kind, string Value);

    public static string Format(string sql, bool mySql)
    {
        var tokens = Tokenize(sql, mySql).Where(t => t.Kind != Kind.Space).ToList();
        var output = new StringBuilder();
        var depth = 0;
        var clauseStack = new Stack<string>();
        var clause = string.Empty;
        var lineStart = true;

        void NewLine(int extraIndent = 0)
        {
            TrimEnd(output);
            if (output.Length > 0) output.Append('\n');
            output.Append(new string(' ', Math.Max(0, depth * 4 + extraIndent)));
            lineStart = true;
        }

        void Write(string text, bool spaceBefore = true)
        {
            if (!lineStart && spaceBefore && output.Length > 0 && output[^1] is not ('(' or ' ' or '\n' or '.')) output.Append(' ');
            output.Append(text);
            lineStart = false;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == Kind.Comment)
            {
                Write(token.Value);
                if (token.Value.StartsWith("--") || token.Value.StartsWith('#')) NewLine();
                continue;
            }
            if (token.Kind == Kind.Word && MatchClause(tokens, i) is { } words)
            {
                var name = string.Join(' ', words.Select(w => w.ToUpperInvariant()));
                if (!(name == "SET" && clause == "UPDATE" && false))
                {
                    if (output.Length > 0) NewLine();
                    Write(name, spaceBefore: false);
                    clause = name;
                    i += words.Length - 1;
                    if (name is "SELECT" or "SET" or "GROUP BY" or "ORDER BY") NewLine(4);
                    continue;
                }
            }
            if (token.Kind == Kind.Word && token.Value.Equals("AND", StringComparison.OrdinalIgnoreCase) && clause is "WHERE" or "HAVING" && !InBetween(tokens, i)
                || token.Kind == Kind.Word && token.Value.Equals("OR", StringComparison.OrdinalIgnoreCase) && clause is "WHERE" or "HAVING")
            {
                NewLine(4);
                Write(token.Value.ToUpperInvariant(), spaceBefore: false);
                continue;
            }
            switch (token.Value)
            {
                case ",":
                    output.Append(',');
                    lineStart = false;
                    if (clause is "SELECT" or "SET" or "GROUP BY" or "ORDER BY" && depth == clauseStack.Count) NewLine(4);
                    continue;
                case "(":
                    Write("(", spaceBefore: i > 0 && tokens[i - 1].Kind != Kind.Word || IsKeyword(tokens[i - 1].Value) && !IsFunction(tokens[i - 1].Value));
                    var subquery = i + 1 < tokens.Count && tokens[i + 1].Value.Equals("SELECT", StringComparison.OrdinalIgnoreCase);
                    if (subquery)
                    {
                        clauseStack.Push(clause);
                        depth++;
                    }
                    else clauseStack.Push("(" + clause);
                    continue;
                case ")":
                    if (clauseStack.Count > 0)
                    {
                        var saved = clauseStack.Pop();
                        if (!saved.StartsWith('('))
                        {
                            depth = Math.Max(0, depth - 1);
                            NewLine();
                            clause = saved;
                        }
                        else clause = saved[1..];
                    }
                    output.Append(')');
                    lineStart = false;
                    continue;
                case ";":
                    TrimEnd(output);
                    output.Append(";\n");
                    depth = 0;
                    clauseStack.Clear();
                    clause = string.Empty;
                    lineStart = true;
                    continue;
                case ".":
                    TrimEnd(output);
                    output.Append('.');
                    lineStart = false;
                    continue;
            }
            var text = token.Kind == Kind.Word && IsKeyword(token.Value) && !(i > 0 && tokens[i - 1].Value == ".") ? token.Value.ToUpperInvariant() : token.Value;
            Write(text, spaceBefore: !(i > 0 && tokens[i - 1].Value == "."));
        }
        return output.ToString().Trim() + (sql.TrimEnd().EndsWith(';') ? string.Empty : string.Empty);
    }

    private static bool IsKeyword(string word) => Keywords.Contains(word);
    private static bool IsFunction(string word) => word.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" or "COALESCE" or "CAST" or "NOW" or "CONCAT" or "LOWER" or "UPPER";

    private static bool InBetween(List<Token> tokens, int index)
    {
        for (var i = index - 1; i >= Math.Max(0, index - 4); i--)
            if (tokens[i].Value.Equals("BETWEEN", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string[]? MatchClause(List<Token> tokens, int index)
    {
        foreach (var clause in Clauses)
        {
            if (index + clause.Length > tokens.Count) continue;
            var ok = true;
            for (var k = 0; k < clause.Length && ok; k++) ok = tokens[index + k].Kind == Kind.Word && tokens[index + k].Value.Equals(clause[k], StringComparison.OrdinalIgnoreCase);
            if (ok) return tokens.Skip(index).Take(clause.Length).Select(t => t.Value).ToArray();
        }
        return null;
    }

    private static void TrimEnd(StringBuilder builder)
    {
        while (builder.Length > 0 && builder[^1] == ' ') builder.Length--;
    }

    private static IEnumerable<Token> Tokenize(string sql, bool mySql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var start = i;
            if (char.IsWhiteSpace(c))
            {
                while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
                yield return new Token(Kind.Space, " ");
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-' || mySql && c == '#')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                yield return new Token(Kind.Comment, sql[start..i].TrimEnd());
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                yield return new Token(Kind.Comment, sql[start..i]);
                continue;
            }
            if (c is '\'' or '"' or '`')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\\' && c == '\'' && mySql) { i += 2; continue; }
                    if (sql[i] == c)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == c) { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                i = Math.Min(i, sql.Length);
                yield return new Token(Kind.Text, sql[start..i]);
                continue;
            }
            if (char.IsLetterOrDigit(c) || c is '_' or '@' or '$')
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '$' || sql[i] == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1]) && char.IsDigit(sql[start]))) i++;
                yield return new Token(Kind.Word, sql[start..i]);
                continue;
            }
            if (i + 1 < sql.Length && (sql.Substring(i, 2) is "<=" or ">=" or "<>" or "!=" or "::" or "||" or "->"))
            {
                i += 2;
                yield return new Token(Kind.Symbol, sql[start..i]);
                continue;
            }
            i++;
            yield return new Token(Kind.Symbol, c.ToString());
        }
    }
}

public sealed record SqlSnippet(string Name, string Icon, Func<bool, string> Build);

/// <summary>Modelli di codice («Snippet»), adattati al dialetto.</summary>
public static class SqlSnippets
{
    public static IReadOnlyList<SqlSnippet> All { get; } =
    [
        new("SELECT", "table", _ => "SELECT *\nFROM tabella\nWHERE condizione\nORDER BY colonna\nLIMIT 100;"),
        new("SELECT con JOIN", "link", _ => "SELECT a.*, b.colonna\nFROM tabella_a a\n    INNER JOIN tabella_b b ON b.id = a.b_id\nWHERE a.colonna = 'valore';"),
        new("Raggruppa e conta", "hash", _ => "SELECT colonna, COUNT(*) AS totale\nFROM tabella\nGROUP BY colonna\nHAVING COUNT(*) > 1\nORDER BY totale DESC;"),
        new("INSERT", "plus", _ => "INSERT INTO tabella (colonna1, colonna2)\nVALUES ('valore1', 'valore2');"),
        new("UPDATE", "pencil", _ => "UPDATE tabella\nSET colonna = 'valore'\nWHERE id = 1;"),
        new("DELETE", "trash", _ => "DELETE FROM tabella\nWHERE id = 1;"),
        new("CREATE TABLE", "table", mysql => mysql
            ? "CREATE TABLE nuova_tabella (\n    id INT UNSIGNED NOT NULL AUTO_INCREMENT,\n    nome VARCHAR(255) NOT NULL,\n    creato_il DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,\n    PRIMARY KEY (id)\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;"
            : "CREATE TABLE nuova_tabella (\n    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,\n    nome TEXT NOT NULL,\n    creato_il TIMESTAMPTZ NOT NULL DEFAULT now()\n);"),
        new("Aggiungi colonna", "column", mysql => mysql
            ? "ALTER TABLE tabella\n    ADD COLUMN nuova_colonna VARCHAR(255) NULL AFTER colonna;"
            : "ALTER TABLE tabella\n    ADD COLUMN nuova_colonna TEXT NULL;"),
        new("Indice", "key", _ => "CREATE INDEX idx_tabella_colonna ON tabella (colonna);"),
        new("Chiave esterna", "link", _ => "ALTER TABLE figlia\n    ADD CONSTRAINT fk_figlia_padre FOREIGN KEY (padre_id) REFERENCES padre (id)\n    ON DELETE CASCADE;"),
        new("CTE (WITH)", "layers", _ => "WITH filtrati AS (\n    SELECT *\n    FROM tabella\n    WHERE condizione\n)\nSELECT *\nFROM filtrati;"),
        new("CASE", "keyword", _ => "CASE\n    WHEN condizione THEN 'valore'\n    ELSE 'altro'\nEND"),
        new("Paginazione", "list", _ => "SELECT *\nFROM tabella\nORDER BY id\nLIMIT 50 OFFSET 0;"),
        new("Transazione", "transaction", mysql => mysql ? "START TRANSACTION;\n\n-- istruzioni\n\nCOMMIT;" : "BEGIN;\n\n-- istruzioni\n\nCOMMIT;"),
    ];
}
