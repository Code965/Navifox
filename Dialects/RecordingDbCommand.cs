using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Navifox.Core.Models;

namespace Navifox.Dialects;

/// <summary>Intercetta anche importazioni, griglia e comandi dei provider, mantenendo i driver nativi.</summary>
internal sealed class RecordingDbCommand(DbCommand inner, ActivityJournal journal, ConnectionProfile profile, Guid connectionOperation,
    Func<ActivityRecording.ActivityOperation?> operation) : DbCommand
{
    [AllowNull] public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
    public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
    public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
    public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
    public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
    protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = value; }
    protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
    protected override DbParameterCollection DbParameterCollection => inner.Parameters;
    protected override DbParameter CreateDbParameter() => inner.CreateParameter();
    public override void Cancel() => inner.Cancel();
    public override void Prepare() => inner.Prepare();
    public override Task PrepareAsync(CancellationToken cancellationToken = default) => inner.PrepareAsync(cancellationToken);

    private Execution Begin()
    {
        var scope = operation();
        var parameters = inner.Parameters.Cast<DbParameter>().Select(p => new
        {
            Name = p.ParameterName, Type = p.DbType.ToString(),
            Value = p.Value is byte[] bytes ? Convert.ToBase64String(bytes) : p.Value is DBNull ? null : p.Value
        }).ToArray();
        var sequence = journal.Begin(profile, inner.Connection?.Database ?? profile.DefaultDatabase, scope?.Id ?? connectionOperation,
            scope?.Kind ?? "SQL", scope?.Description ?? "Comando SQL", CommandText,
            parameters.Length == 0 ? "" : JsonSerializer.Serialize(parameters));
        return new Execution(journal, sequence);
    }

    public override int ExecuteNonQuery()
    {
        var execution = Begin();
        try { var result = inner.ExecuteNonQuery(); execution.Complete(result); return result; }
        catch (Exception e) { execution.Fail(e); throw; }
    }
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var execution = Begin();
        try { var result = await inner.ExecuteNonQueryAsync(cancellationToken); execution.Complete(result); return result; }
        catch (Exception e) { execution.Fail(e); throw; }
    }
    public override object? ExecuteScalar()
    {
        var execution = Begin();
        try { var result = inner.ExecuteScalar(); execution.Complete(); return result; }
        catch (Exception e) { execution.Fail(e); throw; }
    }
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var execution = Begin();
        try { var result = await inner.ExecuteScalarAsync(cancellationToken); execution.Complete(); return result; }
        catch (Exception e) { execution.Fail(e); throw; }
    }
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var execution = Begin();
        try { return new RecordingReader(inner.ExecuteReader(behavior), execution); }
        catch (Exception e) { execution.Fail(e); throw; }
    }
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var execution = Begin();
        try { return new RecordingReader(await inner.ExecuteReaderAsync(behavior, cancellationToken), execution); }
        catch (Exception e) { execution.Fail(e); throw; }
    }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() => inner.DisposeAsync();

    private sealed class Execution(ActivityJournal journal, long sequence)
    {
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private bool _finished;
        public void Complete(long? rows = null) => Finish("Succeeded", null, rows);
        public void Fail(Exception e) => Finish(e is OperationCanceledException ? "Cancelled" : "Failed", e.Message, null);
        private void Finish(string status, string? error, long? rows)
        {
            if (_finished) return;
            _finished = true;
            journal.Finish(sequence, status, _timer.Elapsed.TotalMilliseconds, error, rows);
        }
    }

    private sealed class RecordingReader(DbDataReader inner, Execution execution) : DbDataReader
    {
        private bool _closed;
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken token) => inner.GetFieldValueAsync<T>(ordinal, token);
        public override DataTable? GetSchemaTable() => inner.GetSchemaTable();
        public override IEnumerator GetEnumerator() => new DbEnumerator(this, false);
        public override bool Read() { try { return inner.Read(); } catch (Exception e) { execution.Fail(e); throw; } }
        public override bool NextResult() { try { return inner.NextResult(); } catch (Exception e) { execution.Fail(e); throw; } }
        public override async Task<bool> ReadAsync(CancellationToken token)
        {
            try { return await inner.ReadAsync(token); } catch (Exception e) { execution.Fail(e); throw; }
        }
        public override async Task<bool> NextResultAsync(CancellationToken token)
        {
            try { return await inner.NextResultAsync(token); } catch (Exception e) { execution.Fail(e); throw; }
        }
        public override void Close()
        {
            if (_closed) return;
            _closed = true;
            try { var rows = inner.RecordsAffected; inner.Close(); execution.Complete(rows); }
            catch (Exception e) { execution.Fail(e); throw; }
        }
        protected override void Dispose(bool disposing) { if (disposing) { Close(); inner.Dispose(); } }
        public override async ValueTask DisposeAsync()
        {
            if (_closed) return;
            _closed = true;
            try { var rows = inner.RecordsAffected; await inner.DisposeAsync(); execution.Complete(rows); }
            catch (Exception e) { execution.Fail(e); throw; }
        }
    }
}
