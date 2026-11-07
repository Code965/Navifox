using Navifox.Core;

namespace Navifox.App.Services;

/// <summary>Scheda riaperta all'avvio o salvata in un'area di lavoro.</summary>
public sealed class SessionTab
{
    public string Kind { get; set; } = "table";
    public Guid ProfileId { get; set; }
    public string? Database { get; set; }
    public string? Schema { get; set; }
    public string? Table { get; set; }
    public string? Sql { get; set; }
    public string? FilePath { get; set; }
    public Guid? SavedQueryId { get; set; }
}

public sealed class Workspace
{
    public string Format { get; set; } = "navifox-workspace";
    public int Version { get; set; } = 1;
    public List<SessionTab> Tabs { get; set; } = [];
    public int Selected { get; set; }
}

/// <summary>Archivio locale (%APPDATA%\Navifox): query salvate e cartella dei backup.</summary>
public static class LibraryStore
{
    private static readonly SavedQueryLibrary Library = new(AppPaths.DataDirectory);
    public static string QueryDirectory(Guid profileId, string database) => Library.QueryDirectory(profileId, database);

    /// <summary>Notifica le modifiche (per aggiornare l'albero).</summary>
    public static event Action<Guid, string>? QueriesChanged;

    public static IReadOnlyList<SavedQuery> QueriesFor(Guid profileId, string database) =>
        Library.All().Where(q => q.ProfileId == profileId && q.Database == database).OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static SavedQuery? Query(Guid id) => Library.All().FirstOrDefault(q => q.Id == id);

    public static SavedQuery SaveQuery(SavedQuery query)
    {
        var saved = Library.Save(query);
        QueriesChanged?.Invoke(query.ProfileId, query.Database);
        return saved;
    }

    public static void DeleteQuery(Guid id)
    {
        if (Library.Delete(id) is not { } query) return;
        QueriesChanged?.Invoke(query.ProfileId, query.Database);
    }

    /// <summary>Cartella dei backup di un database: %APPDATA%\Navifox\backups\«connessione»\«database».</summary>
    public static string BackupDirectory(Guid profileId, string database)
    {
        var safe = string.Concat(database.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(AppPaths.DataDirectory, "backups", profileId.ToString("N"), safe);
    }

    public static IReadOnlyList<FileInfo> Backups(Guid profileId, string database)
    {
        var directory = new DirectoryInfo(BackupDirectory(profileId, database));
        return directory.Exists ? directory.GetFiles("*.sql").OrderByDescending(f => f.LastWriteTimeUtc).ToArray() : [];
    }
}
