using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soga.EntityFrameworkCore.Extensions;
using Soga.EntityFrameworkCore.Repositories;
using Soga.Persistence;
using Xunit;

namespace Soga.EntityFrameworkCore.Tests;

/// <summary>
/// The case-sensitive collation SQL Server needs so that identities compare ordinally. PostgreSQL and SQLite are
/// case-sensitive already and need none.
/// </summary>
internal static class TestCollations
{
    public const string SqlServer = "Latin1_General_100_BIN2";
}

/// <summary>A host-style DbContext for SQLite: the host owns the context and includes Soga through <c>AddSoga</c>.</summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddSoga();
}

/// <summary>A host-style DbContext for SQL Server. It uses a distinct type so EF never shares a cached model between providers.</summary>
public sealed class SqlServerTestDbContext(DbContextOptions<SqlServerTestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.AddSoga(identityCollation: TestCollations.SqlServer);
}

/// <summary>A host-style DbContext for PostgreSQL.</summary>
public sealed class PostgresTestDbContext(DbContextOptions<PostgresTestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddSoga();
}

/// <summary>
/// Creates stores over an isolated database, one DbContext per call like a real request scope. Each provider supplies
/// its own fixture so the same test suites run against every supported database.
/// </summary>
public abstract class EfStoreFixture : IDisposable
{
    public abstract DbContext CreateContext();

    internal abstract ISogaStore CreateStore();

    public abstract void Dispose();
}

/// <summary>An in-memory SQLite database on one shared connection.</summary>
public sealed class SqliteFixture : EfStoreFixture
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<TestDbContext> _options;

    public SqliteFixture()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options;

        using var context = new TestDbContext(_options);
        context.Database.EnsureCreated();
    }

    public override DbContext CreateContext() => new TestDbContext(_options);

    internal override ISogaStore CreateStore() => new EfSogaStore<TestDbContext>(new TestDbContext(_options));

    public override void Dispose() => _connection.Dispose();
}

/// <summary>
/// A throwaway SQL Server database. Set <c>SOGA_TEST_SQLSERVER</c> to a connection string that may create databases, for
/// example <c>Server=localhost;Integrated Security=true;TrustServerCertificate=true</c>. Without it the tests are skipped.
/// </summary>
public sealed class SqlServerFixture : EfStoreFixture
{
    public const string Variable = "SOGA_TEST_SQLSERVER";

    private readonly string _master;
    private readonly string _database = $"SogaTest_{Guid.NewGuid():N}";
    private readonly DbContextOptions<SqlServerTestDbContext> _options;

    public SqlServerFixture()
    {
        var master = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(master))
        {
            throw new SkipException($"Set {Variable} to a SQL Server connection string to run the SQL Server tests.");
        }

        _master = master;
        var connection = new SqlConnectionStringBuilder(master) { InitialCatalog = _database }.ConnectionString;
        _options = new DbContextOptionsBuilder<SqlServerTestDbContext>().UseSqlServer(connection).Options;

        using var context = new SqlServerTestDbContext(_options);
        context.Database.EnsureCreated();
    }

    public override DbContext CreateContext() => new SqlServerTestDbContext(_options);

    internal override ISogaStore CreateStore() => new EfSogaStore<SqlServerTestDbContext>(new SqlServerTestDbContext(_options));

    public override void Dispose()
    {
        SqlConnection.ClearAllPools();
        using var connection = new SqlConnection(_master);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// A throwaway PostgreSQL database. Set <c>SOGA_TEST_POSTGRES</c> to a connection string that may create databases, for
/// example <c>Host=localhost;Username=postgres;Password=...</c>. Without it the tests are skipped.
/// </summary>
public sealed class PostgresFixture : EfStoreFixture
{
    public const string Variable = "SOGA_TEST_POSTGRES";

    private readonly DbContextOptions<PostgresTestDbContext> _options;

    public PostgresFixture()
    {
        var master = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(master))
        {
            throw new SkipException($"Set {Variable} to a PostgreSQL connection string to run the PostgreSQL tests.");
        }

        var connection = new NpgsqlConnectionStringBuilder(master) { Database = $"sogatest_{Guid.NewGuid():N}" }.ConnectionString;
        _options = new DbContextOptionsBuilder<PostgresTestDbContext>().UseNpgsql(connection).Options;

        using var context = new PostgresTestDbContext(_options);
        context.Database.EnsureCreated();
    }

    public override DbContext CreateContext() => new PostgresTestDbContext(_options);

    internal override ISogaStore CreateStore() => new EfSogaStore<PostgresTestDbContext>(new PostgresTestDbContext(_options));

    public override void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        using var context = new PostgresTestDbContext(_options);
        context.Database.EnsureDeleted();
    }
}
