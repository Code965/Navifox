using Avalonia.Input;
using Navifox.App.Services;
using Navifox.App.ViewModels;
using Navifox.Core;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.AppTests;

public sealed partial class WorkflowTests
{
    private async Task<TableTabViewModel> Table(ConnectionProfile profile, string name, TestDialogs? dialogs = null)
    {
        var table = new TableTabViewModel(fixture.Access, dialogs ?? new TestDialogs(), profile, "main", "main", name);
        await table.LoadAsync();
        Assert.False(table.HasError, table.StatusMessage);
        return table;
    }

    private async Task Execute(ConnectionProfile profile, params string[] statements)
    {
        await using var connection = await fixture.Provider.ConnectAsync(profile, CancellationToken.None);
        foreach (var sql in statements) await fixture.Provider.ExecuteAsync(connection, sql, 0, CancellationToken.None);
    }

    [Fact]
    public async Task FrecciaGiuCreaUnaSolaBozzaInFondoESaltaPagineIntermedie()
    {
        var profile = fixture.Profile("Freccia giù");
        await Execute(profile, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT)", "INSERT INTO t VALUES(1,'prima'),(2,'ultima')");
        var table = await Table(profile, "t");
        Assert.False(table.AddRowAtEnd(table.Rows[0]));
        table.HasMore = true;
        Assert.False(table.AddRowAtEnd(table.Rows[^1]));
        table.HasMore = false;
        Assert.True(table.AddRowAtEnd(table.Rows[^1]));
        Assert.Equal(3, table.Rows.Count);
        var blank = table.CurrentRow!;
        Assert.True(blank.IsNew);
        Assert.True(table.AddRowAtEnd(blank));
        Assert.Equal(3, table.Rows.Count);
        blank.CellAt(1)!.EditText = "nuova";
        Assert.True(table.AddRowAtEnd(blank));
        Assert.Equal(4, table.Rows.Count);
        await using var raw = await fixture.Provider.ConnectAsync(profile, CancellationToken.None);
        Assert.Equal(2, await fixture.Provider.CountRowsAsync(raw, "main", "t", null, CancellationToken.None));
    }

    [Fact]
    public async Task IncollaPerNomePreservaNullEBinariERigeneraGliIdAlSalvataggio()
    {
        var profile = fixture.Profile("Incolla smart");
        await Execute(profile,
            "CREATE TABLE source(id INTEGER PRIMARY KEY, name TEXT NOT NULL, optional TEXT, image BLOB)",
            "INSERT INTO source VALUES(100,'Ada',NULL,X'0080FF'),(101,'Grace','',X'0102')",
            "CREATE TABLE target(image BLOB, id INTEGER PRIMARY KEY, optional TEXT DEFAULT 'predefinito', name TEXT NOT NULL, tag TEXT DEFAULT 'nuovo', computed TEXT GENERATED ALWAYS AS (name || '!') STORED)");
        var source = await Table(profile, "source");
        source.SelectedRows = source.Rows.ToArray();
        var clipboard = GridRowsClipboard.Deserialize(source.CopyRows()!.Serialize());
        var dialogs = new TestDialogs();
        var target = await Table(profile, "target", dialogs);
        Assert.True(target.AddRowAtEnd(null));
        Assert.Equal(2, await target.PasteRowsAsync(clipboard));
        Assert.Equal(2, target.Rows.Count); // La bozza vuota è stata riutilizzata.
        Assert.Equal(0, dialogs.ConfirmCount); // Stessa struttura logica: nessuna finestra superflua.
        Assert.All(target.Rows, r => Assert.True(r.IsNew));
        Assert.Null(target.Rows[0].CellAt(1)!.Value);
        Assert.True(target.Rows[0].CellAt(2)!.IsAssigned);
        Assert.Null(target.Rows[0].CellAt(2)!.Value);
        Assert.Equal("", target.Rows[1].CellAt(2)!.Value);
        await using (var raw = await fixture.Provider.ConnectAsync(profile, CancellationToken.None))
            Assert.Equal(0, await fixture.Provider.CountRowsAsync(raw, "main", "target", null, CancellationToken.None));
        await target.ApplyChangesCommand.ExecuteAsync(null);
        Assert.False(target.HasError, target.StatusMessage);
        Assert.False(target.HasPendingChanges);
        Assert.Equal(2, target.Rows.Count);
        Assert.Equal(1L, target.Rows[0].CellAt(1)!.Value);
        Assert.Equal("Ada", target.Rows[0].CellAt(3)!.Value);
        Assert.Equal(new byte[] { 0, 128, 255 }, Assert.IsType<byte[]>(target.Rows[0].CellAt(0)!.Value));
        Assert.Null(target.Rows[0].CellAt(2)!.Value); // NULL esplicito: non il DEFAULT.
        Assert.Equal("nuovo", target.Rows[0].CellAt(4)!.Value);
        Assert.Equal("Ada!", target.Rows[0].CellAt(5)!.Value);
        Assert.Contains(fixture.Journal.Read(limit: -1), e => e.Sql.Contains("INSERT INTO \"main\".\"target\"") && e.Status == "Succeeded");
    }

