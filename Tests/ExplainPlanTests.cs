using Navifox.Core;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.Tests;

public sealed class ExplainPlanTests
{
    private static QueryResult Text(string text) => new([new ColumnInfo("QUERY PLAN", "text", true)], [[text]], 0, false);

    [Fact]
    public void PostgresJson()
    {
        var json = """
        [{"Plan": {"Node Type": "Hash Join", "Join Type": "Inner", "Total Cost": 100.0, "Plan Rows": 50, "Hash Cond": "(o.cliente = c.id)",
          "Plans": [
            {"Node Type": "Seq Scan", "Relation Name": "ordini", "Alias": "o", "Total Cost": 80.0, "Plan Rows": 20000},
            {"Node Type": "Hash", "Total Cost": 10.0, "Plan Rows": 10, "Plans": [
              {"Node Type": "Index Scan", "Relation Name": "clienti", "Alias": "c", "Index Name": "clienti_pkey", "Total Cost": 9.0, "Plan Rows": 10}]}
          ]}}]
        """;
        var root = ExplainPlan.Parse(DatabaseKind.PostgreSql, Text(json))!;
        Assert.Equal("Hash Join", root.Operation);
        Assert.Equal(2, root.Children.Count);
        var seq = root.Children[0];
        Assert.Equal("Seq Scan", seq.Operation);
        Assert.StartsWith("ordini o", seq.Detail);
        Assert.True(seq.IsExpensive);
        Assert.Equal(80, seq.CostShare, 1);
        Assert.False(root.Children[1].Children[0].IsExpensive);
    }

    [Fact]
    public void MySqlTree()
    {
        var tree = "-> Nested loop inner join  (cost=2510.50 rows=5000)\n    -> Table scan on o  (cost=500.25 rows=5000)\n    -> Single-row index lookup on c using PRIMARY (id=o.cliente)  (cost=0.25 rows=1)\n";
        var root = ExplainPlan.Parse(DatabaseKind.MySql, Text(tree))!;
        Assert.Equal("Nested loop inner join", root.Operation);
        Assert.Equal(2510.5, root.Cost);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal("Table scan on o", root.Children[0].Operation);
        Assert.True(root.Children[0].IsExpensive);
        Assert.False(root.Children[1].IsExpensive);
    }

    [Fact]
    public void SqliteQueryPlan()
    {
        var result = new QueryResult(
            [new("id", "int", false), new("parent", "int", false), new("notused", "int", false), new("detail", "text", false)],
            [[2L, 0L, 0L, "SCAN ordini"], [5L, 0L, 0L, "SEARCH clienti USING INTEGER PRIMARY KEY (rowid=?)"], [9L, 0L, 0L, "USE TEMP B-TREE FOR ORDER BY"]], 0, false);
        var root = ExplainPlan.Parse(DatabaseKind.Sqlite, result)!;
        Assert.Equal(3, root.Children.Count);
        Assert.True(root.Children[0].IsExpensive);
        Assert.False(root.Children[1].IsExpensive);
        Assert.True(root.Children[2].IsExpensive);
    }

    [Fact]
    public void SqlServerShowplan()
    {
        var xml = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan>
          <RelOp PhysicalOp="Hash Match" LogicalOp="Inner Join" EstimateRows="50" EstimatedTotalSubtreeCost="1.5">
            <Hash>
              <RelOp PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="20000" EstimatedTotalSubtreeCost="1.2">
                <IndexScan><Object Database="[db]" Schema="[dbo]" Table="[ordini]" Index="[PK_ordini]" /></IndexScan>
              </RelOp>
              <RelOp PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="10" EstimatedTotalSubtreeCost="0.01">
                <IndexScan><Object Table="[clienti]" Index="[IX_clienti]" /></IndexScan>
              </RelOp>
            </Hash>
          </RelOp>
        </QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>
        """;
        var root = ExplainPlan.Parse(DatabaseKind.SqlServer, Text(xml))!;
        Assert.Equal("Hash Match (Inner Join)", root.Operation);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal("Clustered Index Scan", root.Children[0].Operation);
        Assert.Equal("ordini.PK_ordini", root.Children[0].Detail);
        Assert.True(root.Children[0].IsExpensive);
        Assert.False(root.Children[1].IsExpensive);
    }
}
