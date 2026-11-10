using System.Text.Json;
using Navifox.Core.Models;
using Navifox.Dialects;
using Xunit;

namespace Navifox.Tests;

public sealed class ReadOnlyProfileTests
{
    [Fact]
    public void SolaLetturaSiConservaNelSalvataggio()
    {
        var profile = new ConnectionProfile { Name = "prod", Kind = DatabaseKind.MySql, ReadOnly = true };
        var record = StoredConnectionRecord.FromProfile(profile, "", "", "", "");
        var json = JsonSerializer.Serialize(record);
        var back = JsonSerializer.Deserialize<StoredConnectionRecord>(json)!.ToProfile("", "", "", "");
        Assert.True(back.ReadOnly);
    }

    [Fact]
    public void ConnessioniSalvatePrimaRestanoModificabili()
    {
        var record = StoredConnectionRecord.FromProfile(new ConnectionProfile { Name = "vecchia" }, "", "", "", "");
        var json = JsonSerializer.Serialize(record).Replace(",\"ReadOnly\":false", string.Empty);
        Assert.DoesNotContain("ReadOnly", json);
        Assert.False(JsonSerializer.Deserialize<StoredConnectionRecord>(json)!.ToProfile("", "", "", "").ReadOnly);
    }
}

public sealed class SameDatabaseTests
{
    [Fact]
    public void DueConnessioniVersoLoStessoServer()
    {
        var a = new ConnectionProfile { Name = "Localhost", Kind = DatabaseKind.MySql, Host = "localhost", Port = 3306 };
        var b = new ConnectionProfile { Name = "locale", Kind = DatabaseKind.MySql, Host = "127.0.0.1" };
        Assert.True(a.IsSameDatabase("biblionet", "biblionet", b, "BIBLIONET", "biblionet"));
        Assert.False(a.IsSameDatabase("biblionet", "biblionet", b, "sitoweb", "sitoweb"));
        Assert.False(a.IsSameDatabase("x", null, b with { Port = 3307 }, "x", null));
        var pg = new ConnectionProfile { Kind = DatabaseKind.PostgreSql, Host = "db.example.com" };
        Assert.False(pg.IsSameDatabase("app", "public", pg with { Id = Guid.NewGuid() }, "App", "public")); // in PostgreSQL i nomi distinguono le maiuscole
        Assert.True(pg.IsSameDatabase("app", "public", pg with { Id = Guid.NewGuid() }, "app", "public"));
        Assert.False(pg.IsSameDatabase("app", "public", pg with { Id = Guid.NewGuid(), Kind = DatabaseKind.MySql }, "app", "public"));
    }
}
