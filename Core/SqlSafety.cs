using System.Text.RegularExpressions;

namespace Navifox.Core;

/// <summary>Tipo di rischio di un'istruzione SQL.</summary>
public enum SqlRisk
{
    /// <summary>Sola lettura (SELECT, SHOW, EXPLAIN…).</summary>
    Read,
    /// <summary>Modifica dati o struttura (INSERT, UPDATE … WHERE, CREATE, ALTER…).</summary>
    Write,
    /// <summary>Distruttiva: UPDATE/DELETE senza WHERE, DROP, TRUNCATE.</summary>
    Dangerous,
}

public sealed record SqlRiskItem(string Statement, SqlRisk Risk, string Reason);

/// <summary>Riconosce le istruzioni che modificano o distruggono dati (modalità sicura e connessioni di produzione).</summary>
public static class SqlSafety
{
    private static readonly Regex Comments = new(@"--[^\n]*|#[^\n]*|/\*(?!!)[\s\S]*?\*/", RegexOptions.Compiled);
    private static readonly Regex Literals = new(@"'(?:[^'\\]|\\.|'')*'|""(?:[^""\\]|\\.)*""|`[^`]*`", RegexOptions.Compiled);
    private static readonly Regex FirstWord = new(@"^\s*\(?\s*(?<w>[A-Za-z]+)", RegexOptions.Compiled);
    private static readonly HashSet<string> ReadOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "SHOW", "EXPLAIN", "DESCRIBE", "DESC", "VALUES", "TABLE", "USE", "SET", "BEGIN", "START", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "HELP", "ANALYZE", "CHECK", "CHECKSUM", "CALL", "DO",
    };

    public static IReadOnlyList<SqlRiskItem> Analyze(string sql, bool mySqlSyntax) =>
        SqlScriptSplitter.Split(sql, mySqlSyntax).Select(Classify).ToArray();

    public static SqlRiskItem Classify(string statement)
    {
        var text = Literals.Replace(Comments.Replace(statement, " "), "''");
        var word = FirstWord.Match(text) is { Success: true } m ? m.Groups["w"].Value.ToUpperInvariant() : string.Empty;
        // WITH … SELECT è lettura; WITH … UPDATE/DELETE/INSERT è scrittura.
        if (word == "WITH")
            word = Regex.Match(text, @"\)\s*(SELECT|INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase | RegexOptions.RightToLeft) is { Success: true } w ? w.Groups[1].Value.ToUpperInvariant() : "SELECT";
        var hasWhere = Regex.IsMatch(text, @"\bWHERE\b", RegexOptions.IgnoreCase);
        return word switch
        {
            "UPDATE" when !hasWhere => new(statement, SqlRisk.Dangerous, "UPDATE senza WHERE: modifica tutte le righe"),
            "DELETE" when !hasWhere => new(statement, SqlRisk.Dangerous, "DELETE senza WHERE: elimina tutte le righe"),
            "DROP" => new(statement, SqlRisk.Dangerous, "DROP: elimina definitivamente l'oggetto"),
            "TRUNCATE" => new(statement, SqlRisk.Dangerous, "TRUNCATE: svuota la tabella"),
            _ when word.Length == 0 || ReadOnly.Contains(word) => new(statement, SqlRisk.Read, string.Empty),
            _ => new(statement, SqlRisk.Write, $"{word}: modifica dati o struttura"),
        };
    }
}
