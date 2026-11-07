using System.Text;
using System.Text.RegularExpressions;
using Navifox.Core.Models;

namespace Navifox.Core;

/// <summary>«Progetta tabella» per SQL Server e SQLite.</summary>
public static partial class TableDesignSql
{
    public static IReadOnlyList<string> Build(DatabaseKind kind, string schema, TableDesign? original, TableDesign target) => kind switch
    {
        DatabaseKind.MySql => new MySqlWriter(target).Build(original),
        DatabaseKind.SqlServer => new SqlServerWriter(schema, target).Build(original),
        DatabaseKind.Sqlite => new SqliteWriter(target).Build(original),
        _ => new PostgresWriter(schema, target).Build(original),
    };

    private static string ReferentialAction(string action) => action.Replace('_', ' ').ToUpperInvariant() switch
    {
        "RESTRICT" => "NO ACTION",
        var other => other,
    };

    // ───────────────────────── SQL Server ─────────────────────────

    private sealed class SqlServerWriter(string schema, TableDesign target) : Writer(target)
    {
        protected override string Q(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
        protected override string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

        private string Table => $"{Q(schema)}.{Q(Target.Name)}";
        private string ObjectLiteral => Literal($"{Q(schema)}.{Q(Target.Name)}");

        private static string DefaultName(string table, string column) => $"DF_{table}_{column}";

        private string ColumnDefinition(DesignColumn c, bool withDefault = true)
        {
            if (c.IsVirtual && !string.IsNullOrWhiteSpace(c.Expression))
                return $"{Q(c.Name)} AS ({c.Expression}){(c.StoredGenerated ? " PERSISTED" : string.Empty)}";
            var sql = new StringBuilder($"{Q(c.Name)} {TypeWithLength(c)}");
            if (c.AutoIncrement) sql.Append(" IDENTITY(1,1)");
            sql.Append(c.NotNull || c.KeyOrder > 0 ? " NOT NULL" : " NULL");
            if (withDefault && !c.AutoIncrement && !string.IsNullOrWhiteSpace(c.Default))
                sql.Append($" CONSTRAINT {Q(DefaultName(Target.Name, c.Name))} DEFAULT ({c.Default.Trim()})");
            return sql.ToString();
        }

        /// <summary>Elimina il vincolo DEFAULT di una colonna (il nome è generato dal server se non scelto da noi).</summary>
        private string DropDefault(string column) =>
            $"DECLARE @df sysname = (SELECT d.name FROM sys.default_constraints d JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id " +
            $"WHERE d.parent_object_id = OBJECT_ID({ObjectLiteral}) AND c.name = {Literal(column)}) " +
            $"IF @df IS NOT NULL EXEC(N'ALTER TABLE {Table.Replace("'", "''")} DROP CONSTRAINT [' + @df + N']')";

        private string Description(string? before, string? now, string? column)
        {
            var level2 = column is null ? string.Empty : $", @level2type = N'COLUMN', @level2name = {Literal(column)}";
            var target = $"@level0type = N'SCHEMA', @level0name = {Literal(schema)}, @level1type = N'TABLE', @level1name = {Literal(Target.Name)}{level2}";
            if (string.IsNullOrEmpty(now)) return $"EXEC sys.sp_dropextendedproperty @name = N'MS_Description', {target}";
            return $"EXEC sys.{(string.IsNullOrEmpty(before) ? "sp_addextendedproperty" : "sp_updateextendedproperty")} @name = N'MS_Description', @value = {Literal(now)}, {target}";
        }

        private string CreateIndex(DesignIndex index)
        {
            var clustered = index.Method?.ToUpperInvariant() is "CLUSTERED" ? "CLUSTERED " : string.Empty;
            return $"CREATE {(index.Kind == "UNIQUE" ? "UNIQUE " : string.Empty)}{clustered}INDEX {Q(index.Name)} ON {Table} ({IndexColumns(index.Columns)})";
        }