    [Fact]
    public async Task ChiaviNaturaliRimangonoELeRigheIncompatibiliNonCambianoLaGriglia()
    {
        var profile = fixture.Profile("Chiavi naturali");
        await Execute(profile, "CREATE TABLE t(code TEXT NOT NULL PRIMARY KEY, value INTEGER)", "INSERT INTO t VALUES('OL123',1)");
        var dialogs = new TestDialogs { Confirm = false };
        var table = await Table(profile, "t", dialogs);
        table.SelectedRows = [table.Rows[0]];
        var duplicate = table.CopyRows()!;
        Assert.Equal(0, await table.PasteRowsAsync(duplicate));
        Assert.Single(table.Rows);
        Assert.Contains("chiavi già presenti", dialogs.LastConfirmation);
        var valid = new GridRowsClipboard(["code", "value"], [[ClipboardCell.From("OL999"), ClipboardCell.From(3)]]);
        Assert.Equal(1, await table.PasteRowsAsync(valid));
        Assert.Equal("OL999", table.Rows[^1].CellAt(0)!.Value);
        var invalid = new GridRowsClipboard(["code", "value"], [[ClipboardCell.From("OL888"), ClipboardCell.From("non numerico")]]);
        Assert.Equal(0, await table.PasteRowsAsync(invalid));
        Assert.Equal(2, table.Rows.Count);
        Assert.NotEmpty(dialogs.Errors);
        table.IsBusy = true;
        Assert.Equal(0, await table.PasteRowsAsync(valid));
    }

    [Fact]
    public async Task AppuntiAvaloniaETextExcelMantengonoColonneETestiMultilinea()
    {
        var payload = new GridRowsClipboard(["code", "name"], [[ClipboardCell.From("a"), ClipboardCell.From("testo\tcon\nvirgolette \"")]]);
        using IAsyncDataTransfer data = RowsClipboard.Create(payload);
        Assert.True(data.Contains(RowsClipboard.Format));
        var restored = await RowsClipboard.ReadAsync(data);
        Assert.Equal(payload.Rows[0][1].Value, restored.Rows[0][1].Value);
        var tsv = await data.TryGetTextAsync();
        var fromText = RowsClipboardText.Parse(tsv!, ["name", "code"]);
        Assert.False(fromText.ByPosition);
        Assert.Equal("testo\tcon\nvirgolette \"", fromText.Rows[0][1].Value);
        var plain = RowsClipboardText.Parse("abc\t42", ["id", "name", "value"], 1);
        Assert.True(plain.ByPosition);
        Assert.Equal(new[] { "name", "value" }, plain.Columns);
    }

    [Fact]
    public async Task IdentitaSqliteSenzaAutoincrementEColonneCalcolateVengonoRiconosciute()
    {
        var profile = fixture.Profile("Metadati inserimento");
        await Execute(profile, "CREATE TABLE normal(id INTEGER PRIMARY KEY, value TEXT, upper_value TEXT GENERATED ALWAYS AS (upper(value)) VIRTUAL)",
            "CREATE TABLE manual(id INTEGER PRIMARY KEY, value TEXT) WITHOUT ROWID");
        var normal = await Table(profile, "normal");
        Assert.True(normal.Columns[0].IsAutoIncrement);
        Assert.True(normal.Columns[2].IsGenerated);
        var manual = await Table(profile, "manual");
        Assert.False(manual.Columns[0].IsAutoIncrement);
        Assert.True(normal.AddRowAtEnd(null));
        normal.SetCurrent(normal.CurrentRow, 2);
        Assert.False(normal.CanEditCurrentCell);
    }
}
