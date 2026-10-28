using System.Text;
using AdaPanel.Infrastructure.Database;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MySqlDefinerRewriterTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "adapanel-definer-" + Guid.NewGuid().ToString("N"));
    public MySqlDefinerRewriterTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    private async Task<(string Output, long Count)> Rewrite(string sql, bool bom = false, string? initialSqlMode = null)
    {
        var source = Path.Combine(folder, Guid.NewGuid() + ".sql");
        var target = source + ".converted";
        await File.WriteAllTextAsync(source, sql, new UTF8Encoding(bom));
        var original = await File.ReadAllBytesAsync(source);
        var count = await MySqlDefinerRewriter.RewriteAsync(source, target, default, initialSqlMode);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        var transformed = await File.ReadAllBytesAsync(target);
        Assert.Equal(bom, transformed.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        return (await File.ReadAllTextAsync(target), count);
    }

    [Theory]
    [InlineData("VIEW `v` SQL SECURITY DEFINER AS SELECT 1")]
    [InlineData("TRIGGER `t` BEFORE INSERT ON `items` FOR EACH ROW SET NEW.a = 1")]
    [InlineData("PROCEDURE `p`() SQL SECURITY INVOKER SELECT 1")]
    [InlineData("FUNCTION `f`() RETURNS INT SQL SECURITY DEFINER RETURN 1")]
    [InlineData("EVENT `e` ON SCHEDULE EVERY 1 DAY DO SELECT 1")]
    public async Task ChangesOnlyTheExplicitOwnerForEverySupportedObject(string definition)
    {
        var result = await Rewrite("CREATE DEFINER=`rsmc_alarmm`@`%` " + definition + ";");
        Assert.Equal("CREATE DEFINER=CURRENT_USER " + definition + ";", result.Output);
        Assert.Equal(1, result.Count);
    }

    [Theory]
    [InlineData("/*!50001 CREATE ALGORITHM=UNDEFINED */\r\n/*!50013 DEFINER=`rsmc_alarmm`@`%` SQL SECURITY DEFINER */\r\n/*!50001 VIEW `v_incidents_full` AS SELECT 1 */;", "/*!50001 CREATE ALGORITHM=UNDEFINED */\r\n/*!50013 DEFINER=CURRENT_USER SQL SECURITY DEFINER */\r\n/*!50001 VIEW `v_incidents_full` AS SELECT 1 */;")]
    [InlineData("/*M!100100 CREATE OR REPLACE DEFINER='alice'@'remote' SQL SECURITY INVOKER VIEW v AS SELECT 1 */;", "/*M!100100 CREATE OR REPLACE DEFINER=CURRENT_USER SQL SECURITY INVOKER VIEW v AS SELECT 1 */;")]
    [InlineData("create\tdefiner = alice@localhost view v as select 1;", "create\tdefiner = CURRENT_USER view v as select 1;")]
    [InlineData("CREATE DEFINER='o\\'neil'@'some-host' VIEW v AS SELECT 1;", "CREATE DEFINER=CURRENT_USER VIEW v AS SELECT 1;")]
    [InlineData("CREATE DEFINER=`o``neil`@`localhost` VIEW v AS SELECT 1;", "CREATE DEFINER=CURRENT_USER VIEW v AS SELECT 1;")]
    [InlineData("CREATE DEFINER=`alice\\`@`localhost` VIEW `v\\` AS SELECT 1;", "CREATE DEFINER=CURRENT_USER VIEW `v\\` AS SELECT 1;")]
    [InlineData("CREATE DEFINER=alice /* account */ @ /* host */ localhost VIEW v AS SELECT 1;", "CREATE DEFINER=CURRENT_USER /* account */  /* host */  VIEW v AS SELECT 1;")]
    public async Task PreservesVersionWrappersFormattingAndAccountEscapes(string source, string expected)
    {
        var result = await Rewrite(source, true);
        Assert.Equal(expected, result.Output);
        Assert.Equal(1, result.Count);
    }

    [Theory]
    [InlineData("CURRENT_USER")]
    [InlineData("CURRENT_USER()")]
    [InlineData("current_user ( )")]
    public async Task ExistingCurrentUserIsUnchanged(string owner)
    {
        var sql = "CREATE DEFINER=" + owner + " SQL SECURITY DEFINER VIEW v AS SELECT 1;";
        var result = await Rewrite(sql);
        Assert.Equal(sql, result.Output);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task NeverRewritesDataOrdinaryCommentsQuotedIdentifiersOrRoutineBodies()
    {
        const string sql = "-- CREATE DEFINER=`x`@`%` VIEW fake AS SELECT 1;\r\n" +
            "# CREATE DEFINER=alice@remote VIEW fake AS SELECT 1;\r\n" +
            "/* CREATE DEFINER=alice@remote VIEW fake AS SELECT 1; */\r\n" +
            "INSERT INTO t VALUES ('CREATE DEFINER=`x`@`%` VIEW fake', 'SQL SECURITY DEFINER'), ('it\\'s; CREATE DEFINER=x@y VIEW v');\r\n" +
            "CREATE TABLE `DEFINER` (`DEFINER` varchar(255) DEFAULT 'DEFINER=x@y');\r\n" +
            "DELIMITER $$\r\nCREATE DEFINER=x@y PROCEDURE p() BEGIN SELECT 'CREATE DEFINER=x@y VIEW v'; SELECT 1; END$$\r\nDELIMITER ;\r\n" +
            "CREATE VIEW v AS SELECT 'DEFINER=x@y', `DEFINER`;";
        var result = await Rewrite(sql);
        Assert.Equal(sql.Replace("CREATE DEFINER=x@y PROCEDURE", "CREATE DEFINER=CURRENT_USER PROCEDURE", StringComparison.Ordinal), result.Output);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task StreamsLargeInsertAndFindsTheFollowingViewAcrossBufferBoundaries()
    {
        var data = new string('a', 2_000_000) + " DEFINER=`fake`@`%` " + new string('b', 65536);
        var sql = "INSERT INTO t VALUES ('" + data + "');\nCREATE DEFINER=`actual`@`%` VIEW v AS SELECT 1;";
        var result = await Rewrite(sql);
        Assert.Equal(sql.Replace("DEFINER=`actual`@`%`", "DEFINER=CURRENT_USER", StringComparison.Ordinal), result.Output);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task MultipleDelimiterStylesAndExecutableRoutineCommentsAreHandled()
    {
        const string sql = "DELIMITER ;;\n/*!50003 CREATE*/ /*!50017 DEFINER=`x`@`%`*/ /*!50003 TRIGGER t BEFORE INSERT ON a FOR EACH ROW BEGIN SET NEW.x='DEFINER=z@k'; END */;;\nDELIMITER //\nCREATE DEFINER=x@y FUNCTION f() RETURNS INT RETURN 1//\nDELIMITER ;\nCREATE DEFINER=x@y VIEW v AS SELECT 1;";
        var result = await Rewrite(sql);
        Assert.Equal(sql.Replace("DEFINER=`x`@`%`", "DEFINER=CURRENT_USER", StringComparison.Ordinal).Replace("DEFINER=x@y", "DEFINER=CURRENT_USER", StringComparison.Ordinal), result.Output);
        Assert.Equal(3, result.Count);
    }

    [Theory]
    [InlineData("CREATE DEFINER=;")]
    [InlineData("CREATE DEFINER=x@;")]
    [InlineData("CREATE DEFINER=x@")]
    [InlineData("CREATE DEFINER x@y VIEW v AS SELECT 1;")]
    [InlineData("CREATE DEFINER=x@y TABLE t(a int);")]
    [InlineData("CREATE DEFINER=x@y NONSENSE;")]
    [InlineData("CREATE DEFINER=x@y VIEW v AS SELECT '\0';")]
    [InlineData("INSERT INTO t VALUES ('unterminated")]
    [InlineData("/* unterminated")]
    [InlineData("/*!50013 CREATE DEFINER=x@y VIEW v AS SELECT 1")]
    [InlineData("SET sql_mode='NO_BACKSLASH_ESCAPES'; CREATE DEFINER=x@y VIEW v AS SELECT 1;")]
    [InlineData("SET `sql_mode` = 'STRICT_TRANS_TABLES,NO_BACKSLASH_ESCAPES';")]
    [InlineData("SET sql_mode='ANSI_QUOTES'; CREATE DEFINER=x@y VIEW \"v\\\" AS SELECT 1;")]
    [InlineData("SET sql_mode='ANSI'; CREATE DEFINER=x@y VIEW v AS SELECT 1;")]
    [InlineData("SET sql_mode='DB2'; CREATE DEFINER=x@y VIEW v AS SELECT 1;")]
    [InlineData("SET sql_mode='MAXDB'; CREATE DEFINER=x@y VIEW v AS SELECT 1;")]
    [InlineData("SET sql_mode='MSSQL'; CREATE DEFINER=x@y VIEW v AS SELECT 1;")]
    [InlineData("SET sql_mode='ORACLE'; CREATE DEFINER=x@y VIEW v AS SELECT 1;")]
    [InlineData("SET sql_mode='POSTGRESQL'; CREATE DEFINER=x@y VIEW v AS SELECT 1;")]
    [InlineData("SET sql_mode='STRICT_TRANS_TABLES, ansi_quotes'; CREATE DEFINER=x@y VIEW v AS SELECT 1;")]
    [InlineData("SET sql_mode='ANSI\\_QUOTES'; CREATE DEFINER=x@y VIEW v AS SELECT 1;")]
    [InlineData("SET sql_mode=CONCAT('NO_BACKSLASH_', 'ESCAPES');")]
    [InlineData("SET sql_mode=0x4e4f5f4241434b534c4153485f45534341504553;")]
    [InlineData("SET sql_mode=CHAR(78,79); ")]
    [InlineData("SET sql_mode:=CHAR(78,79);")]
    [InlineData("SET sql_mode='STRICT_TRANS_TABLES' + ',NO_BACKSLASH_ESCAPES';")]
    [InlineData("SET sql_mode=@OLD_SQL_MODE; CREATE DEFINER=x@y VIEW v AS SELECT 1;")]
    [InlineData("DELIMITER\nCREATE DEFINER=x@y VIEW v AS SELECT 1;")]
    [InlineData("DELIMITER #\nCREATE DEFINER=x@y VIEW v AS SELECT 1#")]
    public async Task MalformedOrUnsupportedInputFailsClosed(string sql)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Rewrite(sql));
    }

    [Fact]
    public async Task RefusesToOverwriteSourceOrAnExistingOutput()
    {
        var source = Path.Combine(folder, "source.sql");
        await File.WriteAllTextAsync(source, "SELECT 1;");
        Assert.Throws<ArgumentException>(() => { _ = MySqlDefinerRewriter.RewriteAsync(source, source); });
        var target = Path.Combine(folder, "existing.sql");
        await File.WriteAllTextAsync(target, "keep");
        await Assert.ThrowsAsync<IOException>(() => MySqlDefinerRewriter.RewriteAsync(source, target));
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task HonorsCancellationBeforeOpeningFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MySqlDefinerRewriter.RewriteAsync(Path.Combine(folder, "absent"), Path.Combine(folder, "output"), cancellation.Token));
    }

    [Fact]
    public async Task StandardMysqldumpSqlModesAndFinalRestorationAreSupported()
    {
        const string sql = "/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;\n" +
            "CREATE DEFINER=`x`@`%` VIEW v AS SELECT 1;\n/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;\n/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;";
        var result = await Rewrite(sql);
        Assert.Equal(sql.Replace("DEFINER=`x`@`%`", "DEFINER=CURRENT_USER", StringComparison.Ordinal), result.Output);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task MysqldumpCanRestoreAndThenReestablishKnownModesBetweenRoutines()
    {
        const string sql = "DELIMITER ;;\n" +
            "/*!50003 SET @saved_sql_mode = @@sql_mode */ ;;\n" +
            "/*!50003 SET sql_mode = 'STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION' */ ;;\n" +
            "/*!50003 CREATE*/ /*!50020 DEFINER=`first_owner`@`%`*/ /*!50003 PROCEDURE first_routine() BEGIN SELECT 'DEFINER=fake@fake'; END */ ;;\n" +
            "/*!50003 SET sql_mode = @saved_sql_mode */ ;;\n" +
            "/*!50003 SET @saved_sql_mode = @@sql_mode */ ;;\n" +
            "/*!50003 SET sql_mode = 'NO_AUTO_VALUE_ON_ZERO' */ ;;\n" +
            "/*!50003 CREATE*/ /*!50020 DEFINER=`second_owner`@`localhost`*/ /*!50003 PROCEDURE second_routine() BEGIN SELECT 2; END */ ;;\n" +
            "/*!50003 SET sql_mode = @saved_sql_mode */ ;;\n" +
            "DELIMITER ;\n";
        var result = await Rewrite(sql);
        Assert.Equal(sql.Replace("DEFINER=`first_owner`@`%`", "DEFINER=CURRENT_USER", StringComparison.Ordinal)
            .Replace("DEFINER=`second_owner`@`localhost`", "DEFINER=CURRENT_USER", StringComparison.Ordinal), result.Output);
        Assert.Equal(2, result.Count);
    }

    [Theory]
    [InlineData("@@sql_mode")]
    [InlineData("@@SESSION.sql_mode")]
    [InlineData("@@LOCAL.sql_mode")]
    public async Task RestoringAKnownSavedModeAllowsTheFollowingView(string modeReference)
    {
        var sql = "SET sql_mode='NO_AUTO_VALUE_ON_ZERO';\n" +
            "SET @saved_sql_mode=" + modeReference + ";\n" +
            "SET sql_mode='STRICT_TRANS_TABLES';\n" +
            "DELIMITER ;;\nCREATE DEFINER=x@y PROCEDURE p() BEGIN SELECT 1; END;;\nDELIMITER ;\n" +
            "SET sql_mode=@saved_sql_mode;\nCREATE DEFINER=x@y VIEW v AS SELECT 1;";
        var result = await Rewrite(sql);
        Assert.Equal(sql.Replace("DEFINER=x@y", "DEFINER=CURRENT_USER", StringComparison.Ordinal), result.Output);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task InitialUnknownModeDoesNotBecomeKnownJustBecauseItWasSaved()
    {
        const string sql = "SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO';\n" +
            "CREATE DEFINER=x@y VIEW first_view AS SELECT 1;\n" +
            "SET SQL_MODE=@OLD_SQL_MODE;\nCREATE DEFINER=x@y VIEW second_view AS SELECT 2;";
        await Assert.ThrowsAsync<InvalidDataException>(() => Rewrite(sql));
    }

    [Fact]
    public async Task OverwritingASavedVariableInvalidatesTheKnownMode()
    {
        const string sql = "SET sql_mode='NO_AUTO_VALUE_ON_ZERO'; SET @saved_sql_mode=@@sql_mode; " +
            "SET @saved_sql_mode='untracked'; SET sql_mode=@saved_sql_mode; CREATE DEFINER=x@y VIEW v AS SELECT 1;";
        await Assert.ThrowsAsync<InvalidDataException>(() => Rewrite(sql));
    }

    [Theory]
    [InlineData(" || ',ANSI_QUOTES'")]
    [InlineData(" || @suffix")]
    [InlineData(" + @suffix")]
    public async Task ModeSaveExpressionsAreNeverMistakenForExactKnownModeCopies(string expression)
    {
        var sql = "SET sql_mode='NO_AUTO_VALUE_ON_ZERO'; SET @saved_sql_mode=@@sql_mode" + expression + "; " +
            "SET sql_mode=@saved_sql_mode; CREATE DEFINER=x@y VIEW v AS SELECT 1;";
        await Assert.ThrowsAsync<InvalidDataException>(() => Rewrite(sql));
    }

    [Theory]
    [InlineData("CREATE DEFINER=x@y VIEW `db`.`v` AS SELECT t.a FROM t;", "CREATE VIEW `db`.`v`")]
    [InlineData("CREATE DEFINER=x@y VIEW v AS SELECT t.a FROM t;", "CREATE VIEW v")]
    public async Task DiagnosticsPreserveQualifiedObjectNamesWithoutAbsorbingBodyExpressions(string sql, string expected)
    {
        var source = Path.Combine(folder, "qualified-diagnostics.sql");
        await File.WriteAllTextAsync(source, sql);
        Assert.Equal(expected, await MySqlDefinerRewriter.FindObjectAtLineAsync(source, 1));
    }

    [Theory]
    [InlineData("/*!80000 SET SQL_MODE='' */;")]
    [InlineData("/*!50700 SET SQL_MODE='' */;")]
    [InlineData("/*M!100100 SET SQL_MODE='' */;")]
    [InlineData("SET SQL_MODE=/*!80000 '' */;")]
    [InlineData("/*!80000 SET @saved=@@SQL_MODE */;")]
    public async Task ConditionalModeDirectivesCannotFalselyReestablishAKnownMode(string directive)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Rewrite("SET SQL_MODE=@unknown; " + directive + " CREATE DEFINER=x@y VIEW v AS SELECT 1;"));
    }

    [Theory]
    [InlineData("NO_BACKSLASH_ESCAPES")]
    [InlineData("ANSI_QUOTES")]
    [InlineData("ANSI")]
    [InlineData("DB2")]
    [InlineData("MAXDB")]
    [InlineData("MSSQL")]
    [InlineData("ORACLE")]
    [InlineData("POSTGRESQL")]
    public void UnsupportedInitialTargetModeIsRejectedBeforeReadingAnyFile(string mode)
    {
        Assert.Throws<InvalidDataException>(() => MySqlDefinerRewriter.EnsureSupportedSqlMode(mode));
        Assert.Throws<InvalidDataException>(() => { _ = MySqlDefinerRewriter.RewriteAsync("absent.sql", "absent-output.sql", default, mode); });
    }

    [Fact]
    public async Task ValidatedInitialTargetModeCanBeSavedAndRestoredBeforeFollowingDdl()
    {
        const string sql = "/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;\n" +
            "/*!50003 SET SQL_MODE=@OLD_SQL_MODE */; CREATE DEFINER=x@y VIEW v AS SELECT 1;";
        var result = await Rewrite(sql, initialSqlMode: "STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION");
        Assert.Equal(sql.Replace("DEFINER=x@y", "DEFINER=CURRENT_USER", StringComparison.Ordinal), result.Output);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task PreservesLineNumbersIncludingUnusualNewlinesInAccounts()
    {
        const string sql = "CREATE DEFINER=`ali\r\nce`@`local\nhost` VIEW v AS SELECT 1;\n";
        var result = await Rewrite(sql);
        Assert.Equal("CREATE DEFINER=CURRENT_USER\r\n\n VIEW v AS SELECT 1;\n", result.Output);
        Assert.Equal(sql.Count(c => c == '\n'), result.Output.Count(c => c == '\n'));
    }

    [Fact]
    public async Task OversizedAccountsFailInsteadOfUsingTheStreamingDataFastPath()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Rewrite("CREATE DEFINER=`" + new string('x', 70000) + "`@localhost VIEW v AS SELECT 1;"));
    }

    [Fact]
    public async Task ExplicitAccountMatchingTheTargetIsStillPortableAndSecurityIsPreserved()
    {
        var result = await Rewrite("CREATE DEFINER=`rsmc_alarmm`@`localhost` SQL SECURITY INVOKER VIEW v AS SELECT 1;");
        Assert.Equal("CREATE DEFINER=CURRENT_USER SQL SECURITY INVOKER VIEW v AS SELECT 1;", result.Output);
        Assert.Equal(1, result.Count);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task MariaDbSandboxHeaderDoesNotHideTheFirstCreateStatement(string newline)
    {
        var sql = "/*M!999999\\- enable the sandbox mode */" + newline +
            "CREATE DEFINER=`source`@`%` SQL SECURITY DEFINER VIEW v AS SELECT 1;" + newline;
        var result = await Rewrite(sql, true);
        Assert.Equal(sql.Replace("DEFINER=`source`@`%`", "DEFINER=CURRENT_USER", StringComparison.Ordinal), result.Output);
        Assert.Equal(1, result.Count);

        var source = Path.Combine(folder, "sandbox-diagnostics.sql");
        await File.WriteAllTextAsync(source, sql);
        Assert.Equal("CREATE VIEW v", await MySqlDefinerRewriter.FindObjectAtLineAsync(source, 2));
    }

    [Theory]
    [InlineData("/*M!999999\\- enable the sandbox mode BROKEN */")]
    [InlineData("/*M!999999\\- enable the sandbox mode ")]
    [InlineData("/*M!999999 \\- enable the sandbox mode */")]
    [InlineData("/*!999999\\- enable the sandbox mode */")]
    public async Task MalformedSandboxLikeDirectivesFailClosed(string header)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Rewrite(header + "\nCREATE DEFINER=x@y VIEW v AS SELECT 1;"));
    }

    [Theory]
    [InlineData(1, "CREATE VIEW `v`")]
    [InlineData(2, "CREATE VIEW `v`")]
    [InlineData(3, "INSERT INTO `data`")]
    [InlineData(4, "CREATE PROCEDURE p")]
    public async Task FindsTheFailedStatementObjectWithoutMaterializingStatements(long line, string expected)
    {
        var source = Path.Combine(folder, "diagnostics.sql");
        await File.WriteAllTextAsync(source, "/*!50001 CREATE ALGORITHM=UNDEFINED */\n/*!50013 DEFINER=`x`@`%` SQL SECURITY DEFINER */ /*!50001 VIEW `v` AS SELECT 1 */;\nINSERT INTO `data` VALUES ('CREATE DEFINER=fake@fake');\nDELIMITER $$\nCREATE DEFINER=x@y PROCEDURE p() BEGIN SELECT 1; END$$\nDELIMITER ;\n");
        // Error line for a stored routine refers to its CREATE, after the client-only DELIMITER.
        if (line == 4) line = 5;
        Assert.Equal(expected, await MySqlDefinerRewriter.FindObjectAtLineAsync(source, line));
    }
}
