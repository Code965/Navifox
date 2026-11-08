using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Navifox.Core.Models;

namespace Navifox.Core;

/// <summary>Passaggio del piano di esecuzione: operazione, oggetto, costo/righe stimati e figli.</summary>
public sealed class PlanNode
{
    public required string Operation { get; init; }
    public string Detail { get; init; } = string.Empty;
    public double? Cost { get; init; }
    public double? Rows { get; init; }
    public List<PlanNode> Children { get; } = [];
    /// <summary>Costo proprio (senza i figli) in percentuale sul totale del piano.</summary>
    public double CostShare { get; set; }
    /// <summary>Passaggio costoso: scansione completa di molte righe o gran parte del costo totale (si mostra in rosso).</summary>
    public bool IsExpensive { get; set; }
    public string Summary => string.Join(" · ", new[]
    {
        Cost is { } c ? $"costo {c.ToString("N1", CultureInfo.GetCultureInfo("it-IT"))}" : null,
        Rows is { } r ? $"{r.ToString("N0", CultureInfo.GetCultureInfo("it-IT"))} righe stimate" : null,
        CostShare >= 1 ? $"{CostShare:N0}% del costo" : null
    }.Where(s => s is not null));

    public IEnumerable<PlanNode> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);
}

/// <summary>
/// Piano di esecuzione come albero: la query da eseguire per ottenerlo e il parser del risultato,
/// per PostgreSQL (EXPLAIN FORMAT JSON), MySQL 8 (EXPLAIN FORMAT=TREE), SQLite (EXPLAIN QUERY PLAN) e SQL Server (SHOWPLAN_XML).
/// </summary>
public static class ExplainPlan
{
    public const double LargeScan = 1000;

    /// <summary>Istruzioni da eseguire (in ordine, sulla stessa connessione) per ottenere il piano.</summary>
    public static IReadOnlyList<string> Statements(DatabaseKind kind, string statement) => kind switch
    {
        DatabaseKind.PostgreSql => [$"EXPLAIN (FORMAT JSON) {statement}"],
        DatabaseKind.MySql => [$"EXPLAIN FORMAT=TREE {statement}"],
        DatabaseKind.Sqlite => [$"EXPLAIN QUERY PLAN {statement}"],
        DatabaseKind.SqlServer => ["SET SHOWPLAN_XML ON", statement, "SET SHOWPLAN_XML OFF"],
        _ => [$"EXPLAIN {statement}"]
    };

    /// <summary>Radice del piano dal testo/righe restituiti; null se il formato non è riconosciuto.</summary>
    public static PlanNode? Parse(DatabaseKind kind, QueryResult result)
    {
        var root = kind switch
        {
            DatabaseKind.PostgreSql => ParsePostgres(FirstText(result)),
            DatabaseKind.MySql => ParseMySqlTree(FirstText(result)),
            DatabaseKind.Sqlite => ParseSqlite(result),
            DatabaseKind.SqlServer => ParseSqlServer(FirstText(result)),
            _ => null
        };
        if (root is not null) Analyze(root);
        return root;
    }

    private static string FirstText(QueryResult result) =>
        string.Join("\n", result.Rows.Select(r => Convert.ToString(r.FirstOrDefault(), CultureInfo.InvariantCulture) ?? string.Empty));

    // ── PostgreSQL ──
    public static PlanNode? ParsePostgres(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        var plan = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement[0].GetProperty("Plan") : document.RootElement.GetProperty("Plan");
        return PostgresNode(plan);
    }

    private static PlanNode PostgresNode(JsonElement plan)
    {
        string Text(string name) => plan.TryGetProperty(name, out var v) ? v.ToString() : string.Empty;
        var detail = new List<string>();
        if (Text("Relation Name") is { Length: > 0 } relation) detail.Add(relation + (Text("Alias") is { Length: > 0 } alias && alias != relation ? $" {alias}" : string.Empty));
        if (Text("Index Name") is { Length: > 0 } index) detail.Add($"indice {index}");
        foreach (var key in new[] { "Join Type", "Hash Cond", "Index Cond", "Filter", "Sort Key", "Group Key" })
            if (Text(key) is { Length: > 0 } value) detail.Add($"{key}: {value}");
        var node = new PlanNode
        {
            Operation = Text("Node Type"),
            Detail = string.Join(" · ", detail),
            Cost = plan.TryGetProperty("Total Cost", out var cost) ? cost.GetDouble() : null,
            Rows = plan.TryGetProperty("Plan Rows", out var rows) ? rows.GetDouble() : null
        };
        if (plan.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray()) node.Children.Add(PostgresNode(child));
        return node;
    }

    // ── MySQL 8 (FORMAT=TREE) ──
    private static readonly Regex TreeLine = new(@"^(?<indent>\s*)->\s*(?<op>.+?)(?:\s+\((?<info>cost=[^)]*)\))?(?:\s+\(actual[^)]*\))?\s*$", RegexOptions.Compiled);

