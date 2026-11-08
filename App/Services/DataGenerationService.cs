using System.Data.Common;
using System.Globalization;
using Navifox.Core;
using Navifox.Core.DataGen;
using Navifox.Core.Models;
using Navifox.Dialects;

namespace Navifox.App.Services;

/// <summary>Preparazione e inserimento transazionale dei dati di prova. Non elimina né modifica righe esistenti.</summary>
public sealed class DataGenerationService(IDatabaseAccessService access)
{
    public async Task<IReadOnlyList<GeneratorColumn>> PrepareAsync(ConnectionProfile profile, string database, string schema, string table, CancellationToken token)
    {
        await using var connection = await access.OpenAsync(profile, database, token);
        var provider = access.Provider(profile);
        var design = await provider.GetTableDesignAsync(connection, database, schema, table, token);
        var metadata = await provider.GetColumnsAsync(connection, database, schema, table, token);
        var primaryCount = design.Columns.Count(c => c.KeyOrder > 0);
        var result = design.Columns.Select(c =>
        {
            var info = metadata.FirstOrDefault(m => m.Name == c.Name);
            var column = c with { AutoIncrement = c.AutoIncrement || info?.IsAutoIncrement == true, IsVirtual = c.IsVirtual || info?.IsGenerated == true };
            var fk = design.ForeignKeys.Any(k => k.Columns.Contains(c.Name, StringComparer.Ordinal));
            return new GeneratorColumn(column, ColumnGuesser.Guess(column, fk))
            {
                IsUnique = primaryCount == 1 && c.KeyOrder > 0 || design.Indexes.Any(i => i.Kind == "UNIQUE" && i.Columns.Count == 1 && i.Columns[0] == c.Name)
            };
        }).ToArray();
        await RefreshValuesAsync(provider, connection, schema, table, design, result, token);
        return result;
    }

    public async Task RefreshAsync(ConnectionProfile profile, string database, string schema, string table, IReadOnlyList<GeneratorColumn> columns, CancellationToken token)
    {
        await using var connection = await access.OpenAsync(profile, database, token);
        var provider = access.Provider(profile);
        var design = await provider.GetTableDesignAsync(connection, database, schema, table, token);
        await RefreshValuesAsync(provider, connection, schema, table, design, columns, token);
    }

    private static async Task RefreshValuesAsync(IDialectProvider provider, DbConnection connection, string schema, string table, TableDesign design, IReadOnlyList<GeneratorColumn> columns, CancellationToken token)
    {
        foreach (var column in columns.Where(c => c.Kind == FakeKind.Sequence))
        {
            var sql = $"SELECT MAX({provider.QuoteIdentifier(column.Column.Name)}) FROM {provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(table)}";
            var value = (await provider.ExecuteAsync(connection, sql, 1, token)).Rows.FirstOrDefault()?.FirstOrDefault();
            column.SequenceStart = value is null or DBNull ? 1 : checked(Convert.ToInt64(value, CultureInfo.InvariantCulture) + 1);
        }
        foreach (var fk in design.ForeignKeys)
        {
            var members = columns.Where(c => c.Kind == FakeKind.ForeignKey && fk.Columns.Contains(c.Column.Name, StringComparer.Ordinal)).ToArray();
            if (members.Length == 0) continue;
            var parentSchema = string.IsNullOrEmpty(fk.ReferencedSchema) ? schema : fk.ReferencedSchema;
            var target = $"{provider.QuoteIdentifier(parentSchema)}.{provider.QuoteIdentifier(fk.ReferencedTable)}";
            var names = string.Join(", ", fk.ReferencedColumns.Select(provider.QuoteIdentifier));
            var where = string.Join(" AND ", fk.ReferencedColumns.Select(c => provider.QuoteIdentifier(c) + " IS NOT NULL"));
            // La query è limitata lato server: non si scansiona una tabella intera per scegliere valori di esempio.
            var sql = provider.Kind.LimitSelect(names, $"FROM {target} WHERE {where}", 1000);
            var rows = (await provider.ExecuteAsync(connection, sql, 1000, token)).Rows;
            foreach (var member in members)
            {
                member.ForeignGroup = fk.Name;
                member.ForeignIndex = fk.Columns.ToList().IndexOf(member.Column.Name);
                member.ForeignRows = rows;
                member.ForeignValues = rows.Select(r => r[member.ForeignIndex]).ToArray();
            }
        }
    }

