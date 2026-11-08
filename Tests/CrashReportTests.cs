using Navifox.Core;
using Xunit;

namespace Navifox.Tests;

public sealed class CrashReportTests
{
    [Fact]
    public void OscuraIDatiPersonali()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = CrashReport.Sanitize($"File {Path.Combine(profile, "Desktop", "x.sql")} non trovato; Server=10.1.2.3;User Id=mario;Password=Segreta1; scrivi a mario.rossi@azienda.it; WHERE nome = 'Mario Rossi'");
        Assert.DoesNotContain(profile, text);
        Assert.DoesNotContain("Segreta1", text);
        Assert.DoesNotContain("10.1.2.3", text);
        Assert.DoesNotContain("mario.rossi@azienda.it", text);
        Assert.DoesNotContain("Mario Rossi", text);
        Assert.Contains("%USERPROFILE%", text);
        Assert.Contains("Password=***", text);
    }

    [Fact]
    public void RapportoEIndirizzo()
    {
        Exception error;
        try { throw new InvalidOperationException("Valore 'segreto' non valido", new FormatException("dentro")); }
        catch (Exception e) { error = e; }
        var report = CrashReport.Build(error, "1.8.1", "prova");
        Assert.StartsWith("Navifox 1.8.1", report);
        Assert.Contains("System.InvalidOperationException", report);
        Assert.Contains("causato da", report);
        Assert.DoesNotContain("segreto", report);
        var url = CrashReport.IssueUrl("System.InvalidOperationException", report);
        Assert.StartsWith("https://github.com/Code965/Navifox/issues/new?labels=crash&title=Errore%3A%20InvalidOperationException", url);
        Assert.True(url.Length < 20000);
    }
}