    public static PlanNode? ParseMySqlTree(string text)
    {
        var stack = new List<(int Indent, PlanNode Node)>();
        PlanNode? root = null;
        foreach (var line in text.Split('\n'))
        {
            var match = TreeLine.Match(line.TrimEnd('\r'));
            if (!match.Success) continue;
            var info = match.Groups["info"].Value;
            var operation = match.Groups["op"].Value.Trim();
            var colon = operation.IndexOf(':');
            var node = new PlanNode
            {
                Operation = colon > 0 ? operation[..colon].Trim() : operation,
                Detail = colon > 0 ? operation[(colon + 1)..].Trim() : string.Empty,
                Cost = Number(Regex.Match(info, @"cost=([\d.e+]+)").Groups[1].Value),
                Rows = Number(Regex.Match(info, @"rows=([\d.e+]+)").Groups[1].Value)
            };
            var indent = match.Groups["indent"].Value.Length;
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) root ??= node;
            else stack[^1].Node.Children.Add(node);
            stack.Add((indent, node));
        }
        return root;
    }

    // ── SQLite (EXPLAIN QUERY PLAN: id, parent, notused, detail) ──
    public static PlanNode? ParseSqlite(QueryResult result)
    {
        if (result.Rows.Count == 0) return null;
        var root = new PlanNode { Operation = "QUERY PLAN" };
        var byId = new Dictionary<long, PlanNode>();
        foreach (var row in result.Rows)
        {
            var id = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
            var parent = Convert.ToInt64(row[1], CultureInfo.InvariantCulture);
            var detail = Convert.ToString(row[^1], CultureInfo.InvariantCulture) ?? string.Empty;
            var space = detail.IndexOf(' ');
            var node = new PlanNode { Operation = space > 0 ? detail[..space] : detail, Detail = space > 0 ? detail[(space + 1)..] : string.Empty };
            byId[id] = node;
            (byId.TryGetValue(parent, out var owner) ? owner : root).Children.Add(node);
        }
        return root;
    }

    // ── SQL Server (SHOWPLAN_XML) ──
    public static PlanNode? ParseSqlServer(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || !xml.TrimStart().StartsWith('<')) return null;
        var document = XDocument.Parse(xml);
        var first = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "RelOp");
        return first is null ? null : SqlServerNode(first);
    }

    private static PlanNode SqlServerNode(XElement relOp)
    {
        string Attr(XElement e, string name) => e.Attribute(name)?.Value ?? string.Empty;
        var objects = relOp.Elements().Where(e => e.Name.LocalName != "RelOp")
            .SelectMany(e => e.Elements().Where(x => x.Name.LocalName == "Object"))
            .Select(o => string.Join(".", new[] { Attr(o, "Table"), Attr(o, "Index") }.Where(s => s.Length > 0)).Replace("[", "").Replace("]", ""))
            .Where(s => s.Length > 0).Distinct();
        var node = new PlanNode
        {
            Operation = Attr(relOp, "PhysicalOp") is { Length: > 0 } physical && Attr(relOp, "LogicalOp") is var logical && logical != physical ? $"{physical} ({logical})" : Attr(relOp, "PhysicalOp"),
            Detail = string.Join(" · ", objects),
            Cost = Number(Attr(relOp, "EstimatedTotalSubtreeCost")),
            Rows = Number(Attr(relOp, "EstimateRows"))
        };
        foreach (var child in ChildRelOps(relOp)) node.Children.Add(SqlServerNode(child));
        return node;
    }

    /// <summary>RelOp figli diretti (annidati dentro gli elementi dell'operatore, non dentro altri RelOp).</summary>
    private static IEnumerable<XElement> ChildRelOps(XElement relOp)
    {
        foreach (var element in relOp.Elements())
        {
            if (element.Name.LocalName == "RelOp") { yield return element; continue; }
            foreach (var nested in ChildRelOps(element)) yield return nested;
        }
    }

    // ── Analisi dei passaggi costosi ──
    private static void Analyze(PlanNode root)
    {
        var total = root.Cost ?? root.SelfAndDescendants().Max(n => n.Cost) ?? 0;
        foreach (var node in root.SelfAndDescendants())
        {
            var own = node.Cost is { } cost ? Math.Max(0, cost - node.Children.Sum(c => c.Cost ?? 0)) : 0;
            node.CostShare = total > 0 ? own * 100 / total : 0;
            var op = node.Operation.ToLowerInvariant();
            var full = $"{op} {node.Detail}".ToLowerInvariant();
            // Scansione completa della tabella: Seq Scan (PostgreSQL), Table scan (MySQL), Clustered Index Scan / Table Scan (SQL Server).
            var fullScan = op.StartsWith("seq scan") || op.StartsWith("table scan") || op.StartsWith("clustered index scan") || op == "full scan";
            // SQLite: «SCAN tabella» senza indice (le stime di righe non ci sono).
            var sqliteScan = op == "scan" && node.Cost is null && !full.Contains(" using ");
            node.IsExpensive = node.CostShare >= 30 && node != root
                               || fullScan && (node.Rows is null or >= LargeScan)
                               || sqliteScan
                               || full.Contains("temporary") || full.Contains("filesort") || full.Contains("use temp b-tree");
        }
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
