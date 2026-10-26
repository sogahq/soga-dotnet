using Microsoft.EntityFrameworkCore;
using Soga.Abstractions;
using Soga.Contracts;
using Soga.EntityFrameworkCore.Entities;
using Soga.Groups;
using Soga.Persistence;
using Soga.Synchronization;
using Xunit;

namespace Soga.EntityFrameworkCore.Tests;

/// <summary>Group, role, edit, delete, reply, and listing behavior on a real relational database.</summary>
public abstract class EfGroupStoreTestsBase(EfStoreFixture fixture) : IDisposable
{
    private const string Tenant = "t1";
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly EfStoreFixture _fixture = fixture;
    private readonly ISogaIdGenerator _ids = new SogaIdGenerator();

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<ConversationRecord> CreateGroupAsync(string owner = "alice", string[]? others = null, string? reference = null, DateTimeOffset? at = null) =>
        await _fixture.CreateStore().CreateGroupAsync(
            new(Tenant, owner, "Team", others ?? ["bob", "carol"], reference, null, Guid.NewGuid(), Guid.NewGuid(), at ?? Now),
            default);

    private Task<GroupMutationOutcome> MutateAsync(Guid groupId, string actor, GroupMutation mutation, bool systemMessage = false, int max = 100, Guid? eventId = null) =>
        _fixture.CreateStore().MutateGroupAsync(
            new(Tenant, actor, groupId, mutation, max, systemMessage, eventId ?? Guid.NewGuid(), _ids, Now.AddMinutes(1)),
            default);

    private Task<AppendMessageOutcome> SendAsync(Guid conversationId, string user, string clientId, string text, Guid? replyTo = null, bool system = false, DateTimeOffset? at = null) =>
        _fixture.CreateStore().AppendMessageAsync(
            new(Tenant, system ? SogaSystemSender.UserId : user, conversationId, clientId, text, Guid.NewGuid(), Guid.NewGuid(), at ?? Now, replyTo, system),
            default);

    private static ParticipantChangedData Participant(string payload) =>
        SogaEventSerializer.ReadData<ParticipantChangedData>(SogaEventSerializer.Deserialize(payload));

    [SkippableFact]
    public async Task CreateGroup_StoresOwnerAndMembersWithRolesAndPublishesOneEvent()
    {
        var group = await CreateGroupAsync(reference: "case:1");

        Assert.Equal((SogaConversationKinds.Group, "Team", 3, "case:1"), (group.Kind, group.Title, group.ParticipantCount, group.ExternalReference));
        Assert.Equal(
            [("alice", SogaRole.Owner), ("bob", SogaRole.Member), ("carol", SogaRole.Member)],
            group.Participants.Select(p => (p.UserId, p.Role)));
        await using var context = _fixture.CreateContext();
        var change = await context.Set<ChangeRow>().SingleAsync();
        Assert.Equal(SogaEventTypes.ConversationCreated, change.ChangeType);
        Assert.Equal(await context.Set<OutboxRow>().Select(o => o.Id).SingleAsync(), change.EventId);
    }

    [SkippableFact]
    public async Task Mutations_FollowTheSameRulesAsTheInMemoryStore()
    {
        var group = await CreateGroupAsync();

        var memberAdds = await MutateAsync(group.Id, "bob", new AddParticipantMutation("dave", SogaRole.Member));
        var outsider = await MutateAsync(group.Id, "zed", new AddParticipantMutation("dave", SogaRole.Member));
        var ownerAdds = await MutateAsync(group.Id, "alice", new AddParticipantMutation("dave", SogaRole.Admin));
        var lastOwnerLeaves = await MutateAsync(group.Id, "alice", new RemoveParticipantMutation("alice"));
        var tooBig = await MutateAsync(group.Id, "alice", new AddParticipantMutation("erin", SogaRole.Member), max: 4);

        Assert.Equal(GroupError.Forbidden, memberAdds.Error);
        Assert.Equal(GroupError.NotFound, outsider.Error);
        Assert.Equal((GroupError.None, true), (ownerAdds.Error, ownerAdds.Changed));
        Assert.Equal(SogaRole.Admin, ownerAdds.Conversation!.Participants.Single(p => p.UserId == "dave").Role);
        Assert.Equal(GroupError.LastOwner, lastOwnerLeaves.Error);
        Assert.Equal(GroupError.TooLarge, tooBig.Error);
    }

