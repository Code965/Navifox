using Navifox.Core;
using Xunit;

namespace Navifox.Tests;

public sealed class TranslatorTests
{
    private static readonly Translator T = new(new Dictionary<string, string>
    {
        ["Apri tabella"] = "Open table",
        ["Esegui"] = "Run",
        ["{0} righe"] = "{0} rows",
        ["Colonna «{0}» selezionata · {1} righe"] = "Column \"{0}\" selected · {1} rows",
        ["Record {0} di {1} nella pagina {2}"] = "Record {0} of {1} on page {2}",
        ["{0}: {1}"] = "{0}: {1}",
        ["Uguale"] = "Uguale",
        ["Roma"] = "Rome",
    });

    [Fact]
    public void TestiEModelli()
    {
        Assert.Equal("Open table", T.Translate("Apri tabella"));
        Assert.Equal("1.234 rows", T.Translate("1.234 righe"));
        Assert.Equal("Column \"città\" selected · 40 rows", T.Translate("Colonna «città» selezionata · 40 righe"));
        Assert.Equal("Record 2 of 30 on page 1", T.Translate("Record 2 di 30 nella pagina 1"));
        Assert.Equal("  Run  ", T.Translate("  Esegui  "));
        Assert.Null(T.Translate("Clienti"));
        Assert.Null(T.Translate("Uguale")); // traduzione identica: ignorata
        Assert.Null(T.Translate("a: b")); // modello senza testo fisso: ignorato
        Assert.Equal("Roma rows", T.Translate("Roma righe")); // i valori (dati dell'utente) restano come sono
    }
}
