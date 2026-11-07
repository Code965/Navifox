using MosaicDB.Core;
using MosaicDB.Dialects;
using Xunit;

namespace MosaicDB.Tests;

public sealed class SqlScriptSplitterTests
{
    [Fact]
    public void SplitsOnSemicolonsOutsideQuotes()
    {
        var statements = SqlScriptSplitter.Split("SELECT 'a;b'; SELECT `x;y` FROM t;\nSELECT \"c;d\"", mySqlSyntax: true).ToArray();
        Assert.Equal(["SELECT 'a;b'", "SELECT `x;y` FROM t", "SELECT \"c;d\""], statements);
    }

    [Fact]
    public void IgnoresCommentsButKeepsMySqlConditionalComments()
    {
        var script = "-- commento;\n# altro;\n/* blocco; */ SELECT 1;\n/*!40101 SET NAMES utf8mb4 */;";
        var statements = SqlScriptSplitter.Split(script, mySqlSyntax: true).ToArray();
        Assert.Equal(["SELECT 1", "/*!40101 SET NAMES utf8mb4 */"], statements);
    }

    [Fact]
    public void HonoursMySqlDelimiterDirective()
    {
        var script = "DELIMITER $$\nCREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END$$\nDELIMITER ;\nSELECT 3;";
        var statements = SqlScriptSplitter.Split(script, mySqlSyntax: true).ToArray();
        Assert.Equal(["CREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END", "SELECT 3"], statements);
    }

    [Fact]
    public void HandlesBackslashEscapesInMySqlStrings()
    {
        var statements = SqlScriptSplitter.Split(@"INSERT INTO t VALUES ('it\'s; ok'); SELECT 1;", mySqlSyntax: true).ToArray();
        Assert.Equal(2, statements.Length);
        Assert.Equal(@"INSERT INTO t VALUES ('it\'s; ok')", statements[0]);
    }

    [Fact]
    public void KeepsPostgresDollarQuotedBodiesTogether()
    {
        var script = "CREATE FUNCTION f() RETURNS int AS $body$ BEGIN RETURN 1; END; $body$ LANGUAGE plpgsql;\nSELECT $1;";
        var statements = SqlScriptSplitter.Split(script, mySqlSyntax: false).ToArray();
        Assert.Equal(2, statements.Length);
        Assert.Contains("RETURN 1; END;", statements[0]);
    }

    [Fact]
    public void MySqlLiteralsEscapeQuotesAndBinary()
    {
        var provider = new MySqlDialectProvider();
        Assert.Equal(@"'it\'s \\ ok'", provider.FormatLiteral(@"it's \ ok"));
        Assert.Equal("0x0AFF", provider.FormatLiteral(new byte[] { 0x0A, 0xFF }));
        Assert.Equal("NULL", provider.FormatLiteral(null));
        Assert.Equal("1.5", provider.FormatLiteral(1.5m));
    }

    [Fact]
    public void PostgresLiteralsDoubleQuotes()
    {
        var provider = new PostgreSqlDialectProvider();
        Assert.Equal("'it''s'", provider.FormatLiteral("it's"));
        Assert.Equal("TRUE", provider.FormatLiteral(true));
        Assert.Equal("'\\x0AFF'::bytea", provider.FormatLiteral(new byte[] { 0x0A, 0xFF }));
    }
}
