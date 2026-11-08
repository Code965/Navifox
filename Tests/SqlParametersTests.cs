using Navifox.Core;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.Tests;

public sealed class SqlParametersTests
{
    [Fact]
    public void TrovaIParametri()
    {
        var sql = "SELECT * FROM t WHERE a = :id AND b = ${citta} AND c::text = 'x :no' -- :commento\n AND d = :id; SET @x := 1; /* :blocco */";
        Assert.Equal(["id", "citta"], SqlParameters.Names(sql));
        Assert.Equal(3, SqlParameters.Find(sql).Count);
    }

    [Fact]
    public void IgnoraOrariECastENomiQuotati()
    {
        Assert.Empty(SqlParameters.Find("SELECT '10:30', x::int, \"a:b\", `c:d`, [e:f] FROM t"));
        Assert.Empty(SqlParameters.Find("CREATE FUNCTION f() AS $$ SELECT :x $$"));
    }

    [Fact]
    public void SostituisceConLetterali()
    {
        var sql = "SELECT * FROM t WHERE id = :id AND nome = :nome AND x = ${raw} AND y = :id";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = SqlParameters.Literal("42", false, DatabaseKind.MySql),
            ["nome"] = SqlParameters.Literal("O'Neil", false, DatabaseKind.MySql),
            ["raw"] = SqlParameters.Literal("NOW()", true, DatabaseKind.MySql),
        };
        Assert.Equal("SELECT * FROM t WHERE id = 42 AND nome = 'O''Neil' AND x = NOW() AND y = 42", SqlParameters.Substitute(sql, values));
        Assert.Equal("NULL", SqlParameters.Literal("null", false, DatabaseKind.PostgreSql));
        Assert.Equal("'007'", SqlParameters.Literal("007", false, DatabaseKind.PostgreSql)); // codici con zeri iniziali restano testo
        Assert.Equal("N'Roma'", SqlParameters.Literal("Roma", false, DatabaseKind.SqlServer));
        Assert.Equal("-3.5", SqlParameters.Literal("-3.5", false, DatabaseKind.Sqlite));
    }

    [Fact]
    public void RegioniDiPiegatura()
    {
        var text = "-- region Clienti\nSELECT *\nFROM clienti\nWHERE id = 1;\n-- endregion\nSELECT 1;\n/* commento\n su due righe */\nCREATE PROCEDURE p()\nBEGIN\n  IF x THEN\n    BEGIN\n      SELECT 1;\n    END;\n  END IF;\nEND;";
        var folds = SqlFolding.Find(text);
        Assert.Contains(folds, f => f.Title == "Clienti" && text[f.Start..f.End].EndsWith("-- endregion"));
        Assert.Contains(folds, f => text[f.Start..f.End] == "SELECT *\nFROM clienti\nWHERE id = 1;");
        Assert.DoesNotContain(folds, f => text[f.Start..f.End] == "SELECT 1;"); // una sola riga
        Assert.Contains(folds, f => f.Title == "/* … */");
        Assert.Contains(folds, f => text[f.Start..f.End].StartsWith("CREATE PROCEDURE") && text[f.Start..f.End].EndsWith("END;"));
        Assert.Contains(folds, f => f.Title == "BEGIN … END");
    }
}

public sealed class TextSearchTests
{
    [Fact]
    public void TrovaESostituisce()
    {
        var text = "SELECT nome, cognome FROM clienti WHERE nome = 'Nome'";
        Assert.Equal(4, TextSearch.FindAll(text, TextSearch.Build("nome", false, false, false)).Count); // nome, cognome, nome, Nome
        Assert.Equal(3, TextSearch.FindAll(text, TextSearch.Build("nome", false, true, false)).Count);
        Assert.Equal(2, TextSearch.FindAll(text, TextSearch.Build("nome", true, true, false)).Count);
        var (replaced, count) = TextSearch.ReplaceAll(text, TextSearch.Build(@"(\w+)\.?\s*=\s*'(\w+)'", false, false, true), "$1 = '$2_x'", true);
        Assert.Equal(1, count);
        Assert.EndsWith("nome = 'Nome_x'", replaced);
        Assert.Null(TextSearch.Build("([", false, false, true)); // regex non valida
        var matches = TextSearch.FindAll("a b a b a", TextSearch.Build("a", true, false, false));
        Assert.Equal(4, TextSearch.Next(matches, 1, false)!.Index);
        Assert.Equal(0, TextSearch.Next(matches, 9, false)!.Index); // ricomincia dall'inizio
        Assert.Equal(8, TextSearch.Next(matches, 0, true)!.Index);
    }
}