    public static void Validate(IReadOnlyList<GeneratorColumn> columns, int count)
    {
        if (count is < 1 or > 1_000_000) throw new InvalidOperationException("Scegli da 1 a 1.000.000 righe.");
        foreach (var c in columns)
        {
            if ((c.Column.AutoIncrement || c.Column.IsVirtual || c.Column.Expression is not null) && c.Kind != FakeKind.Skip)
                throw new InvalidOperationException($"«{c.Column.Name}» è automatica o calcolata: deve restare su «Non inserire».");
            if (c.Kind == FakeKind.Null && c.Column.NotNull) throw new InvalidOperationException($"«{c.Column.Name}» non accetta NULL.");
            if (c.Kind == FakeKind.Skip && c.Column.NotNull && c.Column.Default is null && !c.Column.AutoIncrement && !c.Column.IsVirtual && c.Column.Expression is null)
                throw new InvalidOperationException($"«{c.Column.Name}» è obbligatoria e non ha un valore predefinito.");
            if (c.Kind == FakeKind.ForeignKey && c.ForeignValues.Count == 0)
                throw new InvalidOperationException($"«{c.Column.Name}»: nessun valore disponibile nella tabella collegata. Popola prima la tabella padre o scegli un altro generatore.");
            if (c.Kind == FakeKind.Enum && DataGenerator.EnumValues(c.Column.Type).Length == 0)
                throw new InvalidOperationException($"«{c.Column.Name}»: nessun valore ENUM disponibile.");
        }
    }

    public async Task<int> InsertAsync(ConnectionProfile profile, string database, string schema, string table, IReadOnlyList<GeneratorColumn> columns,
        int count, int seed, bool randomNulls, IProgress<int>? progress, CancellationToken token)
    {
        if (profile.ReadOnly) throw new InvalidOperationException("Connessione in sola lettura: generazione disattivata.");
        Validate(columns, count);
        await using var connection = await access.OpenAsync(profile, database, token);
        var provider = access.Provider(profile);
        using var activity = ActivityRecording.BeginOperation(profile, database, "Generazione dati", $"{schema}.{table}: {count:N0} righe di prova; seme {seed}");
        var transaction = false;
        try
        {
            if (profile.Kind == DatabaseKind.MySql)
            {
                var engine = (await provider.GetTableDesignAsync(connection, database, schema, table, token)).Options.Engine;
                if (!string.IsNullOrWhiteSpace(engine) && !engine.Equals("InnoDB", StringComparison.OrdinalIgnoreCase) && !engine.StartsWith("NDB", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Il generatore richiede una tabella transazionale (ad esempio InnoDB), per annullare gli inserimenti in caso di errore.");
            }
            await provider.ExecuteAsync(connection, provider.Kind.BeginTransactionSql(), 0, token);
            transaction = true;
            var generator = new DataGenerator(columns, seed) { RandomNulls = randomNulls };
            var names = generator.InsertedColumns.Select(c => c.Column.Name).ToArray();
            var batch = new List<IReadOnlyList<object?>>(200);
            var done = 0;
            async Task Flush()
            {
                if (names.Length > 0) await provider.ExecuteAsync(connection, provider.BuildInsert(schema, table, names, batch), 0, token);
                else
                    foreach (var _ in batch)
                    {
                        var target = $"{provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(table)}";
                        await provider.ExecuteAsync(connection, provider.UsesMySqlScriptSyntax ? $"INSERT INTO {target} () VALUES ()" : $"INSERT INTO {target} DEFAULT VALUES", 0, token);
                    }
                done += batch.Count;
                batch.Clear();
                progress?.Report(done);
            }
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                batch.Add(generator.Next());
                if (batch.Count == 200) await Flush();
            }
            if (batch.Count > 0) await Flush();
            await provider.ExecuteAsync(connection, "COMMIT", 0, token);
            transaction = false;
            activity?.Complete();
            return done;
        }
        catch (Exception error)
        {
            if (transaction)
                try { await provider.ExecuteAsync(connection, "ROLLBACK", 0, CancellationToken.None); } catch { }
            activity?.Fail(error);
            throw;
        }
    }
}
