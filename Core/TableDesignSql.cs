using System.Text;
using System.Text.RegularExpressions;
using Navifox.Core.Models;

namespace Navifox.Core;

/// <summary>
/// Genera le istruzioni SQL di «Progetta tabella»: CREATE TABLE per una tabella nuova, oppure le ALTER TABLE
/// necessarie a passare dalla struttura caricata (<c>original</c>) a quella modificata (<c>target</c>).
/// </summary>
public static partial class TableDesignSql
{
    private static readonly HashSet<string> MySqlTextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "tinytext", "text", "mediumtext", "longtext",
    };

    private static readonly HashSet<string> DecimalTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "decimal", "numeric", "float", "double", "real", "dec", "fixed",
    };

    private static readonly Regex IndexPart = new(@"^(?<name>.+?)(?<prefix>\(\d+\))?(?<order>\s+(?:ASC|DESC))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<string> Build(bool mysql, string schema, TableDesign? original, TableDesign target) =>
        mysql ? new MySqlWriter(target).Build(original) : new PostgresWriter(schema, target).Build(original);

    // ───────────────────────── comuni ─────────────────────────

    private static string TypeWithLength(DesignColumn c)
    {
        var type = c.Type.Trim();
        if (type.Contains('(') || c.Length is not > 0) return type;
        var args = c.Decimals is { } d && (d > 0 || DecimalTypes.Contains(type)) ? $"({c.Length},{d})" : $"({c.Length})";
        // Array PostgreSQL: varchar(20)[]
        return type.EndsWith("[]", StringComparison.Ordinal) ? type[..type.IndexOf('[')] + args + type[type.IndexOf('[')..] : type + args;
    }

    private static bool SameColumn(DesignColumn a, DesignColumn b) => (a with { OriginalName = null }) == (b with { OriginalName = null });

    private static bool SameIndex(DesignIndex a, DesignIndex b, bool ignoreName = false) =>
        (ignoreName || a.Name == b.Name) && a.Kind == b.Kind && (a.Method ?? string.Empty).Equals(b.Method ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && a.Comment == b.Comment && a.Columns.SequenceEqual(b.Columns);

    private static bool SameKey(DesignForeignKey a, DesignForeignKey b) =>
        a.Name == b.Name && a.ReferencedSchema == b.ReferencedSchema && a.ReferencedTable == b.ReferencedTable && a.OnDelete == b.OnDelete && a.OnUpdate == b.OnUpdate
        && a.Columns.SequenceEqual(b.Columns) && a.ReferencedColumns.SequenceEqual(b.ReferencedColumns);

    private static IReadOnlyList<DesignColumn> PrimaryKey(TableDesign design) =>
        design.Columns.Where(c => c.KeyOrder > 0).OrderBy(c => c.KeyOrder).ToArray();

    private abstract class Writer(TableDesign target)
    {
        protected TableDesign Target { get; } = target;
        protected abstract string Q(string identifier);
        protected abstract string Literal(string value);

        /// <summary>Colonna di indice: i nomi di colonna vanno quotati, le espressioni restano come sono.</summary>
        protected string IndexColumn(string part)
        {
            var m = IndexPart.Match(part.Trim());
            if (!m.Success) return part;
            var name = m.Groups["name"].Value.Trim();
            var unquoted = name.Trim('`', '"');
            return Target.Columns.Any(c => c.Name == unquoted) ? Q(unquoted) + m.Groups["prefix"].Value + m.Groups["order"].Value.ToUpperInvariant() : part.Trim();
        }

        protected string Columns(IEnumerable<string> names) => string.Join(", ", names.Select(Q));
        protected string IndexColumns(IEnumerable<string> parts) => string.Join(", ", parts.Select(IndexColumn));

        /// <summary>Nome di colonna originale → nome attuale (per confrontare indici e chiavi dopo una rinomina).</summary>
        protected string Renamed(string part)
        {
            var m = IndexPart.Match(part.Trim());
            if (!m.Success) return part;
            var name = m.Groups["name"].Value.Trim().Trim('`', '"');
            return Target.Columns.FirstOrDefault(c => c.OriginalName == name) is { } column && column.Name != name
                ? column.Name + m.Groups["prefix"].Value + m.Groups["order"].Value
                : part;
        }

        protected bool IndexUnchanged(DesignIndex before, DesignIndex now) =>
            SameIndex(before with { Columns = before.Columns.Select(Renamed).ToArray() }, now, ignoreName: true);

        protected bool KeyUnchanged(DesignForeignKey before, DesignForeignKey now) =>
            SameKey(before with { Columns = before.Columns.Select(Renamed).ToArray() }, now);

        protected static List<T> Removed<T>(IReadOnlyList<T> original, IReadOnlyList<T> target, Func<T, string?> originalName) =>
            original.Where(o => target.All(t => originalName(t) != originalName(o))).ToList();
    }

    // ───────────────────────── MySQL / MariaDB ─────────────────────────

    private sealed class MySqlWriter(TableDesign target) : Writer(target)
    {
        protected override string Q(string identifier) => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
        protected override string Literal(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";

        private string Table => Q(Target.Name);

        private string ColumnDefinition(DesignColumn c)
        {
            var sql = new StringBuilder($"{Q(c.Name)} {TypeWithLength(c)}");
            if (c.Unsigned) sql.Append(" UNSIGNED");
            if (c.Zerofill) sql.Append(" ZEROFILL");
            if (c.Collation is { Length: > 0 } collation && MySqlTextTypes.Contains(c.Type)) sql.Append($" COLLATE {collation}");
            if (c.IsVirtual && !string.IsNullOrWhiteSpace(c.Expression)) sql.Append($" GENERATED ALWAYS AS ({c.Expression}) {(c.StoredGenerated ? "STORED" : "VIRTUAL")}");
            sql.Append(c.NotNull ? " NOT NULL" : " NULL");
            if (!c.IsVirtual && !string.IsNullOrWhiteSpace(c.Default)) sql.Append($" DEFAULT {c.Default.Trim()}");
            if (c.AutoIncrement) sql.Append(" AUTO_INCREMENT");
            if (!string.IsNullOrWhiteSpace(c.OnUpdate)) sql.Append($" ON UPDATE {c.OnUpdate.Trim()}");
            if (!string.IsNullOrEmpty(c.Comment)) sql.Append($" COMMENT {Literal(c.Comment)}");
            return sql.ToString();
        }

        private string IndexDefinition(DesignIndex index)
        {
            var kind = index.Kind.ToUpperInvariant() switch { "UNIQUE" => "UNIQUE INDEX", "FULLTEXT" => "FULLTEXT INDEX", "SPATIAL" => "SPATIAL INDEX", _ => "INDEX" };
            var sql = $"{kind} {Q(index.Name)}({IndexColumns(index.Columns)})";
            if (!string.IsNullOrWhiteSpace(index.Method) && index.Kind is not ("FULLTEXT" or "SPATIAL")) sql += $" USING {index.Method.ToUpperInvariant()}";
            if (!string.IsNullOrEmpty(index.Comment)) sql += $" COMMENT {Literal(index.Comment)}";
            return sql;
        }

        private string KeyDefinition(DesignForeignKey key)
        {
            var table = string.IsNullOrEmpty(key.ReferencedSchema) ? Q(key.ReferencedTable) : $"{Q(key.ReferencedSchema)}.{Q(key.ReferencedTable)}";
            return $"CONSTRAINT {Q(key.Name)} FOREIGN KEY ({Columns(key.Columns)}) REFERENCES {table} ({Columns(key.ReferencedColumns)}) ON DELETE {key.OnDelete} ON UPDATE {key.OnUpdate}";
        }

        private string Trigger(DesignTrigger trigger) =>
            $"CREATE TRIGGER {Q(trigger.Name)} {trigger.Timing} {trigger.Events} ON {Table} FOR EACH ROW {trigger.Body.Trim().TrimEnd(';')}";

        private List<string> TableOptions(TableDesignOptions? original)
        {
            var o = Target.Options;
            var options = new List<string>();
            if (!string.IsNullOrEmpty(o.Engine) && o.Engine != original?.Engine) options.Add($"ENGINE = {o.Engine}");
            if (!string.IsNullOrEmpty(o.Charset) && o.Charset != original?.Charset) options.Add($"DEFAULT CHARSET = {o.Charset}");
            if (!string.IsNullOrEmpty(o.Collation) && o.Collation != original?.Collation) options.Add($"COLLATE = {o.Collation}");
            if (o.AutoIncrement is > 0 && o.AutoIncrement != original?.AutoIncrement) options.Add($"AUTO_INCREMENT = {o.AutoIncrement}");
            if (!string.IsNullOrEmpty(o.RowFormat) && o.RowFormat != original?.RowFormat) options.Add($"ROW_FORMAT = {o.RowFormat}");
            return options;
        }

        public IReadOnlyList<string> Build(TableDesign? original)
        {
            var statements = new List<string>();
            if (original is null)
            {
                var lines = Target.Columns.Select(ColumnDefinition).ToList();
                var pk = PrimaryKey(Target);
                if (pk.Count > 0) lines.Add($"PRIMARY KEY ({Columns(pk.Select(c => c.Name))})");
                lines.AddRange(Target.Indexes.Select(IndexDefinition));
                lines.AddRange(Target.ForeignKeys.Select(KeyDefinition));
                var options = TableOptions(null);
                if (!string.IsNullOrEmpty(Target.Comment)) options.Add($"COMMENT = {Literal(Target.Comment)}");
                statements.Add($"CREATE TABLE {Table} (\n  {string.Join(",\n  ", lines)}\n){(options.Count > 0 ? " " + string.Join(" ", options) : string.Empty)}");
                statements.AddRange(Target.Triggers.Select(Trigger));
                return statements;
            }

            // 1. Trigger e chiavi esterne da rimuovere o ricreare.
            foreach (var trigger in original.Triggers.Where(o => Target.Triggers.FirstOrDefault(t => t.OriginalName == o.OriginalName) is not { } t || t with { OriginalName = null } != o with { OriginalName = null }))
                statements.Add($"DROP TRIGGER IF EXISTS {Q(trigger.OriginalName!)}");
            var changedKeys = original.ForeignKeys.Where(o => Target.ForeignKeys.FirstOrDefault(t => t.OriginalName == o.OriginalName) is not { } t || !KeyUnchanged(o, t)).ToList();
            if (changedKeys.Count > 0)
                statements.Add($"ALTER TABLE {Table}\n  {string.Join(",\n  ", changedKeys.Select(k => $"DROP FOREIGN KEY {Q(k.OriginalName!)}"))}");

            // 2. Colonne, chiave primaria, indici e opzioni in un'unica ALTER TABLE.
            var clauses = new List<string>();
            foreach (var column in Removed(original.Columns, Target.Columns, c => c.OriginalName)) clauses.Add($"DROP COLUMN {Q(column.OriginalName!)}");

            var survivorsBefore = original.Columns.Where(o => Target.Columns.Any(t => t.OriginalName == o.OriginalName)).Select(o => o.OriginalName).ToList();
            var survivorsAfter = Target.Columns.Where(t => t.OriginalName is not null && survivorsBefore.Contains(t.OriginalName)).Select(t => t.OriginalName).ToList();
            for (var i = 0; i < Target.Columns.Count; i++)
            {
                var column = Target.Columns[i];
                var position = i == 0 ? " FIRST" : $" AFTER {Q(Target.Columns[i - 1].Name)}";
                if (column.OriginalName is null || original.Columns.FirstOrDefault(o => o.OriginalName == column.OriginalName) is not { } before)
                {
                    clauses.Add($"ADD COLUMN {ColumnDefinition(column)}{position}");
                    continue;
                }
                var index = survivorsAfter.IndexOf(column.OriginalName);
                var moved = (index == 0 ? null : survivorsAfter[index - 1]) != (survivorsBefore.IndexOf(column.OriginalName) is var b and > 0 ? survivorsBefore[b - 1] : null);
                if (!SameColumn(before, column) || moved)
                    clauses.Add($"CHANGE COLUMN {Q(column.OriginalName)} {ColumnDefinition(column)}{position}");
            }

            var oldKey = PrimaryKey(original).Select(c => Target.Columns.FirstOrDefault(t => t.OriginalName == c.OriginalName)?.Name ?? $"\0{c.Name}").ToArray();
            var newKey = PrimaryKey(Target).Select(c => c.Name).ToArray();
            if (!oldKey.SequenceEqual(newKey))
            {
                if (oldKey.Length > 0) clauses.Add("DROP PRIMARY KEY");
                if (newKey.Length > 0) clauses.Add($"ADD PRIMARY KEY ({Columns(newKey)})");
            }

            foreach (var index in original.Indexes)
            {
                var now = Target.Indexes.FirstOrDefault(t => t.OriginalName == index.OriginalName);
                if (now is null || !IndexUnchanged(index, now)) clauses.Add($"DROP INDEX {Q(index.OriginalName!)}");
                else if (now.Name != index.Name) clauses.Add($"RENAME INDEX {Q(index.OriginalName!)} TO {Q(now.Name)}");
            }
            foreach (var index in Target.Indexes)
                if (index.OriginalName is null || original.Indexes.FirstOrDefault(o => o.OriginalName == index.OriginalName) is not { } o || !IndexUnchanged(o, index))
                    clauses.Add($"ADD {IndexDefinition(index)}");

            clauses.AddRange(TableOptions(original.Options));
            if ((Target.Comment ?? string.Empty) != (original.Comment ?? string.Empty)) clauses.Add($"COMMENT = {Literal(Target.Comment ?? string.Empty)}");
            if (clauses.Count > 0) statements.Add($"ALTER TABLE {Table}\n  {string.Join(",\n  ", clauses)}");

            // 3. Chiavi esterne e trigger nuovi o modificati.
            var addedKeys = Target.ForeignKeys.Where(t => t.OriginalName is null || changedKeys.Any(c => c.OriginalName == t.OriginalName)).ToList();
            if (addedKeys.Count > 0)
                statements.Add($"ALTER TABLE {Table}\n  {string.Join(",\n  ", addedKeys.Select(k => "ADD " + KeyDefinition(k)))}");
            foreach (var trigger in Target.Triggers)
                if (trigger.OriginalName is null || original.Triggers.FirstOrDefault(o => o.OriginalName == trigger.OriginalName) is not { } o || o with { OriginalName = null } != trigger with { OriginalName = null })
                    statements.Add(Trigger(trigger));
            return statements;
        }
    }

    // ───────────────────────── PostgreSQL ─────────────────────────

    private sealed class PostgresWriter(string schema, TableDesign target) : Writer(target)
    {
        protected override string Q(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        protected override string Literal(string value) => "'" + value.Replace("'", "''") + "'";

        private string Table => $"{Q(schema)}.{Q(Target.Name)}";
        private string InSchema(string name) => $"{Q(schema)}.{Q(name)}";

        private string ColumnDefinition(DesignColumn c)
        {
            var sql = new StringBuilder($"{Q(c.Name)} {TypeWithLength(c)}");
            if (c.IsVirtual && !string.IsNullOrWhiteSpace(c.Expression)) sql.Append($" GENERATED ALWAYS AS ({c.Expression}) STORED");
            else if (c.AutoIncrement) sql.Append(" GENERATED BY DEFAULT AS IDENTITY");
            if (c.NotNull) sql.Append(" NOT NULL");
            if (!c.IsVirtual && !c.AutoIncrement && !string.IsNullOrWhiteSpace(c.Default)) sql.Append($" DEFAULT {c.Default.Trim()}");
            return sql.ToString();
        }

        private string CommentOn(string what, string? comment) => $"COMMENT ON {what} IS {(string.IsNullOrEmpty(comment) ? "NULL" : Literal(comment))}";

        private string CreateIndex(DesignIndex index)
        {
            var method = string.IsNullOrWhiteSpace(index.Method) || index.Method.Equals("btree", StringComparison.OrdinalIgnoreCase) ? string.Empty : $" USING {index.Method.ToLowerInvariant()}";
            return $"CREATE {(index.Kind == "UNIQUE" ? "UNIQUE " : string.Empty)}INDEX {Q(index.Name)} ON {Table}{method} ({IndexColumns(index.Columns)})";
        }

        private string AddKey(DesignForeignKey key)
        {
            var table = string.IsNullOrEmpty(key.ReferencedSchema) ? InSchema(key.ReferencedTable) : $"{Q(key.ReferencedSchema)}.{Q(key.ReferencedTable)}";
            return $"ALTER TABLE {Table} ADD CONSTRAINT {Q(key.Name)} FOREIGN KEY ({Columns(key.Columns)}) REFERENCES {table} ({Columns(key.ReferencedColumns)}) ON DELETE {key.OnDelete} ON UPDATE {key.OnUpdate}";
        }

        private string Trigger(DesignTrigger trigger)
        {
            var body = trigger.Body.Trim().TrimEnd(';');
            if (!Regex.IsMatch(body, @"^(FOR\s+EACH|REFERENCING|FROM|NOT\s+DEFERRABLE|DEFERRABLE|INITIALLY|WHEN|EXECUTE)", RegexOptions.IgnoreCase)) body = "FOR EACH ROW " + body;
            return $"CREATE TRIGGER {Q(trigger.Name)} {trigger.Timing} {trigger.Events} ON {Table} {body}";
        }

        public IReadOnlyList<string> Build(TableDesign? original)
        {
            var statements = new List<string>();
            if (original is null)
            {
                var lines = Target.Columns.Select(ColumnDefinition).ToList();
                var pk = PrimaryKey(Target);
                if (pk.Count > 0) lines.Add($"PRIMARY KEY ({Columns(pk.Select(c => c.Name))})");
                statements.Add($"CREATE TABLE {Table} (\n  {string.Join(",\n  ", lines)}\n)");
                foreach (var column in Target.Columns.Where(c => !string.IsNullOrEmpty(c.Comment))) statements.Add(CommentOn($"COLUMN {Table}.{Q(column.Name)}", column.Comment));
                foreach (var index in Target.Indexes)
                {
                    statements.Add(CreateIndex(index));
                    if (!string.IsNullOrEmpty(index.Comment)) statements.Add(CommentOn($"INDEX {InSchema(index.Name)}", index.Comment));
                }
                statements.AddRange(Target.ForeignKeys.Select(AddKey));
                statements.AddRange(Target.Triggers.Select(Trigger));
                if (!string.IsNullOrEmpty(Target.Comment)) statements.Add(CommentOn($"TABLE {Table}", Target.Comment));
                return statements;
            }

            // 1. Rimozioni: trigger, chiavi esterne, indici, chiave primaria, colonne.
            foreach (var trigger in original.Triggers.Where(o => Target.Triggers.FirstOrDefault(t => t.OriginalName == o.OriginalName) is not { } t || t with { OriginalName = null } != o with { OriginalName = null }))
                statements.Add($"DROP TRIGGER IF EXISTS {Q(trigger.OriginalName!)} ON {Table}");
            var changedKeys = original.ForeignKeys.Where(o => Target.ForeignKeys.FirstOrDefault(t => t.OriginalName == o.OriginalName) is not { } t || !KeyUnchanged(o, t)).ToList();
            statements.AddRange(changedKeys.Select(k => $"ALTER TABLE {Table} DROP CONSTRAINT {Q(k.OriginalName!)}"));
            var recreatedIndexes = new List<DesignIndex>();
            foreach (var index in original.Indexes)
            {
                var now = Target.Indexes.FirstOrDefault(t => t.OriginalName == index.OriginalName);
                if (now is not null && IndexUnchanged(index, now))
                {
                    if (now.Name != index.Name) statements.Add($"ALTER INDEX {InSchema(index.OriginalName!)} RENAME TO {Q(now.Name)}");
                    continue;
                }
                statements.Add(index.IsConstraint ? $"ALTER TABLE {Table} DROP CONSTRAINT {Q(index.OriginalName!)}" : $"DROP INDEX {InSchema(index.OriginalName!)}");
                if (now is not null) recreatedIndexes.Add(now);
            }

            var oldKey = PrimaryKey(original).Select(c => Target.Columns.FirstOrDefault(t => t.OriginalName == c.OriginalName)?.Name ?? $"\0{c.Name}").ToArray();
            var newKey = PrimaryKey(Target).Select(c => c.Name).ToArray();
            var keyChanged = !oldKey.SequenceEqual(newKey);
            if (keyChanged && oldKey.Length > 0) statements.Add($"ALTER TABLE {Table} DROP CONSTRAINT {Q(original.PrimaryKeyName ?? original.Name + "_pkey")}");

            foreach (var column in Removed(original.Columns, Target.Columns, c => c.OriginalName)) statements.Add($"ALTER TABLE {Table} DROP COLUMN {Q(column.OriginalName!)}");

            // 2. Colonne modificate (rinomina, tipo, NOT NULL, default, identità, espressione, commento) e nuove.
            foreach (var column in Target.Columns)
            {
                if (column.OriginalName is null || original.Columns.FirstOrDefault(o => o.OriginalName == column.OriginalName) is not { } before)
                {
                    statements.Add($"ALTER TABLE {Table} ADD COLUMN {ColumnDefinition(column)}");
                    if (!string.IsNullOrEmpty(column.Comment)) statements.Add(CommentOn($"COLUMN {Table}.{Q(column.Name)}", column.Comment));
                    continue;
                }
                if (SameColumn(before, column)) continue;
                var name = Q(column.Name);
                if (before.Name != column.Name) statements.Add($"ALTER TABLE {Table} RENAME COLUMN {Q(before.Name)} TO {name}");
                var actions = new List<string>();
                var type = TypeWithLength(column);
                if (!type.Equals(TypeWithLength(before), StringComparison.OrdinalIgnoreCase)) actions.Add($"ALTER COLUMN {name} TYPE {type} USING {name}::{type}");
                if (before.AutoIncrement && !column.AutoIncrement) actions.Add($"ALTER COLUMN {name} DROP IDENTITY IF EXISTS");
                if ((before.Default ?? string.Empty) != (column.Default ?? string.Empty) && !column.AutoIncrement && !column.IsVirtual)
                    actions.Add(string.IsNullOrWhiteSpace(column.Default) ? $"ALTER COLUMN {name} DROP DEFAULT" : $"ALTER COLUMN {name} SET DEFAULT {column.Default.Trim()}");
                if (before.NotNull != column.NotNull) actions.Add($"ALTER COLUMN {name} {(column.NotNull ? "SET" : "DROP")} NOT NULL");
                if (!before.AutoIncrement && column.AutoIncrement)
                {
                    if (!column.NotNull) actions.Add($"ALTER COLUMN {name} SET NOT NULL");
                    if (!string.IsNullOrWhiteSpace(before.Default)) actions.Add($"ALTER COLUMN {name} DROP DEFAULT");
                    actions.Add($"ALTER COLUMN {name} ADD GENERATED BY DEFAULT AS IDENTITY");
                }
                if (column.IsVirtual && before.IsVirtual && column.Expression != before.Expression) actions.Add($"ALTER COLUMN {name} SET EXPRESSION AS ({column.Expression})");
                if (before.IsVirtual && !column.IsVirtual) actions.Add($"ALTER COLUMN {name} DROP EXPRESSION");
                if (actions.Count > 0) statements.Add($"ALTER TABLE {Table}\n  {string.Join(",\n  ", actions)}");
                if ((before.Comment ?? string.Empty) != (column.Comment ?? string.Empty)) statements.Add(CommentOn($"COLUMN {Table}.{name}", column.Comment));
            }

            // 3. Aggiunte: chiave primaria, indici, chiavi esterne, trigger, commento.
            if (keyChanged && newKey.Length > 0) statements.Add($"ALTER TABLE {Table} ADD PRIMARY KEY ({Columns(newKey)})");
            foreach (var index in Target.Indexes.Where(i => i.OriginalName is null).Concat(recreatedIndexes))
            {
                statements.Add(CreateIndex(index));
                if (!string.IsNullOrEmpty(index.Comment)) statements.Add(CommentOn($"INDEX {InSchema(index.Name)}", index.Comment));
            }
            statements.AddRange(Target.ForeignKeys.Where(t => t.OriginalName is null || changedKeys.Any(c => c.OriginalName == t.OriginalName)).Select(AddKey));
            foreach (var trigger in Target.Triggers)
                if (trigger.OriginalName is null || original.Triggers.FirstOrDefault(o => o.OriginalName == trigger.OriginalName) is not { } o || o with { OriginalName = null } != trigger with { OriginalName = null })
                    statements.Add(Trigger(trigger));
            if ((Target.Comment ?? string.Empty) != (original.Comment ?? string.Empty)) statements.Add(CommentOn($"TABLE {Table}", Target.Comment));
            return statements;
        }
    }
}