    [SkippableFact]
    public async Task Remove_KeepsTheRowMarksItLeftAndReAddingReactivatesItInsteadOfDuplicating()
    {
        var group = await CreateGroupAsync();

        await MutateAsync(group.Id, "alice", new RemoveParticipantMutation("bob"));
        await using (var context = _fixture.CreateContext())
        {
            var row = await context.Set<ParticipantRow>().SingleAsync(p => p.UserId == "bob");
            Assert.NotNull(row.LeftAt);
        }

        var stillGone = await _fixture.CreateStore().GetConversationAsync(Tenant, "bob", group.Id, default);
        var readded = await MutateAsync(group.Id, "alice", new AddParticipantMutation("bob", SogaRole.Member));

        Assert.Null(stillGone);
        Assert.Equal(3, readded.Conversation!.ParticipantCount);
        await using var check = _fixture.CreateContext();
        Assert.Equal(1, await check.Set<ParticipantRow>().CountAsync(p => p.UserId == "bob"));
        Assert.Null((await check.Set<ParticipantRow>().SingleAsync(p => p.UserId == "bob")).LeftAt);
    }

    [SkippableFact]
    public async Task Remove_WritesATargetedChangeSoTheRemovedUserCanStillSyncTheirOwnRemoval()
    {
        var group = await CreateGroupAsync();
        var head = await _fixture.CreateStore().GetChangeHeadAsync(Tenant, default);

        await MutateAsync(group.Id, "alice", new RemoveParticipantMutation("bob"));
        await SendAsync(group.Id, "alice", "c1", "secret after removal");

        var forBob = await _fixture.CreateStore().GetChangesAsync(Tenant, "bob", head, 50, default);
        var forCarol = await _fixture.CreateStore().GetChangesAsync(Tenant, "carol", head, 50, default);

        var only = Assert.Single(forBob.Changes);
        Assert.Equal(new ParticipantChangedData("bob", "removed", null, "alice"), Participant(only.Payload));
        Assert.Equal(2, forCarol.Changes.Count);
        await using var context = _fixture.CreateContext();
        Assert.Equal("bob", (await context.Set<ChangeRow>().SingleAsync(c => c.TargetUserId != null)).TargetUserId);
    }

    [SkippableFact]
    public async Task Mutation_IsAtomic_AFailureAfterTheParticipantChangeRollsEverythingBack()
    {
        var group = await CreateGroupAsync();
        var reused = Guid.NewGuid();
        await MutateAsync(group.Id, "alice", new UpdateConversationMutation(true, "First", false, null), eventId: reused);

        // The event ID is already taken, so the unique change/outbox key fails at the end of the transaction.
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            MutateAsync(group.Id, "alice", new AddParticipantMutation("dave", SogaRole.Member), eventId: reused));

