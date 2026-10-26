using Microsoft.Extensions.DependencyInjection;
using Soga.Authorization;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Errors;
using Soga.Extensibility;
using Soga.Groups;
using Soga.Messages;
using Soga.Persistence;
using Soga.Persistence.InMemory;
using Soga.ReadState;
using Soga.Synchronization;

namespace Soga.Tests.Messages;

public sealed class MessageLifecycleTests
{
    private static async Task<(TestSoga Soga, ConversationResponse Group)> ArrangeGroupAsync(
        Action<Soga.Configuration.SogaOptions>? configure = null,
        Action<IServiceCollection>? services = null)
    {
        var soga = TestSoga.Create(configure, services);
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob", "carol");
        return (soga, group);
    }

    // ---- editing ----

    [Fact]
    public async Task Edit_ChangesTextKeepsIdentityAndOrderAndSetsEditedAt()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var original = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "helo");
        await soga.SendAsync(TestSoga.Bob, group.Id, "b1", "next");
        soga.Clock.Advance(TimeSpan.FromMinutes(1));

        var edited = (await soga.Get<MessageWorkflow>().EditAsync(TestSoga.Alice, group.Id, original.Id, new("hello"), default)).Value;

        Assert.Equal(("hello", original.Id, original.Sequence, original.SenderId), (edited.Content, edited.Id, edited.Sequence, edited.SenderId));
        Assert.Equal(original.CreatedAt, edited.CreatedAt);
        Assert.Equal(original.CreatedAt.AddMinutes(1), edited.EditedAt);
    }

    [Fact]
    public async Task Edit_OnlyTheSenderMay_AndNonParticipantsSeeNotFound()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var message = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "mine");
        var workflow = soga.Get<MessageWorkflow>();

        var byMember = await workflow.EditAsync(TestSoga.Bob, group.Id, message.Id, new("hijack"), default);
        var byStranger = await workflow.EditAsync(TestSoga.Dave, group.Id, message.Id, new("hijack"), default);

        Assert.Equal(SogaErrorCodes.AuthorizationDenied, byMember.Error?.Code);
        Assert.Equal(SogaErrorCodes.ConversationNotFound, byStranger.Error?.Code);
    }

    [Fact]
    public async Task Edit_AfterTheWindow_IsRefusedButWithinItIsAllowed()
    {
        var (soga, group) = await ArrangeGroupAsync(o => o.Messages.EditWindow = TimeSpan.FromMinutes(10));
        using var _ = soga;
        var message = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "text");
        var workflow = soga.Get<MessageWorkflow>();
        soga.Clock.Advance(TimeSpan.FromMinutes(9));

        var inside = await workflow.EditAsync(TestSoga.Alice, group.Id, message.Id, new("inside"), default);
        soga.Clock.Advance(TimeSpan.FromMinutes(2));
        var outside = await workflow.EditAsync(TestSoga.Alice, group.Id, message.Id, new("outside"), default);

        Assert.True(inside.IsSuccess);
        Assert.Equal(SogaErrorCodes.MessageEditWindowExpired, outside.Error?.Code);
    }

    [Fact]
    public async Task Edit_WhenEditingIsDisabled_IsRefused()
    {
        var (soga, group) = await ArrangeGroupAsync(o => o.Messages.AllowEditing = false);
        using var _ = soga;
        var message = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "text");

        var result = await soga.Get<MessageWorkflow>().EditAsync(TestSoga.Alice, group.Id, message.Id, new("edit"), default);

        Assert.Equal(SogaErrorCodes.MessageNotEditable, result.Error?.Code);
    }

    [Fact]
    public async Task Edit_WithTheSameTextDoesNothing_AndInvalidTextIsRejected()
    {
        var (soga, group) = await ArrangeGroupAsync(o => o.Messages.MaximumTextLength = 10);
        using var _ = soga;
        var message = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "same");
        var workflow = soga.Get<MessageWorkflow>();
        var head = await soga.Get<ISogaStore>().GetChangeHeadAsync(TestSoga.Tenant, default);

        var same = await workflow.EditAsync(TestSoga.Alice, group.Id, message.Id, new("same"), default);
        var empty = await workflow.EditAsync(TestSoga.Alice, group.Id, message.Id, new("  "), default);
        var tooLong = await workflow.EditAsync(TestSoga.Alice, group.Id, message.Id, new(new string('x', 11)), default);

        Assert.Null(same.Value.EditedAt);
        Assert.Equal(head, await soga.Get<ISogaStore>().GetChangeHeadAsync(TestSoga.Tenant, default));
        Assert.Equal(SogaErrorCodes.InvalidRequest, empty.Error?.Code);
        Assert.Equal(SogaErrorCodes.MessageTooLong, tooLong.Error?.Code);
    }

    [Fact]
    public async Task Edit_OfMissingDeletedOrSystemMessages_IsRefused()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var workflow = soga.Get<MessageWorkflow>();
        var deleted = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "gone");
        await workflow.DeleteAsync(TestSoga.Alice, group.Id, deleted.Id, default);
        var system = (await soga.Get<ISogaSystemMessages>().SendAsync(TestSoga.Tenant, group.Id, "notice"))!;

        var missing = await workflow.EditAsync(TestSoga.Alice, group.Id, Guid.NewGuid(), new("x"), default);
        var afterDelete = await workflow.EditAsync(TestSoga.Alice, group.Id, deleted.Id, new("revive"), default);
        var systemEdit = await workflow.EditAsync(TestSoga.Alice, group.Id, system.Id, new("forged"), default);

        Assert.Equal(SogaErrorCodes.MessageNotFound, missing.Error?.Code);
        Assert.Equal(SogaErrorCodes.MessageNotEditable, afterDelete.Error?.Code);
        Assert.Equal(SogaErrorCodes.AuthorizationDenied, systemEdit.Error?.Code);
    }

    [Fact]
    public async Task Edit_KeepsHistoryOnlyWhenConfigured_AndDeleteErasesIt()
    {
        var (soga, group) = await ArrangeGroupAsync(o => o.Messages.RetainEditHistory = true);
        using var _ = soga;
        var workflow = soga.Get<MessageWorkflow>();
        var message = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "v1");
        await workflow.EditAsync(TestSoga.Alice, group.Id, message.Id, new("v2"), default);
        await workflow.EditAsync(TestSoga.Alice, group.Id, message.Id, new("v3"), default);
        var store = (InMemorySogaStore)soga.Get<ISogaStore>();

        Assert.Equal(["v1", "v2"], store.GetEditHistory(message.Id));
        await workflow.DeleteAsync(TestSoga.Alice, group.Id, message.Id, default);
        Assert.Empty(store.GetEditHistory(message.Id));
    }

    // ---- deleting ----

    [Fact]
    public async Task Delete_LeavesAnEmptyTombstoneSoSequencesStayContiguous()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var first = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "one");
        await soga.SendAsync(TestSoga.Alice, group.Id, "c2", "two");
        await soga.SendAsync(TestSoga.Alice, group.Id, "c3", "three");

        await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Alice, group.Id, first.Id, default);

        var history = (await soga.Get<MessageWorkflow>().GetHistoryAsync(TestSoga.Bob, group.Id, null, null, default)).Value.Items;
        Assert.Equal([3L, 2L, 1L], history.Select(m => m.Sequence));
        var tombstone = history.Single(m => m.Id == first.Id);
        Assert.Equal((true, string.Empty), (tombstone.Deleted, tombstone.Content));
    }

    [Fact]
    public async Task Delete_IsIdempotent_AndDeletedMessagesLeaveUnreadCounts()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var message = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "oops");
        await soga.SendAsync(TestSoga.Alice, group.Id, "c2", "kept");
        var workflow = soga.Get<MessageWorkflow>();
        Assert.Equal(2, (await soga.Get<ReadStateWorkflow>().GetTotalUnreadAsync(TestSoga.Bob, default)).Value.TotalUnread);

        var first = await workflow.DeleteAsync(TestSoga.Alice, group.Id, message.Id, default);
        var again = await workflow.DeleteAsync(TestSoga.Alice, group.Id, message.Id, default);

        Assert.True(first.IsSuccess && again.IsSuccess);
        Assert.Equal(1, (await soga.Get<ReadStateWorkflow>().GetTotalUnreadAsync(TestSoga.Bob, default)).Value.TotalUnread);
    }

    [Fact]
    public async Task Delete_OthersMessages_NeedAnAdminRoleInAGroup()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var message = await soga.SendAsync(TestSoga.Bob, group.Id, "b1", "rude");
        var workflow = soga.Get<MessageWorkflow>();

        var byMember = await workflow.DeleteAsync(TestSoga.Carol, group.Id, message.Id, default);
        var byOwner = await workflow.DeleteAsync(TestSoga.Alice, group.Id, message.Id, default);

        Assert.Equal(SogaErrorCodes.AuthorizationDenied, byMember.Error?.Code);
        Assert.True(byOwner.IsSuccess);
    }

    [Fact]
    public async Task Delete_ByAdministrator_IsAuditedAndCanBeDisabled()
    {
        var sink = new RecordingSink();
        var (soga, group) = await ArrangeGroupAsync(services: s => s.AddSingleton<ISogaAuditSink>(sink));
        using var _ = soga;
        var message = await soga.SendAsync(TestSoga.Bob, group.Id, "b1", "rude");

        await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Alice, group.Id, message.Id, default);

        var entry = Assert.Single(sink.Entries, e => e.Action == "message.deleted_by_admin");
        Assert.Equal(("alice", "bob"), (entry.ActorUserId, entry.TargetUserId));
        Assert.DoesNotContain("rude", entry.Detail ?? string.Empty, StringComparison.Ordinal);

        var (locked, lockedGroup) = await ArrangeGroupAsync(o => o.Messages.AllowAdminDeletion = false);
        using var __ = locked;
        var others = await locked.SendAsync(TestSoga.Bob, lockedGroup.Id, "b1", "rude");
        var denied = await locked.Get<MessageWorkflow>().DeleteAsync(TestSoga.Alice, lockedGroup.Id, others.Id, default);
        Assert.Equal(SogaErrorCodes.AuthorizationDenied, denied.Error?.Code);
    }

    [Fact]
    public async Task Delete_InADirectConversation_OnlyTheSenderMay()
    {
        using var soga = TestSoga.Create();
        var direct = (await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value;
        var message = await soga.SendAsync(TestSoga.Alice, direct.Id, "c1", "mine");

        var byOther = await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Bob, direct.Id, message.Id, default);

        Assert.Equal(SogaErrorCodes.AuthorizationDenied, byOther.Error?.Code);
    }

    [Fact]
    public async Task Delete_WhenOwnDeletionIsDisabled_IsRefused()
    {
        var (soga, group) = await ArrangeGroupAsync(o => o.Messages.AllowDeletion = false);
        using var _ = soga;
        var message = await soga.SendAsync(TestSoga.Bob, group.Id, "b1", "stuck");

        var result = await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Bob, group.Id, message.Id, default);

        Assert.Equal(SogaErrorCodes.AuthorizationDenied, result.Error?.Code);
    }

    [Fact]
    public async Task Delete_SystemMessagesCannotBeDeletedByAnyone()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var system = (await soga.Get<ISogaSystemMessages>().SendAsync(TestSoga.Tenant, group.Id, "notice"))!;

        var byOwner = await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Alice, group.Id, system.Id, default);

        Assert.Equal(SogaErrorCodes.MessageNotDeletable, byOwner.Error?.Code);
    }

    [Fact]
    public async Task EditAndDelete_ArePublishedAsDurableEventsThatSyncRecovers()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var message = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "v1");
        var sync = soga.Get<SyncWorkflow>();
        var cursor = (await sync.SyncAsync(TestSoga.Bob, null, null, default)).Value.Cursor;

        // Bob is offline while Alice edits and then deletes.
        await soga.Get<MessageWorkflow>().EditAsync(TestSoga.Alice, group.Id, message.Id, new("v2"), default);
        await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Alice, group.Id, message.Id, default);

        var events = (await sync.SyncAsync(TestSoga.Bob, cursor, null, default)).Value.Events;

        Assert.Equal([SogaEventTypes.MessageUpdated, SogaEventTypes.MessageDeleted], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Equal(message.Sequence, e.Sequence));
        Assert.Equal("v2", SogaEventSerializer.ReadData<MessageCreatedData>(events[0]).Message.Content);
        var deleted = SogaEventSerializer.ReadData<MessageDeletedData>(events[1]);
        Assert.Equal((message.Id, "alice"), (deleted.MessageId, deleted.DeletedBy));
    }

    // ---- replies ----

    [Fact]
    public async Task Reply_CarriesABoundedSummaryOfTheOriginal()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var original = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", new string('x', 500));

        var reply = await soga.SendAsync(TestSoga.Bob, group.Id, "b1", "answer", original.Id);

        Assert.Equal(original.Id, reply.ReplyTo!.MessageId);
        Assert.Equal("alice", reply.ReplyTo.SenderId);
        Assert.Equal(100, reply.ReplyTo.Snippet.Length);
        Assert.False(reply.ReplyTo.Deleted);
    }

    [Fact]
    public async Task Reply_RejectsMissingDeletedAndCrossConversationTargets()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var other = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var elsewhere = await soga.SendAsync(TestSoga.Alice, other.Id, "c1", "in another conversation");
        var deleted = await soga.SendAsync(TestSoga.Alice, group.Id, "c2", "gone");
        await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Alice, group.Id, deleted.Id, default);
        var workflow = soga.Get<MessageWorkflow>();

        var missing = await workflow.SendAsync(TestSoga.Bob, group.Id, new("b1", "x", Guid.NewGuid()), default);
        var crossed = await workflow.SendAsync(TestSoga.Bob, group.Id, new("b2", "x", elsewhere.Id), default);
        var toDeleted = await workflow.SendAsync(TestSoga.Bob, group.Id, new("b3", "x", deleted.Id), default);

        Assert.All([missing, crossed, toDeleted], r => Assert.Equal(SogaErrorCodes.ReplyInvalid, r.Error?.Code));
    }

    [Fact]
    public async Task Reply_ShowsDeletedOnceTheOriginalIsDeletedWithoutItsText()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var original = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "secret words");
        var reply = await soga.SendAsync(TestSoga.Bob, group.Id, "b1", "re: secret", original.Id);
        await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Alice, group.Id, original.Id, default);

        var history = (await soga.Get<MessageWorkflow>().GetHistoryAsync(TestSoga.Carol, group.Id, null, null, default)).Value.Items;

        var summary = history.Single(m => m.Id == reply.Id).ReplyTo!;
        Assert.Equal((true, string.Empty), (summary.Deleted, summary.Snippet));
    }

    // ---- system messages ----

    [Fact]
    public async Task SystemMessages_GetASequenceAndSyncLikeAnyMessage_ButNotFromParticipants()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var sync = soga.Get<SyncWorkflow>();
        var cursor = (await sync.SyncAsync(TestSoga.Bob, null, null, default)).Value.Cursor;

        var system = (await soga.Get<ISogaSystemMessages>().SendAsync(TestSoga.Tenant, group.Id, "Order shipped"))!;

        Assert.Equal((SogaMessageKinds.System, 1L), (system.Kind, system.Sequence));
        Assert.NotEqual("alice", system.SenderId);
        var events = (await sync.SyncAsync(TestSoga.Bob, cursor, null, default)).Value.Events;
        Assert.Equal(SogaEventTypes.MessageCreated, Assert.Single(events).Type);
        Assert.Equal(1, (await soga.Get<ReadStateWorkflow>().GetTotalUnreadAsync(TestSoga.Bob, default)).Value.TotalUnread);
    }

    [Fact]
    public async Task SystemMessages_AreScopedToTheTenantAndRejectBadInput()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        var system = soga.Get<ISogaSystemMessages>();

        var wrongTenant = await system.SendAsync("another-tenant", group.Id, "hi");
        var unknown = await system.SendAsync(TestSoga.Tenant, Guid.NewGuid(), "hi");

        Assert.Null(wrongTenant);
        Assert.Null(unknown);
        await Assert.ThrowsAsync<ArgumentException>(() => system.SendAsync(TestSoga.Tenant, group.Id, " "));
    }

    [Fact]
    public async Task SystemMessages_MayBePostedToAnArchivedConversation()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using var _ = soga;
        await soga.Get<GroupWorkflow>().ArchiveAsync(TestSoga.Alice, group.Id, true, default);

        var system = await soga.Get<ISogaSystemMessages>().SendAsync(TestSoga.Tenant, group.Id, "Archived by request");

        Assert.NotNull(system);
    }

    // ---- extension points ----

    [Fact]
    public async Task Validator_CanRejectSendsAndEditsButOnlyWithAReasonCode()
    {
        var (soga, group) = await ArrangeGroupAsync(services: s => s.AddSingleton<ISogaMessageValidator, NoLinksValidator>());
        using var _ = soga;
        var workflow = soga.Get<MessageWorkflow>();

        var rejected = await workflow.SendAsync(TestSoga.Alice, group.Id, new("c1", "visit http://spam.example"), default);
        var accepted = await workflow.SendAsync(TestSoga.Alice, group.Id, new("c2", "plain text"), default);
        var editRejected = await workflow.EditAsync(TestSoga.Alice, group.Id, accepted.Value.Value.Id, new("now http://spam.example"), default);

        Assert.Equal((SogaErrorCodes.MessageRejected, "contains_link"), (rejected.Error?.Code, rejected.Error?.Detail));
        Assert.Equal(SogaErrorCodes.MessageRejected, editRejected.Error?.Code);
        Assert.Single((await workflow.GetHistoryAsync(TestSoga.Alice, group.Id, null, null, default)).Value.Items);
    }

    [Fact]
    public async Task Policy_OnlyDeniesAndSeesTheTrustedTenantAndRole()
    {
        var policy = new RecordingPolicy(SogaAction.SendMessage);
        var (soga, group) = await ArrangeGroupAsync(services: s => s.AddSingleton<ISogaAuthorizationPolicy>(policy));
        using var _ = soga;
        var workflow = soga.Get<MessageWorkflow>();

        var denied = await workflow.SendAsync(TestSoga.Bob, group.Id, new("b1", "muted user"), default);
        var outsider = await workflow.SendAsync(TestSoga.Dave, group.Id, new("d1", "hello"), default);

        Assert.Equal(SogaErrorCodes.AuthorizationDenied, denied.Error?.Code);
        Assert.Equal(SogaErrorCodes.ConversationNotFound, outsider.Error?.Code);
        var seen = Assert.Single(policy.Seen, c => c.Action == SogaAction.SendMessage);
        Assert.Equal((TestSoga.Tenant, "bob", "member", group.Id), (seen.TenantId, seen.UserId, seen.Role, seen.ConversationId));
    }

    [Fact]
    public async Task Policy_ThatThrowsDeniesTheOperation()
    {
        var (soga, group) = await ArrangeGroupAsync(services: s => s.AddSingleton<ISogaAuthorizationPolicy, BrokenPolicy>());
        using var _ = soga;

        var result = await soga.Get<MessageWorkflow>().SendAsync(TestSoga.Alice, group.Id, new("c1", "hi"), default);

        Assert.Equal(SogaErrorCodes.AuthorizationDenied, result.Error?.Code);
    }

    [Fact]
    public async Task Policy_CanRestrictWhoMayStartConversationsAndCreateGroups()
    {
        using var soga = TestSoga.Create(services: s => s.AddSingleton<ISogaAuthorizationPolicy, OnlyBuyersAndSellersPolicy>());

        var allowedDirect = await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("bob", "order:1"), default);
        var deniedDirect = await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("carol", "order:1"), default);
        var deniedGroup = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new("Team", ["bob"]), default);

        Assert.True(allowedDirect.IsSuccess);
        Assert.Equal(SogaErrorCodes.AuthorizationDenied, deniedDirect.Error?.Code);
        Assert.Equal(SogaErrorCodes.AuthorizationDenied, deniedGroup.Error?.Code);
    }

    [Fact]
    public async Task Policy_CanNeverGrantWhatSogaRefuses()
    {
        var (soga, group) = await ArrangeGroupAsync(services: s => s.AddSingleton<ISogaAuthorizationPolicy, AllowEverythingPolicy>());
        using var _ = soga;

        var outsiderSend = await soga.Get<MessageWorkflow>().SendAsync(TestSoga.Dave, group.Id, new("d1", "let me in"), default);
        var memberAdd = await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Bob, group.Id, new("dave"), default);

        Assert.Equal(SogaErrorCodes.ConversationNotFound, outsiderSend.Error?.Code);
        Assert.Equal(SogaErrorCodes.AuthorizationDenied, memberAdd.Error?.Code);
    }

    private sealed class RecordingSink : ISogaAuditSink
    {
        public List<SogaAuditEntry> Entries { get; } = [];

        public ValueTask WriteAsync(SogaAuditEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoLinksValidator : ISogaMessageValidator
    {
        public ValueTask<SogaValidationResult> ValidateAsync(SogaMessageValidationContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(context.Text.Contains("http", StringComparison.OrdinalIgnoreCase)
                ? SogaValidationResult.Reject("contains_link")
                : SogaValidationResult.Accepted);
    }

    private sealed class RecordingPolicy(SogaAction denied) : ISogaAuthorizationPolicy
    {
        public List<SogaAuthorizationContext> Seen { get; } = [];

        public ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken)
        {
            Seen.Add(context);
            return ValueTask.FromResult(!(context.Action == denied && context.UserId == "bob"));
        }
    }

    private sealed class BrokenPolicy : ISogaAuthorizationPolicy
    {
        public ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken) =>
            context.Action == SogaAction.SendMessage
                ? throw new InvalidOperationException("policy backend down")
                : ValueTask.FromResult(true);
    }

    private sealed class AllowEverythingPolicy : ISogaAuthorizationPolicy
    {
        public ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class OnlyBuyersAndSellersPolicy : ISogaAuthorizationPolicy
    {
        public ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(context.Action switch
            {
                SogaAction.CreateDirectConversation => context.ExternalReference == "order:1" && context.TargetUserId == "bob",
                SogaAction.CreateGroup => false,
                _ => true,
            });
    }
}
