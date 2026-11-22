using Navifox.Core.Models;
using Navifox.Dialects;
using Xunit;

namespace Navifox.Tests;

public sealed class DialectProviderTests
{
    [Fact]
    public void MySqlQuotesEmbeddedBackticks()
    {
        var provider = new MySqlDialectProvider();
        Assert.Equal("`we``ird`", provider.QuoteIdentifier("we`ird"));
    }

    [Fact]
    public void PostgreSqlQuotesEmbeddedQuotes()
    {
        var provider = new PostgreSqlDialectProvider();
        Assert.Equal("\"a\"\"b\"", provider.QuoteIdentifier("a\"b"));
    }

    [Fact]
    public void GliInsertPostgreSqlPossonoConservareGliIdIdentityDuranteLaCopia()
    {
        var sql = new PostgreSqlDialectProvider().BuildInsert("portfolio", "child", ["id", "name"], [new object?[] { 30, "O'Brien" }]);
        Assert.StartsWith("INSERT INTO \"portfolio\".\"child\" (\"id\", \"name\") OVERRIDING SYSTEM VALUE VALUES", sql);
        Assert.Contains("(30, 'O''Brien')", sql);
        Assert.DoesNotContain("OVERRIDING", new MySqlDialectProvider().BuildInsert("app", "child", ["id"], [new object?[] { 30 }]));
    }

    [Theory]
    [InlineData(DatabaseKind.MySql, 3306)]
    [InlineData(DatabaseKind.PostgreSql, 5432)]
    public void UsesStandardPortWhenPortIsOmitted(DatabaseKind kind, int expectedPort)
    {
        Assert.Equal(expectedPort, new ConnectionProfile { Kind = kind }.EffectivePort);
    }
}
