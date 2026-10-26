using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Soga.EntityFrameworkCore.Extensions;
using Soga.EntityFrameworkCore.Repositories;

namespace Soga.EntityFrameworkCore.Tests;

/// <summary>A host-style DbContext: the host owns the context and includes Soga through <c>AddSoga</c>.</summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddSoga();
}

/// <summary>Creates stores over one shared in-memory SQLite connection, one DbContext per call like a real request scope.</summary>
internal sealed class SqliteFixture : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<TestDbContext> _options;

    public SqliteFixture()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public TestDbContext CreateContext() => new(_options);

    public EfSogaStore<TestDbContext> CreateStore() => new(CreateContext());

    public void Dispose() => _connection.Dispose();
}
