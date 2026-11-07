using Navifox.Core;
using Navifox.Core.Models;
using Xunit;

namespace Navifox.Tests;

public sealed class DmlPreviewTests
{
    [Theory]
    [InlineData("DELETE FROM orders WHERE status = 'void'", DatabaseKind.MySql, "SELECT COUNT(*) FROM orders WHERE status = 'void'", null)]
    [InlineData("delete from orders", DatabaseKind.PostgreSql, "SELECT COUNT(*) FROM orders", null)]
    [InlineData("UPDATE orders SET total = 0 WHERE id > 10;", DatabaseKind.MySql, "SELECT COUNT(*) FROM orders WHERE id > 10", null)]
    [InlineData("UPDATE `orders` o SET o.total = (SELECT 1 FROM x WHERE y) WHERE o.id IN (1, 2)", DatabaseKind.MySql, "SELECT COUNT(*) FROM `orders` o WHERE o.id IN (1, 2)", null)]
    [InlineData("UPDATE a JOIN b ON b.id = a.b_id SET a.x = b.x WHERE b.flag = 1", DatabaseKind.MySql, "SELECT COUNT(*) FROM a JOIN b ON b.id = a.b_id WHERE b.flag = 1", null)]
    [InlineData("DELETE FROM log ORDER BY created LIMIT 500", DatabaseKind.MySql, "SELECT COUNT(*) FROM log", 500L)]
    [InlineData("UPDATE t SET a = 1 WHERE b = 'WHERE ; SET' RETURNING id", DatabaseKind.PostgreSql, "SELECT COUNT(*) FROM t WHERE b = 'WHERE ; SET'", null)]
    [InlineData("UPDATE t SET a = s.a FROM s WHERE s.id = t.id", DatabaseKind.PostgreSql, "SELECT COUNT(*) FROM t, s WHERE s.id = t.id", null)]
    [InlineData("DELETE FROM t USING s WHERE s.id = t.id", DatabaseKind.PostgreSql, "SELECT COUNT(*) FROM t, s WHERE s.id = t.id", null)]
    [InlineData("UPDATE t SET t.a = 1 FROM [dbo].[t] t JOIN s ON s.id = t.id WHERE s.ok = 1", DatabaseKind.SqlServer, "SELECT COUNT(*) FROM [dbo].[t] t JOIN s ON s.id = t.id WHERE s.ok = 1", null)]
    [InlineData("DELETE TOP (10) FROM [dbo].[log] WHERE level = N'debug'", DatabaseKind.SqlServer, "SELECT COUNT(*) FROM [dbo].[log] WHERE level = N'debug'", 10L)]
    [InlineData("-- pulizia\nDELETE /* vecchi */ FROM sessions WHERE expires < now()", DatabaseKind.PostgreSql, "SELECT COUNT(*) FROM sessions WHERE expires < now()", null)]
    [InlineData("UPDATE OR IGNORE t SET a = 1 WHERE b = 2", DatabaseKind.Sqlite, "SELECT COUNT(*) FROM t WHERE b = 2", null)]
    public void ConvertiInConteggio(string statement, DatabaseKind kind, string expected, long? limit)
    {
        var result = DmlPreview.Build(statement, kind);
        Assert.NotNull(result);
        Assert.Equal(expected, result.CountSql);
        Assert.Equal(limit, result.Limit);
    }

    [Theory]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t WHERE id IN (SELECT * FROM x)")]
    [InlineData("DELETE FROM t OUTPUT deleted.id WHERE a = 1")]
    [InlineData("SELECT * FROM t")]
    [InlineData("INSERT INTO t VALUES (1)")]
    public void FormeNonSupportate(string statement) => Assert.Null(DmlPreview.Build(statement, DatabaseKind.SqlServer));

    [Fact]
    public void RiconosceUpdateEDelete()
    {
        Assert.True(DmlPreview.IsUpdateOrDelete("  -- x\n update t set a=1"));
        Assert.False(DmlPreview.IsUpdateOrDelete("SELECT 1"));
    }
}
