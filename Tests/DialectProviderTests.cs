using MosaicDB.Core.Models;
using MosaicDB.Dialects;
using Xunit;

namespace MosaicDB.Tests;

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

    [Theory]
    [InlineData(DatabaseKind.MySql, 3306)]
    [InlineData(DatabaseKind.PostgreSql, 5432)]
    public void UsesStandardPortWhenPortIsOmitted(DatabaseKind kind, int expectedPort)
    {
        Assert.Equal(expectedPort, new ConnectionProfile { Kind = kind }.EffectivePort);
    }
}
