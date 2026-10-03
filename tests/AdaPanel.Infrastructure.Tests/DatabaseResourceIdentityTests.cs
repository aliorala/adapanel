using AdaPanel.Core.Common;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DatabaseResourceIdentityTests
{
    [Theory]
    [InlineData(" POSTGRES ", "postgresql")]
    [InlineData("PostgreSQL", "postgresql")]
    [InlineData(" SQLSERVER ", "mssql")]
    [InlineData("MySQL", "mysql")]
    [InlineData("mariadb", "mariadb")]
    [InlineData("pgsql", "pgsql")]
    [InlineData(null, "")]
    public void EngineNormalizationUsesOnlyExistingAliases(string? input, string expected) =>
        Assert.Equal(expected, DatabaseResourceIdentity.NormalizeEngine(input));

    [Theory]
    [InlineData("postgres", " Foo ", "postgresql|Foo")]
    [InlineData("postgresql", "foo", "postgresql|foo")]
    [InlineData("mysql", " Foo ", "mysql|foo")]
    [InlineData("mariadb", " Foo ", "mariadb|foo")]
    [InlineData("sqlserver", " Foo ", "mssql|foo")]
    public void KeyKeepsPostgresQuotedCase(string engine, string name, string expected) =>
        Assert.Equal(expected, DatabaseResourceIdentity.Key(engine, name));

    [Theory]
    [InlineData("postgres", "Foo", "postgresql", "Foo", true)]
    [InlineData("postgresql", "Foo", "postgresql", "foo", false)]
    [InlineData("postgresql", "Foo", "mysql", "Foo", false)]
    [InlineData("pgsql", "foo", "postgresql", "foo", false)]
    [InlineData("mysql", "Foo", "mysql", "foo", true)]
    [InlineData("mariadb", "Foo", "mariadb", "foo", true)]
    [InlineData("sqlserver", "Foo", "mssql", "foo", true)]
    public void EqualityMatchesEngineSemantics(string engine1, string name1, string engine2, string name2, bool expected) =>
        Assert.Equal(expected, DatabaseResourceIdentity.Same(engine1, name1, engine2, name2));
}
