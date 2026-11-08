using System.Text;
using ClosedXML.Excel;
using Navifox.App.Services;
using Navifox.App.ViewModels;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.AppTests;

public sealed partial class WorkflowTests
{
    private async Task AdvanceImport(ImportWizardViewModel wizard, int target = 7)
    {
        while (wizard.Step < target)
        {
            var before = wizard.Step;
            await wizard.NextCommand.ExecuteAsync(null);
            Assert.True(wizard.Step > before, $"Passaggio {before}: {wizard.ErrorMessage}");
        }
    }

    [Fact]
    public async Task CsvNuovaTabellaSelezionaRigheFormatiETipiSenzaScriverePrimaDiAvvia()
    {
        var profile = fixture.Profile("Importazione CSV");
        var path = Path.Combine(fixture.Root, "file clienti.csv");
        await File.WriteAllTextAsync(path, "Preambolo\r\nNote\r\ncodice;importo;data;nome\r\n001;\"1.234,56\";15/12/2024;\"Riga\r\nmultilinea\"\r\n002;4,25;16/12/2024;Anna\r\n003;99,00;17/12/2024;Fuori intervallo\r\n", new UTF8Encoding(true));
        var dialogs = new TestDialogs();
        var wizard = new ImportWizardViewModel(fixture.Access, dialogs, profile, "main", "main");
        wizard.SetSourceFile(path);
        wizard.SelectedDelimiter = "Punto e virgola (;)";
        wizard.SelectedRecordDelimiter = "CRLF";
        wizard.HeaderRow = 3; wizard.FirstDataRow = 4; wizard.LastDataRow = 5;
        wizard.DecimalSymbol = ",";
        await AdvanceImport(wizard, 4);
        Assert.Equal(ImportDataType.Text, wizard.NewColumns[0].SelectedType.Type);
        Assert.Equal(ImportDataType.Decimal, wizard.NewColumns[1].SelectedType.Type);
        Assert.Equal(ImportDataType.Date, wizard.NewColumns[2].SelectedType.Type);
        wizard.TargetTable = "nuovi clienti";
        wizard.NewColumns[0].Name = "codice cliente";
        await AdvanceImport(wizard);
        Assert.Empty(await fixture.Access.ListTablesAsync(profile, "main", "main", CancellationToken.None));
        await wizard.StartCommand.ExecuteAsync(null);
        Assert.True(wizard.IsFinished, wizard.ErrorMessage);
        Assert.Equal(2, wizard.ImportedRows);
        Assert.True(wizard.TableCreated);
        var rows = await ReadData(profile, "SELECT \"codice cliente\",importo,data,nome FROM \"nuovi clienti\" ORDER BY \"codice cliente\"");
        Assert.Equal("001", rows[0][0]);
        Assert.Equal(1234.56, Convert.ToDouble(rows[0][1]));
        Assert.StartsWith("2024-12-15", (string)rows[0][2]!);
        Assert.Equal("Riga\r\nmultilinea", rows[0][3]);
        Assert.Equal("002", rows[1][0]);
        Assert.Equal(4.25, Convert.ToDouble(rows[1][1]));
        Assert.Empty(dialogs.Errors);
        Assert.Contains(fixture.Journal.Read(limit: -1), e => e.Kind == "Importazione dati" && e.ProfileId == profile.Id && e.Status == "Succeeded");
    }

    [Fact]
    public async Task ExcelSceglieFoglioERigheAssoluteEMantieneValoriNativi()
    {
        var profile = fixture.Profile("Excel nativo");
        var path = Path.Combine(fixture.Root, "fogli.xlsx");
        using (var workbook = new XLWorkbook())
        {
            workbook.AddWorksheet("Ignorato").Cell("A1").Value = "Non usare";
            var sheet = workbook.AddWorksheet("Dati");
            sheet.Cell("B5").Value = "codice"; sheet.Cell("C5").Value = "valore"; sheet.Cell("D5").Value = "giorno";
            sheet.Cell("B6").Value = "0012"; sheet.Cell("C6").Value = 12.5; sheet.Cell("D6").Value = new DateTime(2025, 3, 4);
            sheet.Cell("B7").Value = "0013"; sheet.Cell("C7").Value = 3.25; sheet.Cell("D7").Value = new DateTime(2025, 3, 5);
            sheet.Cell("B8").Value = "ESCLUSO";
            workbook.SaveAs(path);
        }
        var wizard = new ImportWizardViewModel(fixture.Access, new TestDialogs(), profile, "main", "main");
        wizard.SetSourceFile(path);
        Assert.Equal(["Ignorato", "Dati"], wizard.Sheets);
        wizard.SelectedSheet = "Dati";
        wizard.HeaderRow = 5; wizard.FirstDataRow = 6; wizard.LastDataRow = 7;
        wizard.DecimalSymbol = ",";
        wizard.TargetTable = "excel_import";
        await AdvanceImport(wizard);
        await wizard.StartCommand.ExecuteAsync(null);
        Assert.True(wizard.IsFinished, wizard.ErrorMessage);
        var rows = await ReadData(profile, "SELECT codice,valore,giorno FROM excel_import ORDER BY codice");
        Assert.Equal(2, rows.Count);
        Assert.Equal("0012", rows[0][0]);
        Assert.Equal(12.5, Convert.ToDouble(rows[0][1]));
        Assert.StartsWith("2025-03-04", (string)rows[0][2]!);
    }

