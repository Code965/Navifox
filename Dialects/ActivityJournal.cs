using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Navifox.Core.Models;

namespace Navifox.Dialects;

public sealed record ActivityEntry(long Sequence, Guid SessionId, Guid OperationId, Guid ProfileId,
    string Connection, string? Database, string Kind, string Description, string Sql, string Parameters,
    DateTimeOffset Started, double Milliseconds, string Status, string? Error, long? AffectedRows, DatabaseKind Dialect)
{
    [JsonIgnore] public string TimeLabel => Started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    [JsonIgnore] public string Target => string.IsNullOrEmpty(Database) ? Connection : $"{Connection} · {Database}";
    [JsonIgnore] public string SqlOneLine
    {
        get { var text = Sql.Length == 0 ? Description : Sql; return (text.Length > 400 ? text[..400] + "…" : text).ReplaceLineEndings(" "); }
    }
    [JsonIgnore] public string DurationLabel => $"{Milliseconds:N0} ms";
    [JsonIgnore] public string StatusLabel => Status switch { "Running" => "In corso", "Succeeded" => "Riuscita", "Exported" => "Esportata", "Failed" => "Errore", "Cancelled" => "Interrotta", _ => "Da verificare" };
    [JsonIgnore] public string OperationLabel => OperationId.ToString("N")[..8];
    [JsonIgnore] public string SessionLabel => SessionId == Guid.Empty ? "Precedente" : SessionId.ToString("N")[..8];
    [JsonIgnore] public string Details => $"{Description}\nSessione: {SessionId}\nOperazione: {OperationId}\n{StatusLabel} · {DurationLabel}" +
        (AffectedRows is { } n ? $" · {n:N0} righe interessate" : "") + "\n\n" + Sql +
        (Parameters.Length > 0 ? "\n\nParametri:\n" + Parameters : "") + (Error is null ? "" : "\n\n" + Error);
}

/// <summary>Registro durevole indipendente dalla cronologia: nessuna scadenza o eliminazione automatica.</summary>
public sealed class ActivityJournal
{
    private readonly string _connectionString;
    public Guid SessionId { get; } = Guid.NewGuid();
    public event Action? Changed;

