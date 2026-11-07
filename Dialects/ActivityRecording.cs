using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Navifox.Core.Models;

namespace Navifox.Dialects;

public static class ActivityRecording
{
    private sealed record ConnectionContext(ConnectionProfile Profile, ActivityJournal Journal)
    {
        public Guid OperationId { get; } = Guid.NewGuid();
    }
    private static readonly ConditionalWeakTable<DbConnection, ConnectionContext> Connections = new();
    private static readonly AsyncLocal<ActivityOperation?> Current = new();
    public static ActivityJournal? Journal { get; set; }

    public static void Register(DbConnection connection, ConnectionProfile profile, ActivityJournal? journal = null)
    {
        if ((journal ?? Journal) is not { } destination) return;
        Connections.Remove(connection);
        Connections.Add(connection, new(profile, destination));
    }

    internal static DbCommand Wrap(DbCommand command) => command.Connection is { } connection && Connections.TryGetValue(connection, out var context)
        ? new RecordingDbCommand(command, context.Journal, context.Profile, context.OperationId, () => Current.Value)
        : command;

    public static ActivityOperation? BeginOperation(ConnectionProfile profile, string? database, string kind, string description, ActivityJournal? journal = null)
    {
        if ((journal ?? Journal) is not { } destination) return null;
        var operation = new ActivityOperation(destination, profile, database, kind, description, Current.Value);
        Current.Value = operation;
        return operation;
    }

    public static async Task<T> RunOperationAsync<T>(ConnectionProfile profile, string? database, string kind, string description, Func<Task<T>> run)
    {
        if (Current.Value is not null) return await run();
        using var operation = BeginOperation(profile, database, kind, description);
        try { var result = await run(); operation?.Complete(); return result; }
        catch (Exception error) { operation?.Fail(error); throw; }
    }

    public static void Note(string description, string status = "Succeeded")
    {
        if (Current.Value is not { } operation) return;
        var sequence = operation.Journal.Begin(operation.Profile, operation.Database, operation.Id, "Passaggio", description);
        operation.Journal.Finish(sequence, status, 0);
    }

    public static async Task WriteScriptAsync(TextWriter writer, string sql)
    {
        var operation = Current.Value;
        var sequence = operation?.Journal.Begin(operation.Profile, operation.Database, operation.Id, "File SQL", operation.Description, sql);
        try
        {
            await writer.WriteAsync(sql);
            if (sequence is { } id) operation!.Journal.Finish(id, "Exported", 0);
        }
        catch (Exception error)
        {
            if (sequence is { } id) operation!.Journal.Finish(id, "Failed", 0, error.Message);
            throw;
        }
    }

    public sealed class ActivityOperation : IDisposable
    {
        internal ActivityJournal Journal { get; }
        internal ConnectionProfile Profile { get; }
        internal string? Database { get; }
        internal string Kind { get; }
        internal string Description { get; }
        public Guid Id { get; } = Guid.NewGuid();
        private readonly ActivityOperation? _previous;
        private readonly long _sequence;
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private bool _finished;

        internal ActivityOperation(ActivityJournal journal, ConnectionProfile profile, string? database, string kind, string description, ActivityOperation? previous)
        {
            Journal = journal; Profile = profile; Database = database; Kind = kind; Description = description; _previous = previous;
            _sequence = journal.Begin(profile, database, Id, kind, description);
        }

        public void Complete(string? error = null) => Finish(error is null ? "Succeeded" : "Failed", error);
        public void Fail(Exception error) => Finish(error is OperationCanceledException ? "Cancelled" : "Failed", error.Message);
        private void Finish(string status, string? error)
        {
            if (_finished) return;
            _finished = true;
            Journal.Finish(_sequence, status, _timer.Elapsed.TotalMilliseconds, error);
        }
        public void Dispose()
        {
            Current.Value = _previous;
            if (!_finished) Finish("Uncertain", "Operazione non completata: verificare i singoli passaggi e le transazioni.");
        }
    }
}