    [Fact]
    public async Task ImportazioneEsistenteRigeneraIdentityEIgnoraCalcolatePreservandoDefaultEBinari()
    {
        var profile = fixture.Profile("Importazione esistente");
        await Execute(profile, "CREATE TABLE t(id INTEGER PRIMARY KEY,nome TEXT NOT NULL,payload BLOB,tag TEXT DEFAULT 'default',upper_name TEXT GENERATED ALWAYS AS (upper(nome)) STORED)", "INSERT INTO t(nome) VALUES('Esistente')");
        var path = Path.Combine(fixture.Root, "esistente.csv");
        await File.WriteAllTextAsync(path, "id,nome,payload,upper_name\n900,Anna,AID/,NON USARE\n901,Bruno,AQI=,NON USARE\n");
        var columns = await fixture.Access.GetColumnsAsync(profile, "main", "main", "t", CancellationToken.None);
        var wizard = new ImportWizardViewModel(fixture.Access, new TestDialogs(), profile, "main", "main", "t", columns);
        wizard.SetSourceFile(path);
        Assert.False(wizard.CreateNewTable);
        Assert.Equal(ImportFormat.Csv, wizard.SelectedFormat.Format);
        await AdvanceImport(wizard);
        Assert.DoesNotContain(wizard.Mappings, m => m.Target is "id" or "upper_name");
        await wizard.StartCommand.ExecuteAsync(null);
        Assert.True(wizard.IsFinished, wizard.ErrorMessage);
        var rows = await ReadData(profile, "SELECT id,nome,payload,tag,upper_name FROM t ORDER BY id");
        Assert.Equal(3, rows.Count);
        Assert.Equal(2L, rows[1][0]);
        Assert.Equal(new byte[] { 0, 128, 255 }, (byte[])rows[1][2]!);
        Assert.Equal("default", rows[1][3]);
        Assert.Equal("ANNA", rows[1][4]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportazioneErroreDopoIlPrimoBatchRipristinaDatiOStruttura(bool newTable)
    {
        var profile = fixture.Profile("Rollback importazione");
        await Execute(profile, "CREATE TABLE existing(value INTEGER CHECK(value < 250))", "INSERT INTO existing VALUES(77)");
        var path = Path.Combine(fixture.Root, "rollback.csv");
        await File.WriteAllTextAsync(path, "value\n" + string.Join("\n", Enumerable.Range(1, 300)) + "\nnon numerico\n");
        var wizard = new ImportWizardViewModel(fixture.Access, new TestDialogs(), profile, "main", "main", newTable ? null : "existing");
        wizard.SetSourceFile(path);
        wizard.TargetTable = newTable ? "rollback_new" : "existing";
        wizard.ReplaceExisting = !newTable;
        wizard.BatchSize = 200;
        await AdvanceImport(wizard);
        await wizard.StartCommand.ExecuteAsync(null);
        Assert.False(wizard.IsFinished);
        Assert.NotEmpty(wizard.ErrorMessage);
        Assert.Equal(0, wizard.ImportedRows);
        Assert.False(wizard.TableCreated);
        Assert.Equal(77L, Assert.Single(await ReadData(profile, "SELECT value FROM existing"))[0]);
        Assert.DoesNotContain(await fixture.Access.ListTablesAsync(profile, "main", "main", CancellationToken.None), t => t.Name == "rollback_new");
    }

    [Fact]
    public async Task ContinuaSuErroreSaltaConversioniEVincoliConSavepoint()
    {
        var profile = fixture.Profile("Errori importazione");
        await Execute(profile, "CREATE TABLE t(value INTEGER UNIQUE)");
        var path = Path.Combine(fixture.Root, "errori.csv");
        await File.WriteAllTextAsync(path, "value\n1\n1\nnon numerico\n2\n");
        var wizard = new ImportWizardViewModel(fixture.Access, new TestDialogs(), profile, "main", "main", "t") { ContinueOnError = true };
        wizard.SetSourceFile(path);
        await AdvanceImport(wizard);
        await wizard.StartCommand.ExecuteAsync(null);
        Assert.True(wizard.IsFinished, wizard.ErrorMessage);
        Assert.Equal(2, wizard.ImportedRows);
        Assert.Equal(2, (await ReadData(profile, "SELECT value FROM t")).Count);
        Assert.Contains("2 errori", wizard.ProgressText);
    }

    [Fact]
    public async Task NuovaTabellaNonSovrascriveOmonimeERispettaRifiutoProduzioneESolaLettura()
    {
        var profile = fixture.Profile("Protezioni importazione");
        await Execute(profile, "CREATE TABLE t(nome TEXT)", "INSERT INTO t VALUES('Da conservare')");
        var path = Path.Combine(fixture.Root, "protezioni.csv");
        await File.WriteAllTextAsync(path, "nome\nAnna\n");
        var dialogs = new TestDialogs { Confirm = false };
        var wizard = new ImportWizardViewModel(fixture.Access, dialogs, profile with { Environment = "Produzione" }, "main", "main");
        wizard.SetSourceFile(path);
        await AdvanceImport(wizard, 4);
        wizard.TargetTable = "t";
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(4, wizard.Step);
        Assert.Contains("esiste", wizard.ErrorMessage);
        wizard.TargetTable = "non_creata";
        await AdvanceImport(wizard);
        await wizard.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.ConfirmCount);
        Assert.False(wizard.TableCreated);
        Assert.Equal("Da conservare", Assert.Single(await ReadData(profile, "SELECT nome FROM t"))[0]);
        var readOnly = new ImportWizardViewModel(fixture.Access, dialogs, profile with { ReadOnly = true }, "main", "main");
        readOnly.SetSourceFile(path);
        await readOnly.StartCommand.ExecuteAsync(null);
        Assert.Contains("sola lettura", readOnly.ErrorMessage);
    }

    [Fact]
    public async Task EliminazioneMultiplaConfermaUnaVoltaERimuoveCicliSoloNellaSelezione()
    {
        var profile = fixture.Profile("Eliminazione multipla");
        await Execute(profile, "CREATE TABLE a(id INTEGER PRIMARY KEY,b_id INTEGER REFERENCES b(id))", "CREATE TABLE b(id INTEGER PRIMARY KEY,a_id INTEGER REFERENCES a(id))", "INSERT INTO a VALUES(1,NULL)", "INSERT INTO b VALUES(1,1)", "UPDATE a SET b_id=1", "CREATE TABLE keep(value TEXT)", "INSERT INTO keep VALUES('Da conservare')");
        var extra = Enumerable.Range(1, 20).Select(i => "selected_" + i).ToArray();
        await Execute(profile, extra.Select(n => $"CREATE TABLE {n}(id INTEGER)").ToArray());
        var dialogs = new TestDialogs();
        var result = await new TableDeletionService(fixture.Access, dialogs).DeleteAsync(profile, "main", "main", ["a", "b", .. extra], false, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(22, result.Deleted.Count);
        Assert.Equal(1, dialogs.ConfirmCount);
        Assert.Contains("• a", dialogs.LastConfirmation);
        Assert.Contains("• b", dialogs.LastConfirmation);
        Assert.All(extra, n => Assert.Contains("• " + n, dialogs.LastConfirmation));
        Assert.Equal("Da conservare", Assert.Single(await ReadData(profile, "SELECT value FROM keep"))[0]);
        var tables = await fixture.Access.ListTablesAsync(profile, "main", "main", CancellationToken.None);
        Assert.Equal("keep", Assert.Single(tables).Name);
    }

    [Fact]
    public async Task EliminazioneNonToccaTabelleFuoriSelezioneENonEsegueSeRifiutataOSolaLettura()
    {
        var profile = fixture.Profile("Protezioni eliminazione");
        await Execute(profile, "CREATE TABLE parent(id INTEGER PRIMARY KEY)", "CREATE TABLE child(id INTEGER REFERENCES parent(id) ON DELETE CASCADE)", "INSERT INTO parent VALUES(1)", "INSERT INTO child VALUES(1)");
        var dialogs = new TestDialogs { Confirm = false };
        var service = new TableDeletionService(fixture.Access, dialogs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(profile, "main", "main", ["parent"], false, CancellationToken.None));
        Assert.Equal(0, dialogs.ConfirmCount);
        var refused = await service.DeleteAsync(profile, "main", "main", ["parent", "child"], false, CancellationToken.None);
        Assert.False(refused.Confirmed);
        Assert.Single(await ReadData(profile, "SELECT * FROM child"));
        var readOnly = await service.DeleteAsync(profile with { ReadOnly = true }, "main", "main", ["parent", "child"], false, CancellationToken.None);
        Assert.False(readOnly.Confirmed);
        Assert.Equal(1, dialogs.ConfirmCount);
        Assert.Single(await ReadData(profile, "SELECT * FROM parent"));
    }

    [Fact]
    public void ParserCsvRifiutaQualificatoriApertiENormalizzaDateOreEBinari()
    {
        Assert.Throws<FormatException>(() => DataImporter.ParseDelimited(new StringReader("a,b\n1,\"incompleto"), ',', '"').ToArray());
        var options = new ImportValueOptions("DMY", "/", ".", ",", "Ora Data", "Hex");
        Assert.Equal(new DateTime(2025, 12, 31, 14, 25, 30), ImportDataTypes.ConvertValue("14.25.30 31/12/2025", ImportDataType.DateTime, options, true));
        Assert.Equal(new byte[] { 0, 128, 255 }, ImportDataTypes.ConvertValue("0x0080ff", ImportDataType.Binary, options, true));
        Assert.Throws<FormatException>(() => ImportDataTypes.ConvertValue("1,5", ImportDataType.Integer, options, true));
    }

    [Fact]
    public async Task SenzaTransazioneSegnalaIRowsGiaScrittiPrimaDellErrore()
    {
        var profile = fixture.Profile("Importazione senza transazione");
        await Execute(profile, "CREATE TABLE t(value INTEGER)");
        var path = Path.Combine(fixture.Root, "parziale.csv");
        await File.WriteAllTextAsync(path, "value\n1\n2\nnon numerico\n");
        var wizard = new ImportWizardViewModel(fixture.Access, new TestDialogs(), profile, "main", "main", "t") { UseTransaction = false, BatchSize = 2 };
        wizard.SetSourceFile(path);
        await AdvanceImport(wizard);
        await wizard.StartCommand.ExecuteAsync(null);
        Assert.False(wizard.IsFinished);
        Assert.Equal(2, wizard.ImportedRows);
        Assert.Equal(2, (await ReadData(profile, "SELECT value FROM t")).Count);
        Assert.Contains(wizard.Log, line => line.Contains("2 record già inseriti"));
    }

    [Fact]
    public async Task CodificheEPrecisioniNonAlteranoSilenziosamenteIValori()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(fixture.Root, "windows1252.csv");
        await File.WriteAllTextAsync(path, "nome;nome\r\nCaffè;Perché\r\n", Encoding.GetEncoding(1252));
        var source = DataImporter.Open(new ImportReadOptions { Format = ImportFormat.Csv, Path = path, Delimiter = ';', Encoding = Encoding.GetEncoding(1252) });
        Assert.Equal(["nome", "nome_2"], source.Fields);
        Assert.Equal(new object?[] { "Caffè", "Perché" }, Assert.Single(source.Rows));
        Assert.Throws<FormatException>(() => ImportDataTypes.ConvertValue("12,34", ImportDataType.Decimal, new(), true));
        Assert.Equal(ImportDataType.Text, ImportDataTypes.Infer(["12,34"], new()));
        Assert.Throws<FormatException>(() => ImportDataTypes.ValidatePrecision(0.00000000001m, "DECIMAL(38,10)"));
        Assert.Throws<FormatException>(() => ImportDataTypes.ValidatePrecision(123.45m, "DECIMAL(4,2)"));
        Assert.Equal(ImportDataType.Boolean, ImportDataTypes.Infer([true, false], new()));
    }
}
