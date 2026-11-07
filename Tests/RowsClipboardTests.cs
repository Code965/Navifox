using System.Numerics;
using Navifox.Core;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.Tests;

public sealed class RowsClipboardTests
{
    [Fact]
    public void GliAppuntiMantengonoNullTestoVuotoBinariDateENumeriPrecisi()
    {
        object?[] values = [null, "", "(NULL)", "\\N", "riga\tcon\nvirgolette \"", new byte[] { 0, 128, 255 }, 9223372036854775807L,
            18446744073709551615UL, 123456789.1234567890123456789m, true, DateTime.Parse("2026-11-07T12:34:56.1234567Z", null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse("2026-11-07T12:34:56.1234567+02:00"), Guid.Parse("a61d1446-3ec7-452f-a144-249fbd2d6c0f")];
        var payload = new GridRowsClipboard(Enumerable.Range(0, values.Length).Select(i => "c" + i).ToArray(), [values.Select(ClipboardCell.From).ToArray()]);
        var restored = GridRowsClipboard.Deserialize(payload.Serialize());
        for (var i = 0; i < values.Length; i++)
        {
            var value = restored.Rows[0][i].ToValue();
            if (values[i] is byte[] bytes) Assert.Equal(bytes, Assert.IsType<byte[]>(value));
            else if (values[i] is long or ulong) Assert.Equal(Convert.ToString(values[i], System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            else Assert.Equal(values[i], value);
        }
    }

    [Fact]
    public void AbbinaPerNomeESaltaSoloLeColonneGenerate()
    {
        var data = new GridRowsClipboard(["id", "nome", "foto", "calcolato"], [[ClipboardCell.From(9), ClipboardCell.From("Ada"), ClipboardCell.From(new byte[] { 1, 2 }), ClipboardCell.From("derivato")]]);
        ColumnInfo[] columns = [new("foto", "BLOB", true), new("id", "INTEGER", false, IsPrimaryKey: true, IsAutoIncrement: true),
            new("nome", "TEXT", false), new("calcolato", "TEXT", true, IsGenerated: true), new("nuovo", "TEXT", true, "'default'")];
        var plan = RowPastePlan.Create(data, columns);
        Assert.Equal("Ada", plan.Rows[0][2]);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(plan.Rows[0][0]));
        Assert.False(plan.Rows[0].ContainsKey(1));
        Assert.False(plan.Rows[0].ContainsKey(3));
        Assert.False(plan.Rows[0].ContainsKey(4));
        Assert.Empty(plan.IgnoredColumns);
        Assert.Empty(plan.RequiredColumns);
    }

    [Fact]
    public void ConservaChiaviNaturaliEIndividuaCampiObbligatoriMancanti()
    {
        var source = new GridRowsClipboard(["code", "extra"], [[ClipboardCell.From("OL123"), ClipboardCell.From("non mappata")]]);
        ColumnInfo[] target = [new("code", "VARCHAR", false, IsPrimaryKey: true), new("nome", "TEXT", false)];
        var plan = RowPastePlan.Create(source, target);
        Assert.Equal("OL123", plan.Rows[0][0]);
        Assert.Contains("nome", plan.RequiredColumns);
        Assert.Contains("extra", plan.IgnoredColumns);
    }

    [Fact]
    public void LaCorrispondenzaIgnoraIlCasoSoloSeNonAmbigua()
    {
        var data = new GridRowsClipboard(["NOME"], [[ClipboardCell.From("test")]]);
        var plan = RowPastePlan.Create(data, [new("nome", "TEXT", false)]);
        Assert.Equal("test", plan.Rows[0][0]);
        Assert.Throws<InvalidOperationException>(() => RowPastePlan.Create(data, [new("nome", "TEXT", true), new("Nome", "TEXT", true)]));
    }

    [Fact]
    public void LaValidazioneNonTroncaNumeriOValoriIncompatibili()
    {
        const string precise = "0.123456789012345678901234567890123456789";
        var plan = RowPastePlan.Create(new(["n"], [[ClipboardCell.From(precise)]]), [new("n", "DECIMAL(65,40)", false)]);
        Assert.Equal(precise, plan.Rows[0][0]);
        var bad = new GridRowsClipboard(["n"], [[ClipboardCell.From("1")], [ClipboardCell.From("non è un numero")]]);
        var error = Assert.Throws<FormatException>(() => RowPastePlan.Create(bad, [new("n", "INTEGER", false)]));
        Assert.Contains("Riga 2", error.Message);
    }

    [Fact]
    public void RifiutaFormatiMalformatiSenzaInterpretareSql()
    {
        Assert.Throws<FormatException>(() => GridRowsClipboard.Deserialize("{\"Columns\":[\"a\"],\"Rows\":[[]]}"));
        Assert.Throws<FormatException>(() => GridRowsClipboard.Deserialize("{\"Version\":2,\"Columns\":[\"a\"],\"Rows\":[[{\"Type\":\"string\",\"Value\":\"x\"}]]}"));
        var sql = "INSERT INTO t VALUES(1); DROP TABLE t;";
        var plan = RowPastePlan.Create(new(["name"], [[ClipboardCell.From(sql)]]), [new("name", "TEXT", true)]);
        Assert.Equal(sql, plan.Rows[0][0]);
    }

    [Fact]
    public void ImpedisceConversioniIntereCheTroncherebberoLeCifre()
    {
        var big = new GridRowsClipboard(["n"], [[ClipboardCell.From("18446744073709551615")]]);
        Assert.Throws<FormatException>(() => RowPastePlan.Create(big, [new("n", "INTEGER", false)], DatabaseKind.Sqlite));
        var unsigned = RowPastePlan.Create(big, [new("n", "bigint unsigned", false)], DatabaseKind.MySql);
        Assert.Equal(BigInteger.Parse("18446744073709551615"), unsigned.Rows[0][0]);
        var fraction = new GridRowsClipboard(["n"], [[ClipboardCell.From(1.5m)]]);
        Assert.Throws<FormatException>(() => RowPastePlan.Create(fraction, [new("n", "INTEGER", false)], DatabaseKind.Sqlite));
    }
}
