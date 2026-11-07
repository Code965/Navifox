using Navifox.Core;
using Xunit;

namespace Navifox.Tests;

/// <summary>Importazione dei dati dalla vecchia cartella (MosaicDB) in quella attuale, già esistente.</summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "navifox_test_paths_" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_root, "MosaicDB");
    private string Current => Path.Combine(_root, "Navifox");

    public AppPathsTests()
    {
        Directory.CreateDirectory(Legacy);
        Directory.CreateDirectory(Current);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ImportaLeConnessioniMancantiSenzaToccareQuelleEsistenti()
    {
        File.WriteAllText(Path.Combine(Legacy, "connections.json"),
            """[{"Id":"a","Name":"vecchia A"},{"Id":"b","Name":"vecchia B"}]""");
        File.WriteAllText(Path.Combine(Current, "connections.json"),
            """[{"Id":"b","Name":"B rinominata"},{"Id":"c","Name":"nuova C"}]""");
        File.WriteAllLines(Path.Combine(Legacy, "history.jsonl"), ["{\"q\":1}", "{\"q\":2}"]);
        File.WriteAllLines(Path.Combine(Current, "history.jsonl"), ["{\"q\":2}", "{\"q\":3}"]);
        File.WriteAllText(Path.Combine(Legacy, "automation.json"), "[]");
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), "{\"Theme\":\"Chiaro\"}");
        File.WriteAllText(Path.Combine(Current, "settings.json"), "{\"Theme\":\"Scuro\"}");

        Assert.Equal(1, AppPaths.ImportLegacy(Legacy, Current));

        var connections = File.ReadAllText(Path.Combine(Current, "connections.json"));
        Assert.Contains("vecchia A", connections);
        Assert.Contains("B rinominata", connections);      // la versione attuale vince
        Assert.DoesNotContain("vecchia B", connections);
        Assert.Contains("nuova C", connections);
        Assert.Equal(["{\"q\":1}", "{\"q\":2}", "{\"q\":3}"], File.ReadAllLines(Path.Combine(Current, "history.jsonl")));
        Assert.True(File.Exists(Path.Combine(Current, "automation.json")));
        Assert.Contains("Scuro", File.ReadAllText(Path.Combine(Current, "settings.json"))); // non sovrascritto
        Assert.True(File.Exists(Path.Combine(Legacy, AppPaths.LegacyMarker)));
        Assert.True(File.Exists(Path.Combine(Legacy, "connections.json")));                // la vecchia cartella resta
    }

    [Fact]
    public void NonImportaDueVolte()
    {
        File.WriteAllText(Path.Combine(Legacy, "connections.json"), """[{"Id":"a","Name":"vecchia A"}]""");
        Assert.Equal(1, AppPaths.ImportLegacy(Legacy, Current));
        File.WriteAllText(Path.Combine(Current, "connections.json"), "[]"); // l'utente l'ha eliminata
        Assert.Equal(0, AppPaths.ImportLegacy(Legacy, Current));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(Current, "connections.json")));
    }

    [Fact]
    public void SenzaVecchiaCartellaNonFaNulla()
    {
        Directory.Delete(Legacy);
        Assert.Equal(0, AppPaths.ImportLegacy(Legacy, Current));
    }
}
