using Navifox.Core;
using Xunit;

namespace Navifox.Tests;

public sealed class SqlSafetyTests
{
    [Theory]
    [InlineData("SELECT * FROM t", SqlRisk.Read)]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", SqlRisk.Read)]
    [InlineData("SHOW TABLES", SqlRisk.Read)]
    [InlineData("UPDATE t SET a = 1 WHERE id = 3", SqlRisk.Write)]
    [InlineData("INSERT INTO t VALUES (1)", SqlRisk.Write)]
    [InlineData("ALTER TABLE t ADD c INT", SqlRisk.Write)]
    [InlineData("UPDATE t SET a = 1", SqlRisk.Dangerous)]
    [InlineData("DELETE FROM t", SqlRisk.Dangerous)]
    [InlineData("DELETE FROM t -- WHERE id = 1", SqlRisk.Dangerous)]
    [InlineData("UPDATE t SET note = 'WHERE'", SqlRisk.Dangerous)]
    [InlineData("DROP TABLE t", SqlRisk.Dangerous)]
    [InlineData("truncate table t", SqlRisk.Dangerous)]
    [InlineData("WITH old AS (SELECT id FROM t) DELETE FROM t", SqlRisk.Dangerous)]
    public void ClassifiesStatements(string sql, SqlRisk expected) => Assert.Equal(expected, SqlSafety.Classify(sql).Risk);

    [Fact]
    public void AnalyzesEveryStatementOfAScript() =>
        Assert.Equal([SqlRisk.Read, SqlRisk.Dangerous, SqlRisk.Write],
            SqlSafety.Analyze("SELECT 1; DELETE FROM a; INSERT INTO b VALUES (2);", mySqlSyntax: true).Select(r => r.Risk));
}