    public ActivityJournal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 15, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, process_id INTEGER NOT NULL, process_start INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS activities(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, operation_id TEXT NOT NULL,
                profile_id TEXT NOT NULL, connection_name TEXT NOT NULL, database_name TEXT, kind TEXT NOT NULL,
                description TEXT NOT NULL, sql TEXT NOT NULL, parameters TEXT NOT NULL, started TEXT NOT NULL,
                milliseconds REAL NOT NULL DEFAULT 0, status TEXT NOT NULL, error TEXT, affected_rows INTEGER, dialect INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_activity_operation ON activities(operation_id, sequence);
            CREATE INDEX IF NOT EXISTS ix_activity_target ON activities(profile_id, database_name, sequence);
            CREATE INDEX IF NOT EXISTS ix_activity_kind ON activities(kind, operation_id);
            CREATE TABLE IF NOT EXISTS imports(name TEXT PRIMARY KEY);
            """;
        command.ExecuteNonQuery();
        RecoverInterrupted(connection);
        using var session = connection.CreateCommand();
        session.CommandText = "INSERT INTO sessions VALUES($id,$pid,$start)";
        session.Parameters.AddWithValue("$id", SessionId.ToString());
        session.Parameters.AddWithValue("$pid", Environment.ProcessId);
        session.Parameters.AddWithValue("$start", Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
        session.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void RecoverInterrupted(SqliteConnection connection)
    {
        var abandoned = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,process_id,process_start FROM sessions";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    using var process = Process.GetProcessById(reader.GetInt32(1));
                    if (process.StartTime.ToUniversalTime().Ticks == reader.GetInt64(2)) continue;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                abandoned.Add(reader.GetString(0));
            }
        }
        foreach (var id in abandoned)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE activities SET status='Uncertain',error='Sessione interrotta: verificare l’esito sul database.' WHERE session_id=$id AND status='Running'; DELETE FROM sessions WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public long Begin(ConnectionProfile profile, string? database, Guid operationId, string kind, string description, string sql = "", string parameters = "")
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO activities(session_id,operation_id,profile_id,connection_name,database_name,kind,description,sql,parameters,started,status,dialect)
                VALUES($session,$operation,$profile,$connection,$database,$kind,$description,$sql,$parameters,$started,'Running',$dialect);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$session", SessionId.ToString());
            command.Parameters.AddWithValue("$operation", operationId.ToString());
            command.Parameters.AddWithValue("$profile", profile.Id.ToString());
            command.Parameters.AddWithValue("$connection", profile.Name);
            command.Parameters.AddWithValue("$database", (object?)database ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$description", description);
            command.Parameters.AddWithValue("$sql", sql);
            command.Parameters.AddWithValue("$parameters", parameters);
            command.Parameters.AddWithValue("$dialect", (int)profile.Kind);
            command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
            var sequence = (long)command.ExecuteScalar()!;
            Notify();
            return sequence;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new IOException("Impossibile scrivere nel registro attività. Il comando non è stato avviato, per evitare una registrazione incompleta.", e);
        }
    }

    public void Finish(long sequence, string status, double milliseconds, string? error = null, long? affectedRows = null)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE activities SET status=$status,milliseconds=$ms,error=$error,affected_rows=$rows WHERE sequence=$sequence";
            command.Parameters.AddWithValue("$sequence", sequence);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$ms", milliseconds);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$rows", (object?)affectedRows ?? DBNull.Value);
            command.ExecuteNonQuery();
            Notify();
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new IOException("Il comando potrebbe essere già stato eseguito, ma non è stato possibile salvarne l’esito. Verifica il database prima di ripeterlo.", e);
        }
    }

    private void Notify() => Changed?.Invoke();

    public IReadOnlyList<ActivityEntry> Read(string search = "", bool onlyProblems = false, Guid? operationId = null, int limit = 200, long? before = null, bool ascending = false, bool onlyTransfers = false) =>
        Enumerate(search, onlyProblems, operationId, limit, before, ascending, onlyTransfers).ToArray();

    public IEnumerable<ActivityEntry> Enumerate(string search = "", bool onlyProblems = false, Guid? operationId = null, int limit = -1, long? before = null, bool ascending = true, bool onlyTransfers = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM activities WHERE ($search='' OR instr(lower(sql || ' ' || description || ' ' || connection_name || ' ' || coalesce(database_name,'') || ' ' || coalesce(error,'') || ' ' || session_id || ' ' || operation_id),lower($search))>0)
            AND ($problems=0 OR status IN ('Failed','Cancelled','Uncertain','Running'))
            AND ($operation IS NULL OR operation_id=$operation) AND ($before IS NULL OR sequence<$before)
            AND ($transfers=0 OR operation_id IN (SELECT operation_id FROM activities WHERE kind IN ('Trasferimento dati','Esportazione dati')))
            ORDER BY sequence
            """ + (ascending ? " ASC" : " DESC") + " LIMIT $limit";
        command.Parameters.AddWithValue("$search", search.Trim());
        command.Parameters.AddWithValue("$problems", onlyProblems ? 1 : 0);
        command.Parameters.AddWithValue("$operation", operationId is { } op ? op.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$transfers", onlyTransfers ? 1 : 0);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), DateTimeOffset.Parse(reader.GetString(10)),
            reader.GetDouble(11), reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetInt64(14), (DatabaseKind)reader.GetInt32(15));
    }