        private string AddKey(DesignForeignKey key)
        {
            var table = $"{Q(string.IsNullOrEmpty(key.ReferencedSchema) ? schema : key.ReferencedSchema)}.{Q(key.ReferencedTable)}";
            return $"ALTER TABLE {Table} ADD CONSTRAINT {Q(key.Name)} FOREIGN KEY ({Columns(key.Columns)}) REFERENCES {table} ({Columns(key.ReferencedColumns)}) ON DELETE {ReferentialAction(key.OnDelete)} ON UPDATE {ReferentialAction(key.OnUpdate)}";
        }

        private string Trigger(DesignTrigger trigger)
        {
            var timing = trigger.Timing.Equals("BEFORE", StringComparison.OrdinalIgnoreCase) ? "INSTEAD OF" : trigger.Timing.ToUpperInvariant();
            var events = string.Join(", ", trigger.Events.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Where(e => !e.Equals("OR", StringComparison.OrdinalIgnoreCase)));
            var body = trigger.Body.Trim();
            if (body.StartsWith("AS", StringComparison.OrdinalIgnoreCase) && body.Length > 2 && char.IsWhiteSpace(body[2])) body = body[2..].TrimStart();
            return $"CREATE TRIGGER {Q(schema)}.{Q(trigger.Name)} ON {Table} {timing} {events} AS\n{body}";
        }

        private string Rename(string what, string current, string name) =>
            $"EXEC sp_rename {Literal(current)}, {Literal(name)}{(what.Length == 0 ? string.Empty : $", N'{what}'")}";

