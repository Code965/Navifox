using MosaicDB.Core;
using MosaicDB.Core.Models;
using Xunit;

namespace MosaicDB.Tests;

public sealed class SchemaObjectSqlTests
{
    [Theory]
    [InlineData("CREATE DEFINER=`root`@`localhost` FUNCTION `iva`(x INT) RETURNS INT RETURN x", "iva")]
    [InlineData("CREATE OR REPLACE FUNCTION public.calcola(a integer) RETURNS integer AS $$ SELECT a $$ LANGUAGE sql", "calcola")]
    [InlineData("CREATE TRIGGER tr_x BEFORE INSERT ON t FOR EACH ROW SET NEW.a = 1", "tr_x")]
    [InlineData("CREATE EVENT IF NOT EXISTS `pulizia` ON SCHEDULE EVERY 1 DAY DO SELECT 1", "pulizia")]
    public void DefinedNameReadsTheObjectName(string definition, string expected) =>
        Assert.Equal(expected, SchemaObjectSql.DefinedName(definition));

    [Fact]
    public void ExtractsSelectFromMySqlShowCreateView() =>
        Assert.Equal("select `t`.`id` AS `id` from `t`",
            SchemaObjectSql.ExtractViewSelect("CREATE ALGORITHM=UNDEFINED DEFINER=`root`@`localhost` SQL SECURITY DEFINER VIEW `v_t` AS select `t`.`id` AS `id` from `t`"));

    [Fact]
    public void MySqlSaveDropsRecreatesAndKeepsTheOriginalForRestore()
    {
        var original = new DatabaseObjectInfo("f", "FUNCTION");
        var plan = SchemaObjectSql.BuildSave(true, "db", SchemaObjectKind.Routine, original, "CREATE FUNCTION f() RETURNS INT RETURN 1;", "CREATE FUNCTION f() RETURNS INT RETURN 2;");
        Assert.Equal(["DROP FUNCTION IF EXISTS `f`", "CREATE FUNCTION f() RETURNS INT RETURN 2"], plan.Statements);
        Assert.Equal(["CREATE FUNCTION f() RETURNS INT RETURN 1"], plan.Restore);
    }

    [Fact]
    public void PostgresViewFallsBackToAtomicRecreate()
    {
        var plan = SchemaObjectSql.BuildViewSave(false, "public", "v", "v", "SELECT 1");
        Assert.StartsWith("CREATE OR REPLACE VIEW \"public\".\"v\" AS", plan.Statements[0]);
        Assert.True(plan.Fallback!.Atomic);
        Assert.Equal("DROP VIEW \"public\".\"v\"", plan.Fallback.Statements[0]);
    }

    [Fact]
    public void PostgresRoutineDropUsesSignature() =>
        Assert.Equal("DROP FUNCTION IF EXISTS \"public\".\"iva\"(importo numeric)",
            SchemaObjectSql.DropSql(false, "public", SchemaObjectKind.Routine, new DatabaseObjectInfo("iva(importo numeric)", "FUNCTION", Identity: "importo numeric", BaseName: "iva")));

    [Fact]
    public void UnchangedSequenceProducesNoStatements()
    {
        var sequence = new SequenceInfo("s", 1, 1, 1, 100, 1, false, 5, null);
        Assert.Empty(SchemaObjectSql.SequenceSql("public", sequence, sequence));
    }
}
