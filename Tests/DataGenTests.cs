using Navifox.Core.DataGen;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.Tests;

public sealed class DataGenTests
{
    [Fact]
    public void CodiceFiscaleNoto()
    {
        // Esempio classico: Mario Rossi, nato a Roma il 1/1/1980 → RSSMRA80A01H501U
        Assert.Equal("RSSMRA80A01H501U", ItalianCodes.FiscalCode("Rossi", "Mario", new DateTime(1980, 1, 1), false, "H501"));
        Assert.True(ItalianCodes.IsValidFiscalCode("RSSMRA80A01H501U"));
        // Donna: giorno + 40; nome con 4+ consonanti: 1ª, 3ª e 4ª.
        Assert.StartsWith("BNCFNC85M41", ItalianCodes.FiscalCode("Bianchi", "Francesca", new DateTime(1985, 8, 1), true, "F205"));
    }

    [Fact]
    public void PartitaIvaEIbanValidi()
    {
        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            Assert.True(ItalianCodes.IsValidVatNumber(ItalianCodes.VatNumber(random)));
            var iban = ItalianCodes.Iban(random);
            Assert.Equal(27, iban.Length);
            Assert.StartsWith("IT", iban);
            Assert.True(ItalianCodes.IsValidIban(iban));
            // Il CIN (5° carattere) è il carattere di controllo di ABI+CAB+conto.
            Assert.Equal(ItalianCodes.ControlLetter(iban[5..]), iban[4]);
        }
        Assert.True(ItalianCodes.IsValidIban("IT60X0542811101000000123456")); // IBAN di esempio ufficiale
        Assert.True(ItalianCodes.IsValidVatNumber("12345670017") || !ItalianCodes.IsValidVatNumber("12345670018"));
    }

    [Fact]
    public void RiconosceLeColonne()
    {
        FakeKind Guess(string name, string type, int? length = null, bool fk = false, int key = 0, bool auto = false) =>
            ColumnGuesser.Guess(new DesignColumn(name, name, type, length, KeyOrder: key, AutoIncrement: auto), fk);
        Assert.Equal(FakeKind.Skip, Guess("id", "int", key: 1, auto: true));
        Assert.Equal(FakeKind.Sequence, Guess("id", "int", key: 1));
        Assert.Equal(FakeKind.ForeignKey, Guess("cliente_id", "int", fk: true));
        Assert.Equal(FakeKind.Email, Guess("email", "varchar", 120));
        Assert.Equal(FakeKind.FirstName, Guess("nome", "varchar", 50));
        Assert.Equal(FakeKind.LastName, Guess("cognome", "varchar", 50));
        Assert.Equal(FakeKind.FiscalCode, Guess("codice_fiscale", "char", 16));
        Assert.Equal(FakeKind.VatNumber, Guess("partita_iva", "varchar", 11));
        Assert.Equal(FakeKind.Phone, Guess("telefono", "varchar", 20));
        Assert.Equal(FakeKind.City, Guess("citta", "varchar", 60));
        Assert.Equal(FakeKind.Price, Guess("importo", "decimal", 10));
        Assert.Equal(FakeKind.BirthDate, Guess("data_nascita", "date"));
        Assert.Equal(FakeKind.Enum, Guess("stato", "enum('nuovo','chiuso')"));
        Assert.Equal(FakeKind.Integer, Guess("telefono_count", "int")); // nome da telefono ma tipo numerico
        Assert.Equal(FakeKind.Boolean, Guess("attivo", "boolean"));
        Assert.Equal(FakeKind.DateTime, Guess("created_at", "timestamp"));
    }

    [Fact]
    public void RigheCoerentiERispettanoIVincoli()
    {
        var columns = new[]
        {
            new GeneratorColumn(new DesignColumn("id", "id", "int", KeyOrder: 1, NotNull: true), FakeKind.Sequence) { SequenceStart = 101 },
            new GeneratorColumn(new DesignColumn("nome", "nome", "varchar", 50, NotNull: true), FakeKind.FirstName),
            new GeneratorColumn(new DesignColumn("cognome", "cognome", "varchar", 50, NotNull: true), FakeKind.LastName),
            new GeneratorColumn(new DesignColumn("email", "email", "varchar", 30, NotNull: true), FakeKind.Email) { IsUnique = true },
            new GeneratorColumn(new DesignColumn("cf", "cf", "char", 16, NotNull: true), FakeKind.FiscalCode),
            new GeneratorColumn(new DesignColumn("importo", "importo", "decimal", 5, 2, NotNull: true), FakeKind.Price),
            new GeneratorColumn(new DesignColumn("cliente", "cliente", "int", NotNull: true), FakeKind.ForeignKey) { ForeignValues = [7L, 8L] },
            new GeneratorColumn(new DesignColumn("stato", "stato", "enum('a','b')", NotNull: true), FakeKind.Enum),
            new GeneratorColumn(new DesignColumn("creato", "creato", "timestamp", Default: "CURRENT_TIMESTAMP"), FakeKind.Skip),
        };
        var generator = new DataGenerator(columns, seed: 42);
        Assert.Equal(8, generator.InsertedColumns.Count);
        var rows = generator.Generate(300).ToArray();
        Assert.Equal(Enumerable.Range(101, 300).Select(i => (object)(long)i), rows.Select(r => r[0]));
        Assert.All(rows, r =>
        {
            var cf = (string)r[4]!;
            Assert.True(ItalianCodes.IsValidFiscalCode(cf));
            Assert.Equal(ItalianCodes.FiscalCode((string)r[2]!, (string)r[1]!, DateTime.Today, false, "H501")[..6], cf[..6]); // stesso nome e cognome
            Assert.True(((string)r[3]!).Length <= 30);
            Assert.Contains("@example.", (string)r[3]!);
            Assert.InRange((decimal)r[5]!, 0m, 999.99m);
            Assert.Contains(r[6], new object[] { 7L, 8L });
            Assert.Contains(r[7], new object[] { "a", "b" });
        });
        Assert.Equal(300, rows.Select(r => r[3]).Distinct().Count());
        // Stesso seme → stesse righe.
        Assert.Equal(rows[0], new DataGenerator(columns, seed: 42).Next());
    }

    [Fact]
    public void AnonimizzazioneDeterministica()
    {
        var columns = new[]
        {
            new ColumnInfo("id", "int", false, IsPrimaryKey: true),
            new ColumnInfo("email", "varchar(100)", true),
            new ColumnInfo("cognome", "character varying", true),
            new ColumnInfo("quantita", "int", false),
            new ColumnInfo("note", "text", true),
        };
        var masker = DataMasker.For(columns)!;
        Assert.Equal(["email", "cognome"], masker.MaskedColumns.Select(c => c.Column));
        var a = masker.Mask([1, "mario.rossi@azienda.it", "Rossi", 5, "testo"]);
        var b = masker.Mask([2, "mario.rossi@azienda.it", null, 6, "altro"]);
        Assert.Equal(1, a[0]);
        Assert.NotEqual("mario.rossi@azienda.it", a[1]);
        Assert.Equal(a[1], b[1]); // stesso valore → stesso sostituto
        Assert.Null(b[2]);
        Assert.Equal(5, a[3]);
        Assert.Equal("testo", a[4]);
        Assert.Null(DataMasker.For([new ColumnInfo("id", "int", false), new ColumnInfo("totale", "decimal(10,2)", false)]));
    }

    [Fact]
    public void MascheramentoUsaSalePerCopiaEConservaChiaviNullELunghezzeUnivoche()
    {
        ColumnInfo[] columns = [new("id", "integer", false, IsPrimaryKey: true), new("email", "varchar(20)", false), new("nome", "varchar(5)", true), new("telefono", "varchar(20)", true)];
        var salt = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var a = DataMasker.For(columns, ["telefono"], uniqueColumns: ["email", "nome"], salt: salt)!;
        var b = DataMasker.For(columns, ["telefono"], uniqueColumns: ["email", "nome"], salt: salt)!;
        var other = DataMasker.For(columns, ["telefono"], uniqueColumns: ["email", "nome"], salt: new byte[32])!;
        var rows = Enumerable.Range(0, 300).Select(i => a.Mask([i, $"originale{i}@azienda.it", "Nome" + i, "3331234567"])).ToArray();
        Assert.Equal(300, rows.Select(r => r[1]).Distinct().Count());
        Assert.Equal(300, rows.Select(r => r[2]).Distinct().Count());
        Assert.All(rows, r => { Assert.InRange(((string)r[1]!).Length, 1, 20); Assert.InRange(((string)r[2]!).Length, 1, 5); Assert.Equal("3331234567", r[3]); });
        Assert.Equal(rows[0], b.Mask([0, "originale0@azienda.it", "Nome0", "3331234567"]));
        Assert.NotEqual(rows[0][1], other.Mask([0, "originale0@azienda.it", "Nome0", "3331234567"])[1]);
        Assert.Equal(rows[0][1], a.Mask([99, "originale0@azienda.it", null, null])[1]);
        Assert.Null(a.Mask([99, null, null, null])[1]);
    }

    [Fact]
    public void GeneratoriUnivociMantengonoChecksumEInterromponoDominiEsauriti()
    {
        GeneratorColumn[] columns = [
            new(new("cf", "cf", "char", 16, NotNull: true), FakeKind.FiscalCode) { IsUnique = true },
            new(new("piva", "piva", "char", 11, NotNull: true), FakeKind.VatNumber) { IsUnique = true },
            new(new("iban", "iban", "char", 27, NotNull: true), FakeKind.Iban) { IsUnique = true },
            new(new("codice", "codice", "varchar", 4, NotNull: true), FakeKind.Text) { IsUnique = true }
        ];
        var rows = new DataGenerator(columns, 5).Generate(300).ToArray();
        Assert.All(rows, r => { Assert.True(ItalianCodes.IsValidFiscalCode((string)r[0]!)); Assert.True(ItalianCodes.IsValidVatNumber((string)r[1]!)); Assert.True(ItalianCodes.IsValidIban((string)r[2]!)); Assert.InRange(((string)r[3]!).Length, 1, 4); });
        for (var i = 0; i < columns.Length; i++) Assert.Equal(300, rows.Select(r => r[i]).Distinct().Count());
        var exhausted = new DataGenerator([new(new("stato", "stato", "enum('a','b')", NotNull: true), FakeKind.Enum) { IsUnique = true }], 3);
        Assert.Equal(2, exhausted.Generate(2).Select(r => r[0]).Distinct().Count());
        Assert.Throws<InvalidOperationException>(() => exhausted.Next());
    }

    [Fact]
    public void PrecisioniNumericheEsteseERidotteNonSuperanoILimiti()
    {
        GeneratorColumn[] columns = [
            new(new("grande", "grande", "decimal", 65, 30, NotNull: true), FakeKind.Decimal),
            new(new("piccolo", "piccolo", "decimal", 1, 1, NotNull: true), FakeKind.Price)
        ];
        Assert.All(new DataGenerator(columns, 3).Generate(200), r => { Assert.InRange((decimal)r[0]!, 0, 10000); Assert.InRange((decimal)r[1]!, 0, 0.9m); });
    }
}
