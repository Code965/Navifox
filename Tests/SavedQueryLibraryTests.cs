using System.Text.Json;
using Navifox.Core;
using Xunit;

namespace Navifox.Tests;

public sealed class SavedQueryLibraryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "navifox_queries_" + Guid.NewGuid().ToString("N"));
    private readonly Guid _profile = Guid.NewGuid();
    private SavedQuery NewQuery(string name = "Autori", string database = "biblionet") =>
        new() { ProfileId = _profile, Database = database, Name = name, Sql = "SELECT * FROM authors;" };

    [Fact]
    public void SalvaFileSqlERiapreLaQueryDopoIlRiavvio()
    {
        var library = new SavedQueryLibrary(_directory);
        var saved = library.Save(NewQuery());
        Assert.Equal(saved.Sql, File.ReadAllText(saved.FilePath!));
        Assert.StartsWith(library.QueryDirectory(_profile, "biblionet"), saved.FilePath);
        var reopened = Assert.Single(new SavedQueryLibrary(_directory).All());
        Assert.Equal(saved.Id, reopened.Id);
        Assert.Equal(saved.Sql, reopened.Sql);
    }

    [Fact]
    public void AggiornaERinominaSenzaDuplicati()
    {
        var library = new SavedQueryLibrary(_directory);
        var saved = library.Save(NewQuery());
        var oldPath = saved.FilePath!;
        saved.Sql = "SELECT name FROM authors;";
        saved.Name = "Nomi autori";
        var renamed = library.Save(saved);
        Assert.False(File.Exists(oldPath));
        Assert.Equal(saved.Sql, File.ReadAllText(renamed.FilePath!));
        Assert.Equal(saved.Id, Assert.Single(library.All()).Id);
    }

    [Fact]
    public void NomiUgualiRestanoSeparatiPerDatabaseEConnessione()
    {
        var library = new SavedQueryLibrary(_directory);
        var first = library.Save(NewQuery());
        var second = library.Save(NewQuery(database: "altro"));
        var third = NewQuery(); third.ProfileId = Guid.NewGuid();
        third = library.Save(third);
        Assert.Equal(3, library.All().Count);
        Assert.Equal(3, new[] { first.FilePath, second.FilePath, third.FilePath }.Distinct().Count());
        Assert.Throws<InvalidOperationException>(() => library.Save(NewQuery(name: "AUTORI")));
    }

    [Fact]
    public void MigraLeQueryPrecedentiSenzaCambiareIdentita()
    {
        Directory.CreateDirectory(_directory);
        var legacy = NewQuery();
        File.WriteAllText(Path.Combine(_directory, "saved-queries.json"), JsonSerializer.Serialize(new[] { legacy }));
        var migrated = Assert.Single(new SavedQueryLibrary(_directory).All());
        Assert.Equal(legacy.Id, migrated.Id);
        Assert.Equal(legacy.Sql, File.ReadAllText(migrated.FilePath!));
        File.WriteAllText(migrated.FilePath!, "SELECT 42;");
        Assert.Equal("SELECT 42;", Assert.Single(new SavedQueryLibrary(_directory).All()).Sql);
    }

    [Fact]
    public void IndiceCorrottoNonVieneSovrascritto()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "saved-queries.json");
        File.WriteAllText(path, "{corrotto");
        Assert.Throws<JsonException>(() => new SavedQueryLibrary(_directory).Save(NewQuery()));
        Assert.Equal("{corrotto", File.ReadAllText(path));
    }

    [Fact]
    public void NomiConPercorsiONomiRiservatiRestanoNellaCartellaDati()
    {
        var library = new SavedQueryLibrary(_directory);
        var first = library.Save(NewQuery("../../CON", "../db"));
        Assert.StartsWith(Path.GetFullPath(_directory) + Path.DirectorySeparatorChar, Path.GetFullPath(first.FilePath!));
        Assert.True(File.Exists(first.FilePath));
        library.Delete(first.Id);
        Assert.False(File.Exists(first.FilePath));
        Assert.Empty(library.All());
    }

    [Fact]
    public async Task DueIstanzeSalvanoSenzaPerdereQuery()
    {
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => new SavedQueryLibrary(_directory).Save(NewQuery("Query " + i)))));
        Assert.Equal(12, new SavedQueryLibrary(_directory).All().Count);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
