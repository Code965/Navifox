using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;

namespace Navifox.App.Services;

public sealed record SqlHistoryEntry(DateTime Time, string Connection, string? Database, string Sql, double Milliseconds, string? Error)
{
    public bool Succeeded => Error is null;
    public string TimeLabel => Time.ToString("yyyy-MM-dd HH:mm:ss");
    public string DurationLabel => Milliseconds <= 0 ? "—" : Milliseconds < 1000 ? $"{Milliseconds:N0} ms" : $"{Milliseconds / 1000:N2} s";
    public string Target => Database is null ? Connection : $"{Connection} · {Database}";
    public string SqlOneLine => Sql.Length > 400 ? Sql[..400].ReplaceLineEndings(" ") + "…" : Sql.ReplaceLineEndings(" ");
}

/// <summary>Registro delle istruzioni eseguite (Strumenti › Registro cronologia). Conservato tra le sessioni.</summary>
public static class SqlHistory
{
    private const int MaxEntries = 3000;
    private static readonly string FilePath = Navifox.Core.AppPaths.File("history.jsonl");
    private static readonly object Gate = new();
    private static bool _loaded;

    public static ObservableCollection<SqlHistoryEntry> Entries { get; } = [];

    public static void Load()
    {
        if (_loaded) return;
        if (Navifox.Dialects.ActivityRecording.Journal is { } journal)
        {
            journal.ImportLegacy(FilePath);
            _loaded = true;
            return;
        }
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var lines = File.ReadLines(FilePath).TakeLast(MaxEntries);
            foreach (var line in lines.Reverse())
                if (JsonSerializer.Deserialize<SqlHistoryEntry>(line) is { } entry) Entries.Add(entry);
        }
        catch
        {
            // cronologia illeggibile: si riparte da zero
        }
    }

    public static void Record(string connection, string? database, string sql, TimeSpan elapsed, string? error)
    {
        if (Navifox.Dialects.ActivityRecording.Journal is not null) return;
        if (string.IsNullOrWhiteSpace(sql)) return;
        var entry = new SqlHistoryEntry(DateTime.Now, connection, database, sql.Trim(), elapsed.TotalMilliseconds, error);
        Dispatcher.UIThread.Post(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > MaxEntries) Entries.RemoveAt(Entries.Count - 1);
        });
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
            catch
            {
                // la cronologia non deve mai bloccare un'operazione
            }
        }
    }

    public static void Clear()
    {
        Entries.Clear();
        lock (Gate)
        {
            try { File.Delete(FilePath); } catch { /* ignorato */ }
        }
    }
}
