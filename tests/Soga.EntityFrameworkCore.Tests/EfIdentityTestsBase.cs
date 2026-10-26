using Microsoft.EntityFrameworkCore;
using Soga.Contracts;
using Soga.Groups;
using Soga.Persistence;
using Xunit;

namespace Soga.EntityFrameworkCore.Tests;

/// <summary>
/// Identities are compared ordinally: <c>Alice</c> and <c>alice</c> are different users, and <c>Acme</c> and <c>acme</c>
/// are different tenants, on every database. SQL Server would merge them under its default collation.
/// </summary>
public abstract class EfIdentityTestsBase(EfStoreFixture fixture) : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [SkippableFact]
    public async Task UsersWhoDifferOnlyByCase_AreDifferentUsers()
    {
        var store = fixture.CreateStore();
        var direct = (await store.GetOrCreateDirectConversationAsync(
            new("t1", "alice", "bob", null, Guid.NewGuid(), Guid.NewGuid(), Now), default)).Conversation;

        var asImpostor = await fixture.CreateStore().GetConversationAsync("t1", "Alice", direct.Id, default);
        var access = await fixture.CreateStore().GetAccessAsync("t1", "ALICE", direct.Id, default);
        var send = await fixture.CreateStore().AppendMessageAsync(
            new("t1", "Alice", direct.Id, "c1", "let me in", Guid.NewGuid(), Guid.NewGuid(), Now), default);
        var history = await fixture.CreateStore().GetMessagesAsync("t1", "Alice", direct.Id, null, 10, default);
        var listed = await fixture.CreateStore().ListConversationsAsync(
            new("t1", "Alice", 10, null, ConversationStateFilter.All, null, null, 0), default);

        Assert.Null(asImpostor);
        Assert.Null(access);
        Assert.Equal(AppendStatus.ConversationNotFound, send.Status);
        Assert.Null(history);
        Assert.Empty(listed.Items);
    }

    [SkippableFact]
    public async Task DirectConversations_KeepCaseDifferentUsersApart()
    {
        var lower = (await fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new("t1", "alice", "bob", null, Guid.NewGuid(), Guid.NewGuid(), Now), default)).Conversation;
        var upper = (await fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new("t1", "Alice", "bob", null, Guid.NewGuid(), Guid.NewGuid(), Now), default)).Conversation;

        Assert.NotEqual(lower.Id, upper.Id);
    }

    [SkippableFact]
    public async Task GroupParticipants_ThatDifferByCase_AreSeparateMembers()
    {
        var group = await fixture.CreateStore().CreateGroupAsync(
            new("t1", "alice", "Team", ["Alice", "bob", "BOB"], null, null, Guid.NewGuid(), Guid.NewGuid(), Now), default);

        Assert.Equal(4, group.ParticipantCount);
        await using var context = fixture.CreateContext();
        Assert.Equal(4, await context.Set<Soga.EntityFrameworkCore.Entities.ParticipantRow>().CountAsync());
    }

    [SkippableFact]
    public async Task MessageIdempotencyKeys_AreCaseSensitive()
    {
        var conversation = (await fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new("t1", "alice", "bob", null, Guid.NewGuid(), Guid.NewGuid(), Now), default)).Conversation;

        var lower = await fixture.CreateStore().AppendMessageAsync(
            new("t1", "alice", conversation.Id, "key-a", "first", Guid.NewGuid(), Guid.NewGuid(), Now), default);
        var upper = await fixture.CreateStore().AppendMessageAsync(
            new("t1", "alice", conversation.Id, "KEY-A", "second", Guid.NewGuid(), Guid.NewGuid(), Now), default);

        Assert.Equal((AppendStatus.Created, AppendStatus.Created), (lower.Status, upper.Status));
        Assert.Equal(2, upper.Message!.Sequence);
    }

    [SkippableFact]
    public async Task TenantsThatDifferByCase_AreIsolated()
    {
        var acme = (await fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new("Acme", "alice", "bob", null, Guid.NewGuid(), Guid.NewGuid(), Now), default)).Conversation;

        var asLower = await fixture.CreateStore().GetConversationAsync("acme", "alice", acme.Id, default);
        var head = await fixture.CreateStore().GetChangeHeadAsync("acme", default);

        Assert.Null(asLower);
        Assert.Equal(0, head);
    }

    [SkippableFact]
    public async Task ExternalReferences_AreCaseSensitiveInFilters()
    {
        var upper = (await fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new("t1", "alice", "bob", "Order:1", Guid.NewGuid(), Guid.NewGuid(), Now), default)).Conversation;
        await fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new("t1", "alice", "bob", "order:1", Guid.NewGuid(), Guid.NewGuid(), Now), default);

        var page = await fixture.CreateStore().ListConversationsAsync(
            new("t1", "alice", 10, null, ConversationStateFilter.All, "Order:1", null, 0), default);

        Assert.Equal([upper.Id], page.Items.Select(c => c.Id));
    }

    [SkippableFact]
    public async Task ChangesAddressedToAUser_AreMatchedExactly()
    {
        var group = await fixture.CreateStore().CreateGroupAsync(
            new("t1", "alice", "Team", ["bob"], null, null, Guid.NewGuid(), Guid.NewGuid(), Now), default);
        var head = await fixture.CreateStore().GetChangeHeadAsync("t1", default);
        await fixture.CreateStore().MutateGroupAsync(
            new("t1", "alice", group.Id, new RemoveParticipantMutation("bob"), 100, false, Guid.NewGuid(), new Soga.Abstractions.SogaIdGenerator(), Now), default);

        var forBob = await fixture.CreateStore().GetChangesAsync("t1", "bob", head, 10, default);
        var forShoutingBob = await fixture.CreateStore().GetChangesAsync("t1", "BOB", head, 10, default);

        Assert.Single(forBob.Changes);
        Assert.Empty(forShoutingBob.Changes);
    }
}
