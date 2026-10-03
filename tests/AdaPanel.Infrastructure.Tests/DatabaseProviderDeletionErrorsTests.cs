using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Database;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// Exercise the error mapping used by the real provider catch blocks. No server or socket is opened.
public sealed class DatabaseProviderDeletionErrorsTests
{
    [Theory]
    [InlineData(1008)]
    [InlineData(1049)]
    public void MissingMySqlDatabaseConfirmsAbsence(int number) =>
        AssertGone(MySqlDatabaseProvider.MapDeleteDatabaseException(MySqlError(number)), "database.database_not_found");

    [Fact]
    public void MissingMySqlAccountConfirmsAbsence() =>
        AssertGone(MySqlDatabaseProvider.MapDeleteUserException(MySqlError(1396)), "database.user_not_found");

    [Theory]
    [InlineData(1045, "database.connection_failed", 503)]
    [InlineData(1044, "database.configuration_failed", 500)]
    [InlineData(1142, "database.configuration_failed", 500)]
    [InlineData(2003, "database.configuration_failed", 500)]
    [InlineData(1396, "database.configuration_failed", 500)]
    public void MySqlDatabaseFailuresDoNotPretendTheDatabaseIsGone(int number, string key, int status) =>
        AssertFailure(MySqlDatabaseProvider.MapDeleteDatabaseException(MySqlError(number)), key, status);

    [Theory]
    [InlineData(1045, "database.connection_failed", 503)]
    [InlineData(1227, "database.configuration_failed", 500)]
    [InlineData(1008, "database.configuration_failed", 500)]
    [InlineData(1049, "database.configuration_failed", 500)]
    public void MySqlAccountFailuresDoNotPretendTheAccountIsGone(int number, string key, int status) =>
        AssertFailure(MySqlDatabaseProvider.MapDeleteUserException(MySqlError(number)), key, status);

    [Fact]
    public void MissingPostgresDatabaseConfirmsAbsence() =>
        AssertGone(PostgreSqlDatabaseProvider.MapDeleteDatabaseException(PostgresError(PostgresErrorCodes.InvalidCatalogName)), "database.database_not_found");

    [Fact]
    public void MissingPostgresRoleConfirmsAbsence() =>
        AssertGone(PostgreSqlDatabaseProvider.MapDeleteUserException(PostgresError(PostgresErrorCodes.UndefinedObject)), "database.user_not_found");

    [Theory]
    [InlineData("28P01", "database.connection_failed", 503)]
    [InlineData("42501", "database.configuration_failed", 500)]
    [InlineData("55006", "database.configuration_failed", 500)]
    [InlineData("08006", "database.configuration_failed", 500)]
    [InlineData("42704", "database.configuration_failed", 500)]
    public void PostgresDatabaseFailuresDoNotPretendTheDatabaseIsGone(string state, string key, int status) =>
        AssertFailure(PostgreSqlDatabaseProvider.MapDeleteDatabaseException(PostgresError(state)), key, status);

    [Theory]
    [InlineData("28P01", "database.connection_failed", 503)]
    [InlineData("42501", "database.configuration_failed", 500)]
    [InlineData("2BP01", "database.configuration_failed", 500)]
    [InlineData("3D000", "database.configuration_failed", 500)]
    public void PostgresRoleFailuresDoNotPretendTheRoleIsGone(string state, string key, int status) =>
        AssertFailure(PostgreSqlDatabaseProvider.MapDeleteUserException(PostgresError(state)), key, status);

    [Fact]
    public void ConfirmedMissingSqlServerDatabaseConfirmsAbsenceOnlyForDeletion()
    {
        AssertGone(MsSqlDatabaseProvider.MapCommandException(50001, deletingDatabase: true,
            state: 173, message: "AdaPanel.DatabaseDeletion.AbsenceConfirmed"), "database.database_not_found");
        AssertFailure(MsSqlDatabaseProvider.MapCommandException(50001), "database.database_not_found", 404);
    }

    [Theory]
    [InlineData(true, 1, "Database not found.")]
    [InlineData(true, 173, "Fixture DDL trigger denied DROP")]
    [InlineData(true, 172, "AdaPanel.DatabaseDeletion.AbsenceConfirmed")]
    [InlineData(true, 173, "adapanel.DatabaseDeletion.AbsenceConfirmed")]
    [InlineData(true, 173, "AdaPanel.DatabaseDeletion.AbsenceConfirmed\nOther error")]
    [InlineData(false, 173, "AdaPanel.DatabaseDeletion.AbsenceConfirmed")]
    public void AUserDefinedSqlServerErrorCannotMasqueradeAsConfirmedDatabaseAbsence(bool deleting, byte state, string message) =>
        AssertFailure(MsSqlDatabaseProvider.MapCommandException(50001, deleting, state, message), "database.database_not_found", 404);

    [Theory]
    [InlineData(4060, "database.database_not_found", 404)]
    [InlineData(911, "database.database_not_found", 404)]
    [InlineData(229, "database.configuration_failed", 500)]
    [InlineData(18456, "database.configuration_failed", 500)]
    [InlineData(50002, "database.configuration_failed", 500)]
    [InlineData(1801, "database.database_already_exists", 409)]
    [InlineData(15025, "database.user_already_exists", 409)]
    public void SqlServerUncertainMissingAccessAndOtherErrorsRetainFailure(int number, string key, int status)
    {
        AssertFailure(MsSqlDatabaseProvider.MapCommandException(number, deletingDatabase: true), key, status);
        AssertFailure(MsSqlDatabaseProvider.MapCommandException(number), key, status);
    }

    [Fact]
    public void SqlServerDeleteCannotReportInvisibleDatabaseAsConfirmedAbsent()
    {
        var sql = MsSqlDatabaseProvider.BuildDeleteDatabaseSql("fixture_db");
        // DB_ID can return NULL for hidden databases. Only the explicitly checked absence
        // branch may emit 50001; unknown visibility must emit the ordinary failure 50002.
        var missingBranch = sql.IndexOf("IF DB_ID", StringComparison.Ordinal);
        var permissionCheck = sql.IndexOf("COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY DATABASE'), 0) <> 1", StringComparison.Ordinal);
        var unknownFailure = sql.IndexOf("THROW 50002", StringComparison.Ordinal);
        var gone = sql.IndexOf("THROW 50001", StringComparison.Ordinal);
        var drop = sql.IndexOf("DROP DATABASE", StringComparison.Ordinal);
        Assert.True(missingBranch >= 0 && missingBranch < permissionCheck);
        Assert.True(permissionCheck < unknownFailure && unknownFailure < gone && gone < drop);
        Assert.Contains("THROW 50001, 'AdaPanel.DatabaseDeletion.AbsenceConfirmed', 173;", sql, StringComparison.Ordinal);
    }

    private static MySqlException MySqlError(int number) => (MySqlException)typeof(MySqlException)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(MySqlErrorCode), typeof(string)], null)!
        .Invoke([(MySqlErrorCode)number, "Fixture database error"]);

    private static PostgresException PostgresError(string state) => new("Fixture database error", "ERROR", "ERROR", state);

    private static void AssertGone(AppException error, string key)
    {
        Assert.Equal(key, error.MessageKey);
        Assert.True(error.ResourceAlreadyGone);
        Assert.Equal(404, error.HttpStatusCode);
    }

    private static void AssertFailure(AppException error, string key, int status)
    {
        Assert.Equal(key, error.MessageKey);
        Assert.False(error.ResourceAlreadyGone);
        Assert.Equal(status, error.HttpStatusCode);
    }
}
