using Microsoft.EntityFrameworkCore;
using Soga.EntityFrameworkCore.Configuration;
using Soga.EntityFrameworkCore.Entities;
using Soga.EntityFrameworkCore.Extensions;
using Xunit;

namespace Soga.EntityFrameworkCore.Tests;

/// <summary>
/// SQL Server is case-insensitive by default. These tests need no database: they inspect the model and the startup check.
/// </summary>
public sealed class SqlServerSafetyTests
{
    private const string OfflineConnection = "Server=.;Database=NotUsed;Integrated Security=true;TrustServerCertificate=true";

    [Fact]
    public void Startup_OnSqlServerWithoutAnIdentityCollation_IsRefused()
    {
        var options = new DbContextOptionsBuilder<UnsafeSqlServerContext>().UseSqlServer(OfflineConnection).Options;
        using var context = new UnsafeSqlServerContext(options);

        var exception = Assert.Throws<InvalidOperationException>(() => SogaEfModelCheck.Validate(context));

        Assert.Contains("identityCollation", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Alice", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_OnSqlServerWithACaseSensitiveCollation_IsAccepted()
    {
        var options = new DbContextOptionsBuilder<SqlServerTestDbContext>().UseSqlServer(OfflineConnection).Options;
        using var context = new SqlServerTestDbContext(options);

        SogaEfModelCheck.Validate(context);
    }

    [Fact]
    public void Startup_OnPostgreSqlAndSqlite_NeedsNoCollation()
    {
        var sqlite = new DbContextOptionsBuilder<TestDbContext>().UseSqlite("DataSource=:memory:").Options;
        using var sqliteContext = new TestDbContext(sqlite);
        var postgres = new DbContextOptionsBuilder<PostgresTestDbContext>().UseNpgsql("Host=localhost;Database=NotUsed").Options;
        using var postgresContext = new PostgresTestDbContext(postgres);

        SogaEfModelCheck.Validate(sqliteContext);
        SogaEfModelCheck.Validate(postgresContext);
    }

    [Fact]
    public void IdentityColumns_UseAnOrdinalKeyComparerSoEfNeverMergesUsersThatDifferByCase()
    {
        var options = new DbContextOptionsBuilder<SqlServerTestDbContext>().UseSqlServer(OfflineConnection).Options;
        using var context = new SqlServerTestDbContext(options);

        var identityColumns = new (Type Entity, string Property)[]
        {
            (typeof(ParticipantRow), nameof(ParticipantRow.UserId)),
            (typeof(ParticipantRow), nameof(ParticipantRow.TenantId)),
            (typeof(ConversationRow), nameof(ConversationRow.TenantId)),
            (typeof(MessageRow), nameof(MessageRow.SenderId)),
            (typeof(MessageRow), nameof(MessageRow.ClientMessageId)),
            (typeof(ChangeRow), nameof(ChangeRow.TargetUserId)),
            (typeof(OutboxRow), nameof(OutboxRow.TenantId)),
            (typeof(TenantStateRow), nameof(TenantStateRow.TenantId)),
        };

        foreach (var (entity, property) in identityColumns)
        {
            var comparer = context.Model.FindEntityType(entity)!.FindProperty(property)!.GetKeyValueComparer();
            Assert.False(comparer!.Equals("alice", "Alice"), $"{entity.Name}.{property} treats alice and Alice as equal.");
            Assert.True(comparer.Equals("alice", "alice"));
        }
    }

    [Fact]
    public void EveryIdentityColumn_GetsTheCollation()
    {
        var options = new DbContextOptionsBuilder<SqlServerTestDbContext>().UseSqlServer(OfflineConnection).Options;
        using var context = new SqlServerTestDbContext(options);
        var design = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)context).Instance
            .GetService(typeof(Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel)) as Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel;

        var columns = new (Type Entity, string Property)[]
        {
            (typeof(ConversationRow), nameof(ConversationRow.TenantId)),
            (typeof(ConversationRow), nameof(ConversationRow.DirectKey)),
            (typeof(ConversationRow), nameof(ConversationRow.ExternalReference)),
            (typeof(ParticipantRow), nameof(ParticipantRow.UserId)),
            (typeof(MessageRow), nameof(MessageRow.SenderId)),
            (typeof(MessageRow), nameof(MessageRow.ClientMessageId)),
            (typeof(MessageRow), nameof(MessageRow.DeletedBy)),
            (typeof(ChangeRow), nameof(ChangeRow.TargetUserId)),
            (typeof(MessageEditRow), nameof(MessageEditRow.TenantId)),
        };

        foreach (var (entity, property) in columns)
        {
            Assert.Equal(
                TestCollations.SqlServer,
                design!.Model.FindEntityType(entity)!.FindProperty(property)!.GetCollation());
        }
    }

    private sealed class UnsafeSqlServerContext(DbContextOptions<UnsafeSqlServerContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddSoga();
    }
}
