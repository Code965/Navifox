using Navifox.Core;
using Xunit;

namespace Navifox.Tests;

public sealed class SelectionStatsTests
{
    [Fact]
    public void NumeriSommaMediaMinMax()
    {
        var stats = SelectionStats.Compute([10, 20.5m, 3L, null, "4"]);
        Assert.Equal(5, stats.Count);
        Assert.Equal(1, stats.NullCount);
        Assert.Equal(4, stats.NumericCount);
        Assert.Equal(37.5m, stats.Sum);
        Assert.Equal(9.375m, stats.Average);
        Assert.Equal(3m, stats.Min);
        Assert.Equal(20.5m, stats.Max);
        Assert.Equal("Conteggio 5  ·  Somma 37,5  ·  Media 9,375  ·  Min 3  ·  Max 20,5  ·  NULL 1", stats.Format());
    }

    [Fact]
    public void TestoDateEMisti()
    {
        Assert.Equal("Conteggio 3  ·  Distinti 2", SelectionStats.Compute(["Roma", "Milano", "Roma"]).Format());
        Assert.Equal("Conteggio 2  ·  Min 01/03/2024  ·  Max 05/03/2024 10:30:00",
            SelectionStats.Compute([new DateTime(2024, 3, 5, 10, 30, 0), new DateTime(2024, 3, 1)]).Format());
        Assert.Equal("Conteggio 3  ·  Somma 1.500  ·  Media 750  ·  Min 500  ·  Max 1.000  ·  2 numerici",
            SelectionStats.Compute([1000, "n/d", 500]).Format());
        Assert.Equal(string.Empty, SelectionStats.Compute([42]).Format());
        Assert.Null(SelectionStats.Compute([true, false]).Average);
    }
}