    /// <summary>Esporta tutto il gruppo, in ordine: gli SQL non riusciti/parametrici restano commentati e visibili.</summary>
    public void WriteScript(TextWriter writer, Guid? operationId, Guid profileId, string? database)
    {
        var exportOperations = Enumerate(operationId: operationId).Where(e => e.Kind == "Esportazione dati" && e.Sql.Length == 0).Select(e => e.OperationId).ToHashSet();
        writer.WriteLine("-- Registro delle esecuzioni Navifox. Rivedere lo script prima di eseguirlo.");
        writer.WriteLine("-- Per ricostruire il database serve la stessa base iniziale. Controllare COMMIT/ROLLBACK e gli esiti incerti.");
        foreach (var entry in Enumerate(operationId: operationId).Where(e => e.ProfileId == profileId && e.Database == database))
        {
            writer.WriteLine($"\n-- #{entry.Sequence} {entry.TimeLabel} · {entry.Target.ReplaceLineEndings(" ")} · {entry.StatusLabel}");
            if (entry.Sql.Length == 0) { WriteComment(writer, entry.Description); continue; }
            if (entry.Status is not ("Succeeded" or "Exported") || entry.Parameters.Length > 0 || entry.Kind == "Storico importato" ||
                exportOperations.Contains(entry.OperationId) && (operationId is null || entry.Status != "Exported"))
            {
                WriteComment(writer, entry.Sql);
                if (entry.Parameters.Length > 0) WriteComment(writer, "Parametri da impostare: " + entry.Parameters);
                if (entry.Kind == "Storico importato") WriteComment(writer, "Storico precedente: verificare il dialetto e il contesto prima di eseguire.");
                if (entry.Error is not null) WriteComment(writer, entry.Error);
                continue;
            }
            if (entry.Dialect == DatabaseKind.MySql && entry.Status != "Exported")
            {
                var delimiter = "$navifox_" + entry.Sequence + "$";
                while (entry.Sql.Contains(delimiter, StringComparison.Ordinal)) delimiter += "$";
                writer.WriteLine("DELIMITER " + delimiter);
                writer.WriteLine(entry.Sql.TrimEnd());
                writer.WriteLine(delimiter);
                writer.WriteLine("DELIMITER ;");
            }
            else
            {
                writer.WriteLine(entry.Sql.TrimEnd());
                writer.WriteLine(";");
                if (entry.Dialect == DatabaseKind.SqlServer) writer.WriteLine("GO");
            }
        }
    }

    private static void WriteComment(TextWriter writer, string text)
    {
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n')) writer.WriteLine("-- " + line);
    }

    public void ImportLegacy(string path)
    {
        const string name = "history.jsonl-v1";
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT count(*) FROM imports WHERE name=$name";
        check.Parameters.AddWithValue("$name", name);
        if ((long)check.ExecuteScalar()! > 0) return;
        if (File.Exists(path))
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var item = json.RootElement;
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO activities(session_id,operation_id,profile_id,connection_name,database_name,kind,description,sql,parameters,started,milliseconds,status,error)
                    VALUES($session,$operation,$profile,$connection,$database,'Storico importato','Cronologia precedente: copertura parziale',$sql,'',$started,$ms,$status,$error)
                    """;
                insert.Parameters.AddWithValue("$session", Guid.Empty.ToString());
                insert.Parameters.AddWithValue("$operation", Guid.NewGuid().ToString());
                insert.Parameters.AddWithValue("$profile", Guid.Empty.ToString());
                insert.Parameters.AddWithValue("$connection", item.GetProperty("Connection").GetString() ?? "");
                insert.Parameters.AddWithValue("$database", (object?)item.GetProperty("Database").GetString() ?? DBNull.Value);
                insert.Parameters.AddWithValue("$sql", item.GetProperty("Sql").GetString() ?? "");
                insert.Parameters.AddWithValue("$started", item.GetProperty("Time").GetDateTime().ToUniversalTime().ToString("O"));
                insert.Parameters.AddWithValue("$ms", item.GetProperty("Milliseconds").GetDouble());
                var error = item.GetProperty("Error").GetString();
                insert.Parameters.AddWithValue("$status", error is null ? "Succeeded" : "Failed");
                insert.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // Una riga incompleta non impedisce di importare quelle successive. L'originale rimane intatto.
            }
        }
        using var done = connection.CreateCommand();
        done.Transaction = transaction;
        done.CommandText = "INSERT INTO imports VALUES($name)";
        done.Parameters.AddWithValue("$name", name);
        done.ExecuteNonQuery();
        transaction.Commit();
    }
}
