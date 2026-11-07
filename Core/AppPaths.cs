using System.Text.Json.Nodes;

namespace Navifox.Core;

/// <summary>
/// Cartella dei dati di Navifox (%APPDATA%\Navifox): connessioni, impostazioni, cronologia, automazioni.
/// Dati della vecchia cartella %APPDATA%\MosaicDB (versioni precedenti alla rinomina): al primo avvio si importano
/// nella nuova, anche se questa esiste già, senza sovrascrivere nulla. La vecchia cartella resta come copia di sicurezza.
/// La variabile d'ambiente NAVIFOX_DATA_DIR usa una cartella diversa (test, installazioni portatili).
/// </summary>
public static class AppPaths
{
    /// <summary>File scritto nella vecchia cartella dopo l'importazione, così non si ripete.</summary>
    public const string LegacyMarker = "IMPORTATO-IN-NAVIFOX.txt";

    private static readonly Lazy<string> Directory = new(Prepare, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string DataDirectory => Directory.Value;

    public static string File(string name) => Path.Combine(DataDirectory, name);

    private static string Prepare()
    {
        var overrideDir = Environment.GetEnvironmentVariable("NAVIFOX_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            System.IO.Directory.CreateDirectory(overrideDir);
            return overrideDir;
        }
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var current = Path.Combine(root, "Navifox");
        var legacy = Path.Combine(root, "MosaicDB");
        try
        {
            System.IO.Directory.CreateDirectory(current);
            ImportLegacy(legacy, current);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Se la nuova cartella non è utilizzabile si continua con la vecchia, così nessun dato va perso.
            if (System.IO.Directory.Exists(legacy) && !System.IO.Directory.Exists(current)) return legacy;
        }
        return current;
    }

    /// <summary>
    /// Importa i dati di una cartella precedente in quella attuale senza perdere nulla:
    /// connessioni mancanti (per Id), cronologia unita, altri file copiati solo se assenti. Restituisce le connessioni importate.
    /// </summary>
    public static int ImportLegacy(string legacy, string current)
    {
        if (!System.IO.Directory.Exists(legacy) || System.IO.File.Exists(Path.Combine(legacy, LegacyMarker))) return 0;
        var imported = 0;
        try
        {
            imported = MergeConnections(Path.Combine(legacy, "connections.json"), Path.Combine(current, "connections.json"));
            MergeLines(Path.Combine(legacy, "history.jsonl"), Path.Combine(current, "history.jsonl"));
            foreach (var file in System.IO.Directory.GetFiles(legacy))
            {
                var name = Path.GetFileName(file);
                if (name is "connections.json" or "history.jsonl" or LegacyMarker) continue;
                var target = Path.Combine(current, name);
                if (!System.IO.File.Exists(target)) System.IO.File.Copy(file, target);
            }
            System.IO.File.WriteAllText(Path.Combine(legacy, LegacyMarker),
                $"I dati di questa cartella sono stati importati in {current} il {DateTime.Now:yyyy-MM-dd HH:mm} ({imported} connessioni).\n" +
                "La cartella resta come copia di sicurezza: puoi eliminarla quando vuoi.\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Importazione non riuscita: si riproverà al prossimo avvio (nessun file viene sovrascritto).
        }
        return imported;
    }

    /// <summary>Aggiunge al file attuale le connessioni del vecchio file che non ci sono (stesso Id = stessa connessione).</summary>
    private static int MergeConnections(string legacyFile, string currentFile)
    {
        if (!System.IO.File.Exists(legacyFile)) return 0;
        if (JsonNode.Parse(System.IO.File.ReadAllText(legacyFile)) is not JsonArray old || old.Count == 0) return 0;
        var current = System.IO.File.Exists(currentFile) && JsonNode.Parse(System.IO.File.ReadAllText(currentFile)) is JsonArray existing ? existing : [];
        var ids = current.Select(IdOf).Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var item in old.ToArray())
        {
            if (item is null || IdOf(item) is not { } id || ids.Contains(id)) continue;
            old.Remove(item);
            current.Add(item);
            ids.Add(id);
            added++;
        }
        if (added == 0) return 0;
        var temp = currentFile + ".tmp";
        System.IO.File.WriteAllText(temp, current.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        System.IO.File.Move(temp, currentFile, overwrite: true);
        return added;
    }

    private static string? IdOf(JsonNode? node) => (node?["Id"] ?? node?["id"])?.ToString();

    /// <summary>Cronologia: le righe vecchie che mancano vanno prima di quelle attuali (ordine nel tempo).</summary>
    private static void MergeLines(string legacyFile, string currentFile)
    {
        if (!System.IO.File.Exists(legacyFile)) return;
        var old = System.IO.File.ReadAllLines(legacyFile).Where(l => l.Length > 0).ToList();
        var now = System.IO.File.Exists(currentFile) ? System.IO.File.ReadAllLines(currentFile).Where(l => l.Length > 0).ToList() : [];
        var known = now.ToHashSet();
        var missing = old.Where(l => !known.Contains(l)).ToList();
        if (missing.Count == 0) return;
        var temp = currentFile + ".tmp";
        System.IO.File.WriteAllLines(temp, missing.Concat(now));
        System.IO.File.Move(temp, currentFile, overwrite: true);
    }
}