        var after = await _fixture.CreateStore().GetConversationAsync(Tenant, "alice", group.Id, default);
        Assert.Equal(3, after!.ParticipantCount);
        await using var context = _fixture.CreateContext();
        Assert.Equal(0, await context.Set<ParticipantRow>().CountAsync(p => p.UserId == "dave"));
    }

    [SkippableFact]
    public async Task Update_AndArchive_PersistAndAreIdempotent()
    {
        var group = await CreateGroupAsync();

        var renamed = await MutateAsync(group.Id, "alice", new UpdateConversationMutation(true, "Renamed", true, "{\"room\":1}"));
        var same = await MutateAsync(group.Id, "alice", new UpdateConversationMutation(true, "Renamed", true, "{\"room\":1}"));
        var archived = await MutateAsync(group.Id, "alice", new ArchiveMutation(true));
        var restored = await MutateAsync(group.Id, "alice", new ArchiveMutation(false));

        Assert.Equal(("Renamed", "{\"room\":1}", true, false), (renamed.Conversation!.Title, renamed.Conversation.MetadataJson, renamed.Changed, same.Changed));
        Assert.NotNull(archived.Conversation!.ArchivedAt);
        Assert.Null(restored.Conversation!.ArchivedAt);
    }

    [SkippableFact]
    public async Task SystemMessage_IsPostedInTheSameTransactionWhenConfigured()
    {
        var group = await CreateGroupAsync();

        await MutateAsync(group.Id, "alice", new AddParticipantMutation("dave", SogaRole.Member), systemMessage: true);

        var history = await _fixture.CreateStore().GetMessagesAsync(Tenant, "alice", group.Id, null, 10, default);
        var message = Assert.Single(history!);
        Assert.Equal((SogaMessageKinds.System, SogaSystemSender.UserId, 1L), (message.Kind, message.SenderId, message.Sequence));
        Assert.Contains("participant_added", message.Content, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Access_ReportsRoleAndKindWithoutLoadingTheConversation()
    {
        var group = await CreateGroupAsync();
        await MutateAsync(group.Id, "alice", new RemoveParticipantMutation("carol"));

        var owner = await _fixture.CreateStore().GetAccessAsync(Tenant, "alice", group.Id, default);
        var gone = await _fixture.CreateStore().GetAccessAsync(Tenant, "carol", group.Id, default);
        var stranger = await _fixture.CreateStore().GetAccessAsync(Tenant, "zed", group.Id, default);

        Assert.Equal((SogaRole.Owner, true), (owner!.Role, owner.IsGroup));
        Assert.Null(gone);
        Assert.Null(stranger);
    }

    // ---- messages ----

    [SkippableFact]
    public async Task Append_HonoursArchiveReplyAndSystemRules()
    {
        var group = await CreateGroupAsync();
        var other = await CreateGroupAsync();
        var first = (await SendAsync(group.Id, "alice", "c1", "original")).Message!;
        var elsewhere = (await SendAsync(other.Id, "alice", "c1", "other room")).Message!;

        var reply = await SendAsync(group.Id, "bob", "b1", "answer", first.Id);
        var crossed = await SendAsync(group.Id, "bob", "b2", "answer", elsewhere.Id);
        var missing = await SendAsync(group.Id, "bob", "b3", "answer", Guid.NewGuid());
        await MutateAsync(group.Id, "alice", new ArchiveMutation(true));
        var archived = await SendAsync(group.Id, "bob", "b4", "late");
        var systemWhileArchived = await SendAsync(group.Id, "x", "s1", "notice", system: true);

        Assert.Equal(("alice", "original"), (reply.Message!.ReplyTo!.SenderId, reply.Message.ReplyTo.Snippet));
        Assert.Equal(AppendStatus.ReplyInvalid, crossed.Status);
        Assert.Equal(AppendStatus.ReplyInvalid, missing.Status);
        Assert.Equal(AppendStatus.Archived, archived.Status);
        Assert.Equal((AppendStatus.Created, SogaMessageKinds.System), (systemWhileArchived.Status, systemWhileArchived.Message!.Kind));
    }

    [SkippableFact]
    public async Task Append_SystemMessagesRequireAnExistingConversationInTheTenantOnly()
    {
        var group = await CreateGroupAsync();

        var wrongTenant = await _fixture.CreateStore().AppendMessageAsync(
            new("t2", SogaSystemSender.UserId, group.Id, "s1", "x", Guid.NewGuid(), Guid.NewGuid(), Now, null, true),
            default);
        var unknown = await SendAsync(Guid.NewGuid(), "x", "s1", "x", system: true);

        Assert.Equal(AppendStatus.ConversationNotFound, wrongTenant.Status);
        Assert.Equal(AppendStatus.ConversationNotFound, unknown.Status);
    }

    [SkippableFact]
    public async Task Edit_UpdatesInPlaceKeepsSequenceAndHonoursTheRules()
    {
        var group = await CreateGroupAsync();
        var message = (await SendAsync(group.Id, "alice", "c1", "helo")).Message!;
        var store = _fixture.CreateStore();
        EditMessageCommand Edit(string user, string text, DateTimeOffset at, TimeSpan? window = null, bool retain = false) =>
            new(Tenant, user, group.Id, message.Id, text, Guid.NewGuid(), _ids, at, window, retain);

        var updated = await store.EditMessageAsync(Edit("alice", "hello", Now.AddMinutes(1), retain: true), default);
        var unchanged = await store.EditMessageAsync(Edit("alice", "hello", Now.AddMinutes(2)), default);
        var byOther = await store.EditMessageAsync(Edit("bob", "hijack", Now.AddMinutes(2)), default);
        var stranger = await store.EditMessageAsync(Edit("zed", "hijack", Now.AddMinutes(2)), default);
        var late = await store.EditMessageAsync(Edit("alice", "too late", Now.AddHours(1), TimeSpan.FromMinutes(15)), default);

        Assert.Equal((EditStatus.Updated, "hello", 1L, Now.AddMinutes(1)), (updated.Status, updated.Message!.Content, updated.Message.Sequence, updated.Message.EditedAt));
        Assert.Equal(EditStatus.Unchanged, unchanged.Status);
        Assert.Equal(EditStatus.Forbidden, byOther.Status);
        Assert.Equal(EditStatus.ConversationNotFound, stranger.Status);
        Assert.Equal(EditStatus.WindowExpired, late.Status);
        await using var context = _fixture.CreateContext();
        Assert.Equal("helo", (await context.Set<MessageEditRow>().SingleAsync()).PreviousContent);
        Assert.Equal(1, await context.Set<ChangeRow>().CountAsync(c => c.ChangeType == SogaEventTypes.MessageUpdated));
    }

    [SkippableFact]
    public async Task Edit_DoesNotKeepHistoryUnlessAsked()
    {
        var group = await CreateGroupAsync();
        var message = (await SendAsync(group.Id, "alice", "c1", "v1")).Message!;

        await _fixture.CreateStore().EditMessageAsync(new(Tenant, "alice", group.Id, message.Id, "v2", Guid.NewGuid(), _ids, Now, null, false), default);

        await using var context = _fixture.CreateContext();
        Assert.Empty(await context.Set<MessageEditRow>().ToListAsync());
    }

    [SkippableFact]
    public async Task Delete_ErasesTextAndRetainedHistoryKeepsTheTombstoneAndIsIdempotent()
    {
        var group = await CreateGroupAsync();
        var message = (await SendAsync(group.Id, "bob", "b1", "v1")).Message!;
        var store = _fixture.CreateStore();
        await store.EditMessageAsync(new(Tenant, "bob", group.Id, message.Id, "v2", Guid.NewGuid(), _ids, Now, null, true), default);
        DeleteMessageCommand Delete(string user) => new(Tenant, user, group.Id, message.Id, true, true, Guid.NewGuid(), Now.AddMinutes(5));

        var byMember = await store.DeleteMessageAsync(Delete("carol"), default);
        var byOwner = await store.DeleteMessageAsync(Delete("alice"), default);
        var again = await store.DeleteMessageAsync(Delete("bob"), default);

        Assert.Equal(DeleteStatus.Forbidden, byMember.Status);
        Assert.Equal((DeleteStatus.Deleted, true, "bob"), (byOwner.Status, byOwner.ByAdministrator, byOwner.SenderId));
        Assert.Equal(DeleteStatus.AlreadyDeleted, again.Status);
        await using var context = _fixture.CreateContext();
        var row = await context.Set<MessageRow>().SingleAsync();
        Assert.Equal((string.Empty, "alice"), (row.Content, row.DeletedBy));
        Assert.Empty(await context.Set<MessageEditRow>().ToListAsync());
        Assert.Equal(1, await context.Set<ChangeRow>().CountAsync(c => c.ChangeType == SogaEventTypes.MessageDeleted));
    }

    [SkippableFact]
    public async Task Delete_SystemMessagesAndDirectConversationsFollowTheRules()
    {
        var group = await CreateGroupAsync();
        var system = (await SendAsync(group.Id, "x", "s1", "notice", system: true)).Message!;
        var direct = (await _fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new(Tenant, "alice", "bob", null, Guid.NewGuid(), Guid.NewGuid(), Now), default)).Conversation;
        var directMessage = (await SendAsync(direct.Id, "alice", "d1", "mine")).Message!;
        var store = _fixture.CreateStore();

        var systemDelete = await store.DeleteMessageAsync(new(Tenant, "alice", group.Id, system.Id, true, true, Guid.NewGuid(), Now), default);
        var otherInDirect = await store.DeleteMessageAsync(new(Tenant, "bob", direct.Id, directMessage.Id, true, true, Guid.NewGuid(), Now), default);

        Assert.Equal(DeleteStatus.NotDeletable, systemDelete.Status);
        Assert.Equal(DeleteStatus.Forbidden, otherInDirect.Status);
    }

    [SkippableFact]
    public async Task DeletedMessagesLeaveUnreadCountsAndPreviewsShowTombstones()
    {
        var group = await CreateGroupAsync();
        var keep = (await SendAsync(group.Id, "bob", "b1", "keep")).Message!;
        var drop = (await SendAsync(group.Id, "bob", "b2", "drop")).Message!;
        var store = _fixture.CreateStore();
        Assert.Equal(2, await store.GetTotalUnreadAsync(Tenant, "alice", default));

        await store.DeleteMessageAsync(new(Tenant, "bob", group.Id, drop.Id, true, true, Guid.NewGuid(), Now), default);

        Assert.Equal(1, await store.GetTotalUnreadAsync(Tenant, "alice", default));
        var summary = await store.GetConversationAsync(Tenant, "alice", group.Id, default);
        Assert.Equal((1L, true, string.Empty), (summary!.UnreadCount, summary.LastMessage!.Deleted, summary.LastMessage.Snippet));
        var history = await store.GetMessagesAsync(Tenant, "alice", group.Id, null, 10, default);
        Assert.Equal([2L, 1L], history!.Select(m => m.Sequence));
        Assert.Equal(keep.Id, history![1].Id);
    }

    [SkippableFact]
    public async Task Replies_ShowDeletedOnceTheOriginalIsDeleted()
    {
        var group = await CreateGroupAsync();
        var original = (await SendAsync(group.Id, "alice", "c1", "secret")).Message!;
        await SendAsync(group.Id, "bob", "b1", "re", original.Id);
        await _fixture.CreateStore().DeleteMessageAsync(new(Tenant, "alice", group.Id, original.Id, true, true, Guid.NewGuid(), Now), default);

        var history = await _fixture.CreateStore().GetMessagesAsync(Tenant, "bob", group.Id, null, 10, default);

        var reply = history!.Single(m => m.ReplyToMessageId is not null);
        Assert.Equal((true, string.Empty), (reply.ReplyTo!.Deleted, reply.ReplyTo.Snippet));
    }

    // ---- listing ----

    [SkippableFact]
    public async Task List_FiltersAndCountsWithoutLoadingGroupParticipants()
    {
        var active = await CreateGroupAsync(reference: "case:1");
        var archived = await CreateGroupAsync();
        await MutateAsync(archived.Id, "alice", new ArchiveMutation(true));
        var direct = (await _fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new(Tenant, "alice", "dave", "order:9", Guid.NewGuid(), Guid.NewGuid(), Now), default)).Conversation;
        ListConversationsQuery Query(string? kind = null, ConversationStateFilter state = ConversationStateFilter.Active, string? reference = null, int limit = 10) =>
            new(Tenant, "alice", limit, kind, state, reference, null, 0);

        var all = await _fixture.CreateStore().ListConversationsAsync(Query(), default);
        var groups = await _fixture.CreateStore().ListConversationsAsync(Query(SogaConversationKinds.Group, ConversationStateFilter.All), default);
        var archivedOnly = await _fixture.CreateStore().ListConversationsAsync(Query(state: ConversationStateFilter.Archived), default);
        var byReference = await _fixture.CreateStore().ListConversationsAsync(Query(reference: "order:9"), default);

        Assert.Equal(2, all.Items.Count);
        Assert.Equal(2, groups.Items.Count);
        Assert.Equal([archived.Id], archivedOnly.Items.Select(c => c.Id));
        Assert.Equal([direct.Id], byReference.Items.Select(c => c.Id));
        var listedGroup = all.Items.Single(c => c.Id == active.Id);
        Assert.Equal((3, 0), (listedGroup.ParticipantCount, listedGroup.Participants.Count));
        Assert.Equal(2, all.Items.Single(c => c.Id == direct.Id).Participants.Count);
    }

    [SkippableFact]
    public async Task List_PagesEveryConversationExactlyOnceEvenWithIdenticalTimestamps()
    {
        var expected = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            expected.Add((await CreateGroupAsync(others: [$"user{i}"])).Id);
        }

        var seen = new List<Guid>();
        DateTimeOffset? after = null;
        var skip = 0;
        while (true)
        {
            var page = await _fixture.CreateStore().ListConversationsAsync(
                new(Tenant, "alice", 3, null, ConversationStateFilter.Active, null, after, skip),
                default);
            seen.AddRange(page.Items.Select(c => c.Id));
            if (!page.HasMore)
            {
                break;
            }

            var last = page.Items[^1].LastActivityAt;
            var tied = page.Items.Count(c => c.LastActivityAt == last);
            skip = after == last && tied == page.Items.Count ? skip + tied : tied;
            after = last;
        }

        Assert.Equal(7, seen.Count);
        Assert.Equal(expected.Order(), seen.Order());
    }

    [SkippableFact]
    public async Task List_OrdersByMostRecentActivityAndSurfacesUnreadAndRole()
    {
        var old = await CreateGroupAsync(at: Now);
        var recent = await CreateGroupAsync(at: Now.AddMinutes(5));
        await SendAsync(old.Id, "bob", "b1", "bump", at: Now.AddMinutes(10));

        var page = await _fixture.CreateStore().ListConversationsAsync(
            new(Tenant, "alice", 10, null, ConversationStateFilter.Active, null, null, 0), default);

        Assert.Equal([old.Id, recent.Id], page.Items.Select(c => c.Id));
        Assert.Equal((1L, SogaRole.Owner), (page.Items[0].UnreadCount, page.Items[0].MyRole));
    }

    [SkippableFact]
    public async Task ActiveConversationIds_ListsOnlyCurrentMemberships()
    {
        var kept = await CreateGroupAsync();
        var left = await CreateGroupAsync();
        await MutateAsync(left.Id, "bob", new RemoveParticipantMutation("bob"));

        var ids = await _fixture.CreateStore().ListActiveConversationIdsAsync(Tenant, "bob", 10, default);

        Assert.Equal([kept.Id], ids);
    }

    [SkippableFact]
    public async Task Metadata_IsStoredOnDirectConversationsAtCreationOnly()
    {
        var first = await _fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new(Tenant, "alice", "bob", null, Guid.NewGuid(), Guid.NewGuid(), Now, "{\"a\":1}"), default);
        var again = await _fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new(Tenant, "bob", "alice", null, Guid.NewGuid(), Guid.NewGuid(), Now, "{\"a\":2}"), default);

        Assert.Equal("{\"a\":1}", again.Conversation.MetadataJson);
        Assert.False(again.Created);
        Assert.Equal(first.Conversation.Id, again.Conversation.Id);
    }
}
