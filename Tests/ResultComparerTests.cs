using Navifox.Core;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.Tests;

public sealed class ResultComparerTests
{
    private static QueryResult R(string[] columns, params object?[][] rows) =>
        new(columns.Select(c => new ColumnInfo(c, "text", true)).ToArray(), rows.Select(r => (IReadOnlyList<object?>)r).ToArray(), 0, false);

    [Fact]
    public void UgualiAncheInOrdineDiverso()
    {
        var a = R(["id", "nome"], [1, "A"], [2, "B"]);
        var b = R(["ID", "nome"], [2L, "B"], [1m, "A"]);
        var diff = ResultComparer.Compare([a, b]);
        Assert.True(ResultComparer.AllEqual(diff));
    }

    [Fact]
    public void SegnalaRigheDiverseEMancanti()
    {
        var dev = R(["id", "stato"], [1, "ok"], [2, "ok"], [3, null]);
        var prod = R(["id", "stato"], [1, "ok"], [2, "KO"]);
        var diff = ResultComparer.Compare([dev, prod, null]);
        Assert.False(ResultComparer.AllEqual(diff));
        Assert.Equal(0, diff[0].OnlyHere);
        Assert.Equal(1, diff[1].OnlyHere);
        Assert.Equal([1], diff[1].DifferentRows);
        Assert.Equal(2, diff[1].Missing); // (2,ok) e (3,NULL)
        Assert.False(diff[2].SameColumns); // errore su quel server
    }
}