        public IReadOnlyList<string> Build(TableDesign? original)
        {
            var statements = new List<string>();
            var pkName = Target.PrimaryKeyName ?? $"PK_{Target.Name}";
            if (original is null)
            {
                var lines = Target.Columns.Select(c => ColumnDefinition(c)).ToList();
                var pk = PrimaryKey(Target);
                if (pk.Count > 0) lines.Add($"CONSTRAINT {Q(pkName)} PRIMARY KEY ({Columns(pk.Select(c => c.Name))})");
                statements.Add($"CREATE TABLE {Table} (\n  {string.Join(",\n  ", lines)}\n)");
                statements.AddRange(Target.Indexes.Select(CreateIndex));
                statements.AddRange(Target.ForeignKeys.Select(AddKey));
                statements.AddRange(Target.Triggers.Select(Trigger));
                if (!string.IsNullOrEmpty(Target.Comment)) statements.Add(Description(null, Target.Comment, null));
                foreach (var column in Target.Columns.Where(c => !string.IsNullOrEmpty(c.Comment))) statements.Add(Description(null, column.Comment, column.Name));
                return statements;
            }

            // 1. Rimozioni: trigger, chiavi esterne, indici, chiave primaria, colonne.
            foreach (var trigger in original.Triggers.Where(o => Target.Triggers.FirstOrDefault(t => t.OriginalName == o.OriginalName) is not { } t || t with { OriginalName = null } != o with { OriginalName = null }))
                statements.Add($"DROP TRIGGER IF EXISTS {Q(schema)}.{Q(trigger.OriginalName!)}");
            var changedKeys = original.ForeignKeys.Where(o => Target.ForeignKeys.FirstOrDefault(t => t.OriginalName == o.OriginalName) is not { } t || !KeyUnchanged(o, t)).ToList();
            statements.AddRange(changedKeys.Select(k => $"ALTER TABLE {Table} DROP CONSTRAINT {Q(k.OriginalName!)}"));
            var recreatedIndexes = new List<DesignIndex>();
            foreach (var index in original.Indexes)
            {
                var now = Target.Indexes.FirstOrDefault(t => t.OriginalName == index.OriginalName);
                if (now is not null && IndexUnchanged(index, now))
                {
                    if (now.Name != index.Name) statements.Add(Rename("INDEX", $"{schema}.{original.Name}.{index.OriginalName}", now.Name));
                    continue;
                }
                statements.Add(index.IsConstraint ? $"ALTER TABLE {Table} DROP CONSTRAINT {Q(index.OriginalName!)}" : $"DROP INDEX {Q(index.OriginalName!)} ON {Table}");
                if (now is not null) recreatedIndexes.Add(now);
            }

            var oldKey = PrimaryKey(original).Select(c => Target.Columns.FirstOrDefault(t => t.OriginalName == c.OriginalName)?.Name ?? $"\0{c.Name}").ToArray();
            var newKey = PrimaryKey(Target).Select(c => c.Name).ToArray();
            var keyChanged = !oldKey.SequenceEqual(newKey);
            if (keyChanged && oldKey.Length > 0) statements.Add($"ALTER TABLE {Table} DROP CONSTRAINT {Q(original.PrimaryKeyName ?? $"PK_{original.Name}")}");

            foreach (var column in Removed(original.Columns, Target.Columns, c => c.OriginalName))
            {
                statements.Add(DropDefault(column.OriginalName!));
                statements.Add($"ALTER TABLE {Table} DROP COLUMN {Q(column.OriginalName!)}");
            }

            // 2. Colonne modificate e nuove.
            foreach (var column in Target.Columns)
            {
                if (column.OriginalName is null || original.Columns.FirstOrDefault(o => o.OriginalName == column.OriginalName) is not { } before)
                {
                    statements.Add($"ALTER TABLE {Table} ADD {ColumnDefinition(column)}");
                    if (!string.IsNullOrEmpty(column.Comment)) statements.Add(Description(null, column.Comment, column.Name));
                    continue;
                }
                if (SameColumn(before, column)) continue;
                if (before.AutoIncrement != column.AutoIncrement)
                    throw new InvalidOperationException($"SQL Server non permette di aggiungere o togliere IDENTITY alla colonna esistente «{before.Name}»: crea una nuova colonna e copia i valori.");
                if (before.Name != column.Name) statements.Add(Rename("COLUMN", $"{schema}.{original.Name}.{before.Name}", column.Name));
                if (before.IsVirtual || column.IsVirtual)
                {
                    // Le colonne calcolate non si modificano: si eliminano e si ricreano.
                    if (before.IsVirtual != column.IsVirtual || before.Expression != column.Expression || before.StoredGenerated != column.StoredGenerated)
                    {
                        statements.Add($"ALTER TABLE {Table} DROP COLUMN {Q(column.Name)}");
                        statements.Add($"ALTER TABLE {Table} ADD {ColumnDefinition(column)}");
                    }
                }
                else
                {
                    var typeChanged = !TypeWithLength(before).Equals(TypeWithLength(column), StringComparison.OrdinalIgnoreCase) || before.NotNull != column.NotNull;
                    if (typeChanged) statements.Add($"ALTER TABLE {Table} ALTER COLUMN {ColumnDefinition(column, withDefault: false)}");
                    if ((before.Default ?? string.Empty) != (column.Default ?? string.Empty))
                    {
                        if (!string.IsNullOrWhiteSpace(before.Default)) statements.Add(DropDefault(column.Name));
                        if (!string.IsNullOrWhiteSpace(column.Default))
                            statements.Add($"ALTER TABLE {Table} ADD CONSTRAINT {Q(DefaultName(Target.Name, column.Name))} DEFAULT ({column.Default.Trim()}) FOR {Q(column.Name)}");
                    }
                }
                if ((before.Comment ?? string.Empty) != (column.Comment ?? string.Empty)) statements.Add(Description(before.Comment, column.Comment, column.Name));
            }

            // 3. Aggiunte: chiave primaria, indici, chiavi esterne, trigger, commento.
            if (keyChanged && newKey.Length > 0) statements.Add($"ALTER TABLE {Table} ADD CONSTRAINT {Q(pkName)} PRIMARY KEY ({Columns(newKey)})");
            statements.AddRange(Target.Indexes.Where(i => i.OriginalName is null).Concat(recreatedIndexes).Select(CreateIndex));
            statements.AddRange(Target.ForeignKeys.Where(t => t.OriginalName is null || changedKeys.Any(c => c.OriginalName == t.OriginalName)).Select(AddKey));
            foreach (var trigger in Target.Triggers)
                if (trigger.OriginalName is null || original.Triggers.FirstOrDefault(o => o.OriginalName == trigger.OriginalName) is not { } o || o with { OriginalName = null } != trigger with { OriginalName = null })
                    statements.Add(Trigger(trigger));
            if ((Target.Comment ?? string.Empty) != (original.Comment ?? string.Empty)) statements.Add(Description(original.Comment, Target.Comment, null));
            return statements;
        }
    }

