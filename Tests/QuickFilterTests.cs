using Navifox.Core;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.Tests;

public sealed class QuickFilterTests
{
    private static string Lit(object? value) => value is null ? "NULL" : "'" + value.ToString()!.Replace("'", "''") + "'";

    [Theory]
    [InlineData(">100", true, DatabaseKind.MySql, "`c` > 100")]
    [InlineData(">= 2,5", true, DatabaseKind.MySql, "`c` >= 2.5")]
    [InlineData("10..20", true, DatabaseKind.MySql, "`c` BETWEEN 10 AND 20")]
    [InlineData("Roma*", false, DatabaseKind.MySql, "`c` LIKE 'Roma%'")]
    [InlineData("Roma*", false, DatabaseKind.PostgreSql, "CAST(`c` AS TEXT) ILIKE 'Roma%'")]
    [InlineData("ros", false, DatabaseKind.Sqlite, "`c` LIKE '%ros%'")]
    [InlineData("42", true, DatabaseKind.SqlServer, "`c` = 42")]
    [InlineData("4*", true, DatabaseKind.SqlServer, "CAST(`c` AS NVARCHAR(100)) LIKE '4%'")]
    [InlineData("null", false, DatabaseKind.MySql, "`c` IS NULL")]
    [InlineData("!null", false, DatabaseKind.MySql, "`c` IS NOT NULL")]
    [InlineData("!Roma", false, DatabaseKind.MySql, "(`c` <> 'Roma' OR `c` IS NULL)")]
    [InlineData("=O'Neil", false, DatabaseKind.MySql, "`c` = 'O''Neil'")]
    [InlineData("<2024-01-01", false, DatabaseKind.MySql, "`c` < '2024-01-01'")]
    public void GeneraSql(string text, bool numeric, DatabaseKind kind, string expected) =>
        Assert.Equal(expected, QuickFilter.ToSql(QuickFilter.Parse(text)!, "`c`", numeric, kind, Lit));

    [Fact]
    public void VuotoNessunFiltro() => Assert.Null(QuickFilter.Parse("   "));

    [Theory]
    [InlineData(">100", 150, true)]
    [InlineData(">100", 50, false)]
    [InlineData("10..20", 20, true)]
    [InlineData("Roma*", "romagna", true)]
    [InlineData("Roma*", "Bologna", false)]
    [InlineData("log", "Bologna", true)]
    [InlineData("null", null, true)]
    [InlineData("!null", null, false)]
    [InlineData("!Roma", null, true)]
    [InlineData("!Roma", "roma", false)]
    [InlineData("5", 15, false)]
    [InlineData("15", 15L, true)]
    [InlineData("12,5", 12.5, true)]
    public void ConfrontoLatoClient(string text, object? value, bool expected) =>
        Assert.Equal(expected, QuickFilter.Matches(QuickFilter.Parse(text)!, value));

    [Fact]
    public void DateLatoClient()
    {
        Assert.True(QuickFilter.Matches(QuickFilter.Parse(">=2024-03-01")!, new DateTime(2024, 3, 5)));
        Assert.False(QuickFilter.Matches(QuickFilter.Parse("<2024-03-01")!, new DateTime(2024, 3, 5)));
        Assert.True(QuickFilter.Matches(QuickFilter.Parse("2024-03")!, new DateTime(2024, 3, 5)));
    }
}
