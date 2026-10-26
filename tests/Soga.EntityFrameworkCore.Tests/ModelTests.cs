using Microsoft.EntityFrameworkCore;
using Soga.EntityFrameworkCore.Entities;
using Xunit;

namespace Soga.EntityFrameworkCore.Tests;

public sealed class ModelTests
{
    [Fact]
    public void AddSoga_RegistersPersistenceModel()
    {
        using var fixture = new SqliteFixture();
        using var context = fixture.CreateContext();

        var tables = context.Model.GetEntityTypes().Select(e => e.GetTableName()!).Order().ToArray();

        Assert.Equal(
            ["SogaChanges", "SogaConversations", "SogaMessages", "SogaOutbox", "SogaParticipants", "SogaTenantState"],
            tables);
    }

    [Fact]
    public void AddSoga_EnforcesIdempotencyAndOrderingWithUniqueIndexes()
    {
        using var fixture = new SqliteFixture();
        using var context = fixture.CreateContext();
        var messages = context.Model.FindEntityType(typeof(MessageRow))!;

        var unique = messages.GetIndexes().Where(i => i.IsUnique)
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name))).ToArray();

        Assert.Contains("TenantId,ConversationId,Sequence", unique);
        Assert.Contains("TenantId,ConversationId,SenderId,ClientMessageId", unique);
    }

    [Fact]
    public void AddSoga_EnforcesDirectConversationUniquenessPerTenant()
    {
        using var fixture = new SqliteFixture();
        using var context = fixture.CreateContext();
        var conversations = context.Model.FindEntityType(typeof(ConversationRow))!;

        Assert.Contains(
            conversations.GetIndexes(),
            i => i.IsUnique && string.Join(",", i.Properties.Select(p => p.Name)) == "TenantId,DirectKey");
    }

    [Fact]
    public void AddSoga_TenantIsPartOfEveryKey()
    {
        using var fixture = new SqliteFixture();
        using var context = fixture.CreateContext();

        foreach (var entity in context.Model.GetEntityTypes())
        {
            Assert.Contains(entity.FindPrimaryKey()!.Properties, p => p.Name == "TenantId");
        }
    }

    [Fact]
    public void AddSoga_DoesNotReferenceHostUserEntities()
    {
        using var fixture = new SqliteFixture();
        using var context = fixture.CreateContext();

        var foreignKeyTargets = context.Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Select(fk => fk.PrincipalEntityType.ClrType)
            .Distinct();

        Assert.All(foreignKeyTargets, target => Assert.Equal(typeof(ConversationRow), target));
    }

    [Fact]
    public void AddSoga_WithSchemaAndPrefix_AppliesBothToEveryTable()
    {
        var builder = new DbContextOptionsBuilder<PrefixedContext>().UseSqlite("DataSource=:memory:");
        using var context = new PrefixedContext(builder.Options);

        Assert.All(context.Model.GetEntityTypes(), e => Assert.StartsWith("Chat", e.GetTableName(), StringComparison.Ordinal));
        Assert.All(context.Model.GetEntityTypes(), e => Assert.Equal("messaging", e.GetSchema()));
    }

    [Fact]
    public void Store_WhenContextOmitsTheSogaModel_FailsWithActionableMessage()
    {
        var options = new DbContextOptionsBuilder<PlainContext>().UseSqlite("DataSource=:memory:").Options;
        using var context = new PlainContext(options);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new Repositories.EfSogaStore<PlainContext>(context));

        Assert.Contains("AddSoga", exception.Message, StringComparison.Ordinal);
    }

    private sealed class PrefixedContext(DbContextOptions<PrefixedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            Extensions.SogaModelBuilderExtensions.AddSoga(modelBuilder, "messaging", "Chat");
    }

    private sealed class PlainContext(DbContextOptions<PlainContext> options) : DbContext(options);
}