    // ───────────────────────── SQLite ─────────────────────────

    /// <summary>
    /// SQLite supporta solo poche ALTER TABLE (aggiungi, rinomina, elimina colonna): per le altre modifiche
    /// la tabella viene ricostruita (nuova tabella, copia dei dati, sostituzione), come raccomandato dalla documentazione.
    /// </summary>
    private sealed class SqliteWriter(TableDesign target) : Writer(target)
    {
        protected override string Q(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        protected override string Literal(string value) => "'" + value.Replace("'", "''") + "'";

        private string Table => Q(Target.Name);

        /// <summary>Chiave primaria INTEGER singola con autoincremento: va dichiarata sulla colonna.</summary>
        private bool InlineKey(DesignColumn c) =>
            c.KeyOrder > 0 && PrimaryKey(Target).Count == 1 && (c.AutoIncrement || Regex.IsMatch(c.Type, "^int(eger)?$", RegexOptions.IgnoreCase));

        private string ColumnDefinition(DesignColumn c)
        {
            var type = c.AutoIncrement && InlineKey(c) ? "INTEGER" : TypeWithLength(c);
            var sql = new StringBuilder($"{Q(c.Name)} {type}");
            if (InlineKey(c)) sql.Append(c.AutoIncrement ? " PRIMARY KEY AUTOINCREMENT" : " PRIMARY KEY");
            if (c.NotNull) sql.Append(" NOT NULL");
            if (c.IsVirtual && !string.IsNullOrWhiteSpace(c.Expression)) sql.Append($" GENERATED ALWAYS AS ({c.Expression}) {(c.StoredGenerated ? "STORED" : "VIRTUAL")}");
            else if (!string.IsNullOrWhiteSpace(c.Default)) sql.Append($" DEFAULT {WrapDefault(c.Default.Trim())}");
            if (!string.IsNullOrWhiteSpace(c.Collation)) sql.Append($" COLLATE {c.Collation}");
            return sql.ToString();
        }

        /// <summary>Le espressioni (non letterali) come DEFAULT vanno tra parentesi.</summary>
        private static string WrapDefault(string value) =>
            Regex.IsMatch(value, @"^(?:'(?:[^']|'')*'|-?\d+(?:\.\d+)?|NULL|TRUE|FALSE|CURRENT_TIMESTAMP|CURRENT_DATE|CURRENT_TIME|\(.*\))$", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                ? value : $"({value})";

        private string KeyDefinition(DesignForeignKey key) =>
            $"CONSTRAINT {Q(key.Name)} FOREIGN KEY ({Columns(key.Columns)}) REFERENCES {Q(key.ReferencedTable)} ({Columns(key.ReferencedColumns)}) ON DELETE {ReferentialAction(key.OnDelete)} ON UPDATE {ReferentialAction(key.OnUpdate)}";

        private string CreateTable(string name)
        {
            var lines = Target.Columns.Select(ColumnDefinition).ToList();
            var pk = PrimaryKey(Target);
            if (pk.Count > 0 && !pk.Any(InlineKey)) lines.Add($"PRIMARY KEY ({Columns(pk.Select(c => c.Name))})");
            lines.AddRange(Target.Indexes.Where(i => i.IsConstraint).Select(i => $"CONSTRAINT {Q(i.Name)} UNIQUE ({IndexColumns(i.Columns)})"));
            lines.AddRange(Target.ForeignKeys.Select(KeyDefinition));
            return $"CREATE TABLE {Q(name)} (\n  {string.Join(",\n  ", lines)}\n)";
        }

        private string CreateIndex(DesignIndex index) =>
            $"CREATE {(index.Kind == "UNIQUE" ? "UNIQUE " : string.Empty)}INDEX {Q(index.Name)} ON {Table} ({IndexColumns(index.Columns)})";

        private string Trigger(DesignTrigger trigger)
        {
            var body = trigger.Body.Trim();
            if (!Regex.IsMatch(body, @"^(FOR\s+EACH|WHEN|BEGIN)\b", RegexOptions.IgnoreCase)) body = $"BEGIN\n  {body.TrimEnd(';')};\nEND";
            if (!body.EndsWith(';')) body += ";";
            return $"CREATE TRIGGER {Q(trigger.Name)} {trigger.Timing} {trigger.Events} ON {Table} {body}";
        }

        public IReadOnlyList<string> Build(TableDesign? original)
        {
            var statements = new List<string>();
            if (original is null)
            {
                statements.Add(CreateTable(Target.Name));
                statements.AddRange(Target.Indexes.Where(i => !i.IsConstraint).Select(CreateIndex));
                statements.AddRange(Target.Triggers.Select(Trigger));
                return statements;
            }

            var removed = Removed(original.Columns, Target.Columns, c => c.OriginalName);
            var added = Target.Columns.Where(c => c.OriginalName is null || original.Columns.All(o => o.OriginalName != c.OriginalName)).ToList();
            var changed = Target.Columns.Where(c => original.Columns.FirstOrDefault(o => o.OriginalName == c.OriginalName && c.OriginalName is not null) is { } before
                                                    && !SameColumn(before with { Name = c.Name }, c)).ToList();
            var renamed = Target.Columns.Where(c => original.Columns.FirstOrDefault(o => o.OriginalName == c.OriginalName && c.OriginalName is not null) is { } before && before.Name != c.Name).ToList();
            var order = Target.Columns.Where(c => c.OriginalName is not null && original.Columns.Any(o => o.OriginalName == c.OriginalName)).Select(c => c.OriginalName).ToList();
            var originalOrder = original.Columns.Where(o => order.Contains(o.OriginalName)).Select(o => o.OriginalName).ToList();
            var keyChanged = !PrimaryKey(original).Select(c => Target.Columns.FirstOrDefault(t => t.OriginalName == c.OriginalName)?.Name ?? "\0")
                .SequenceEqual(PrimaryKey(Target).Select(c => c.Name));
            var keysChanged = original.ForeignKeys.Count != Target.ForeignKeys.Count
                              || original.ForeignKeys.Any(o => Target.ForeignKeys.FirstOrDefault(t => t.OriginalName == o.OriginalName) is not { } t || !KeyUnchanged(o, t));
            var constraintsChanged = original.Indexes.Where(i => i.IsConstraint).Count() != Target.Indexes.Count(i => i.IsConstraint)
                                     || original.Indexes.Where(i => i.IsConstraint).Any(o => Target.Indexes.FirstOrDefault(t => t.OriginalName == o.OriginalName) is not { } t || !IndexUnchanged(o, t));
            // Le colonne aggiunte con ALTER TABLE non possono essere chiave primaria, UNIQUE, NOT NULL senza default o con default non costante.
            var simpleAdds = added.All(c => c.KeyOrder == 0 && !c.AutoIncrement && (!c.NotNull || !string.IsNullOrWhiteSpace(c.Default)) && (!c.IsVirtual || !c.StoredGenerated)
                                            && (string.IsNullOrWhiteSpace(c.Default) || !WrapDefault(c.Default.Trim()).StartsWith('(')));
            var simpleDrops = removed.All(c => c.KeyOrder == 0 && Target.Indexes.Concat(original.Indexes).All(i => !i.Columns.Contains(c.Name)) && original.ForeignKeys.All(k => !k.Columns.Contains(c.Name)));
            var rebuild = changed.Count > 0 || keyChanged || keysChanged || constraintsChanged || !simpleAdds || !simpleDrops || !order.SequenceEqual(originalOrder)
                          || (added.Count > 0 && Target.Columns.ToList().IndexOf(added[0]) < Target.Columns.Count - added.Count);

            // Trigger: quelli da modificare si eliminano sempre (e si ricreano alla fine).
            foreach (var trigger in original.Triggers)
                if (rebuild || Target.Triggers.FirstOrDefault(t => t.OriginalName == trigger.OriginalName) is not { } t || t with { OriginalName = null } != trigger with { OriginalName = null })
                    statements.Add($"DROP TRIGGER IF EXISTS {Q(trigger.OriginalName!)}");

            if (!rebuild)
            {
                foreach (var column in renamed) statements.Add($"ALTER TABLE {Table} RENAME COLUMN {Q(original.Columns.First(o => o.OriginalName == column.OriginalName).Name)} TO {Q(column.Name)}");
                foreach (var column in removed) statements.Add($"ALTER TABLE {Table} DROP COLUMN {Q(column.OriginalName!)}");
                foreach (var column in added) statements.Add($"ALTER TABLE {Table} ADD COLUMN {ColumnDefinition(column)}");
                foreach (var index in original.Indexes.Where(i => !i.IsConstraint))
                    if (Target.Indexes.FirstOrDefault(t => t.OriginalName == index.OriginalName) is not { } now || !IndexUnchanged(index, now) || now.Name != index.Name)
                        statements.Add($"DROP INDEX IF EXISTS {Q(index.OriginalName!)}");
                foreach (var index in Target.Indexes.Where(i => !i.IsConstraint))
                    if (index.OriginalName is null || original.Indexes.FirstOrDefault(o => o.OriginalName == index.OriginalName) is not { } o || !IndexUnchanged(o, index) || o.Name != index.Name)
                        statements.Add(CreateIndex(index));
                foreach (var trigger in Target.Triggers)
                    if (trigger.OriginalName is null || original.Triggers.FirstOrDefault(o => o.OriginalName == trigger.OriginalName) is not { } o || o with { OriginalName = null } != trigger with { OriginalName = null })
                        statements.Add(Trigger(trigger));
                return statements;
            }

            // Ricostruzione: le chiavi esterne si verificano solo al COMMIT, così la sostituzione della tabella non le viola.
            var temporary = $"_navifox_new_{Target.Name}";
            statements.Add("PRAGMA defer_foreign_keys = ON");
            statements.Add(CreateTable(temporary));
            var copied = Target.Columns.Where(c => !c.IsVirtual && c.OriginalName is not null && original.Columns.FirstOrDefault(o => o.OriginalName == c.OriginalName) is { IsVirtual: false }).ToList();
            // Colonna diventata NOT NULL con un valore predefinito: i NULL esistenti prendono il predefinito.
            string Source(DesignColumn c)
            {
                var before = original.Columns.First(o => o.OriginalName == c.OriginalName);
                return c.NotNull && !before.NotNull && !string.IsNullOrWhiteSpace(c.Default) ? $"COALESCE({Q(before.Name)}, {WrapDefault(c.Default.Trim())})" : Q(before.Name);
            }
            if (copied.Count > 0)
                statements.Add($"INSERT INTO {Q(temporary)} ({Columns(copied.Select(c => c.Name))})\n  SELECT {string.Join(", ", copied.Select(Source))} FROM {Q(original.Name)}");
            statements.Add($"DROP TABLE {Q(original.Name)}");
            statements.Add($"ALTER TABLE {Q(temporary)} RENAME TO {Table}");
            statements.AddRange(Target.Indexes.Where(i => !i.IsConstraint).Select(CreateIndex));
            statements.AddRange(Target.Triggers.Select(Trigger));
            return statements;
        }
    }
}
