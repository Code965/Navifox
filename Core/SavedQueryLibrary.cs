using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Navifox.Core;

public sealed class SavedQuery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public string Database { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;
    [JsonIgnore] public string? FilePath { get; set; }
}

/// <summary>File SQL per connessione/database e indice compatibile con le query delle versioni precedenti.</summary>
public sealed class SavedQueryLibrary(string dataDirectory)
{
    private readonly object _gate = new();
    private readonly string _index = Path.Combine(dataDirectory, "saved-queries.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string QueryDirectory(Guid profileId, string database)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(database)))[..12];
        return Path.Combine(dataDirectory, "queries", profileId.ToString("N"), SafeName(database) + "_" + suffix);
    }

    public IReadOnlyList<SavedQuery> All()
    {
        lock (_gate) { using var lease = Acquire(); return Load(); }
    }

    public SavedQuery Save(SavedQuery query)
    {
        if (query.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(query.Database)) throw new ArgumentException("Scegli connessione e database prima di salvare la query.");
        if (string.IsNullOrWhiteSpace(query.Name)) throw new ArgumentException("Inserisci il nome della query.");
        lock (_gate)
        {
            using var lease = Acquire();
            var queries = Load();
            var existing = queries.FirstOrDefault(q => q.Id == query.Id);
            if (existing is not null && (existing.ProfileId != query.ProfileId || existing.Database != query.Database))
                throw new InvalidOperationException("Per salvare in un altro database usa una nuova query.");
            var saved = new SavedQuery { Id = query.Id, ProfileId = query.ProfileId, Database = query.Database, Name = query.Name.Trim(), Sql = query.Sql, Modified = DateTimeOffset.Now };
            saved.FilePath = FileFor(saved);
            if (queries.Any(q => q.Id != saved.Id && (string.Equals(q.FilePath, saved.FilePath, StringComparison.OrdinalIgnoreCase) ||
                q.ProfileId == saved.ProfileId && q.Database == saved.Database && string.Equals(q.Name, saved.Name, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Esiste già una query con questo nome nel database.");
            if (existing is null && File.Exists(saved.FilePath)) throw new IOException("Esiste già un file SQL con questo nome: scegli un nome diverso.");
            var previousSql = existing?.Sql;
            AtomicWrite(saved.FilePath, saved.Sql);
            queries.RemoveAll(q => q.Id == saved.Id);
            queries.Add(saved);
            try { AtomicWrite(_index, JsonSerializer.Serialize(queries, Json)); }
            catch
            {
                // Ripristina il file se l'indice non può essere aggiornato; l'errore viene mostrato all'utente.
                if (existing is not null && existing.FilePath == saved.FilePath) AtomicWrite(saved.FilePath, previousSql!);
                else File.Delete(saved.FilePath);
                throw;
            }
            if (existing?.FilePath is { } previous && !string.Equals(previous, saved.FilePath, StringComparison.OrdinalIgnoreCase)) File.Delete(previous);
            return saved;
        }
    }

    public SavedQuery? Delete(Guid id)
    {
        lock (_gate)
        {
            using var lease = Acquire();
            var queries = Load();
            var query = queries.FirstOrDefault(q => q.Id == id);
            if (query is null) return null;
            queries.Remove(query);
            AtomicWrite(_index, JsonSerializer.Serialize(queries, Json));
            if (query.FilePath is { } path) File.Delete(path);
            return query;
        }
    }

    private List<SavedQuery> Load()
    {
        // Gli errori dell'indice non vengono convertiti in un elenco vuoto: non perdere query al salvataggio successivo.
        var queries = File.Exists(_index) ? JsonSerializer.Deserialize<List<SavedQuery>>(File.ReadAllText(_index)) ?? throw new InvalidDataException("Indice delle query non valido.") : [];
        foreach (var query in queries)
        {
            // I vecchi elementi senza database restano consultabili e vengono migrati quando vengono salvati in un database.
            var path = FileFor(query);
            query.FilePath = path;
            if (!File.Exists(path)) AtomicWrite(path, query.Sql);
            else query.Sql = File.ReadAllText(path);
        }
        return queries;
    }

    private FileStream Acquire()
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "saved-queries.lock");
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 99) { Thread.Sleep(20); }
        }
    }

    private string FileFor(SavedQuery query) => Path.Combine(QueryDirectory(query.ProfileId, query.Database), SafeName(query.Name) + "_" + query.Id.ToString("N")[..8] + ".sql");

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(value.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c)).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "query";
        if (safe.Length > 70) safe = safe[..70];
        // Un prefisso evita anche i nomi di dispositivo riservati di Windows.
        return "q_" + safe;
    }

    private static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
