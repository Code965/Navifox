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
