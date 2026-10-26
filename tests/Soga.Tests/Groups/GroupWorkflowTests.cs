using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Soga.Authorization;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Errors;
using Soga.Extensibility;
using Soga.Groups;
using Soga.Messages;
using Soga.Persistence;
using Soga.Synchronization;

namespace Soga.Tests.Groups;

public sealed class GroupWorkflowTests
{
    private static string Role(ConversationResponse conversation, string userId) =>
        conversation.Participants!.Single(p => p.UserId == userId).Role;

    [Fact]
    public async Task Create_MakesTheCallerOwnerAndTheOthersMembers()
    {
        using var soga = TestSoga.Create();

        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob", "carol");

        Assert.Equal((SogaConversationKinds.Group, "Team", 3, "owner"), (group.Kind, group.Title, group.ParticipantCount, group.MyRole));
        Assert.Equal("owner", Role(group, "alice"));
        Assert.Equal(["member", "member"], [Role(group, "bob"), Role(group, "carol")]);
    }

    [Fact]
    public async Task Create_IgnoresDuplicatesAndTheCallerInTheMemberList()
    {
        using var soga = TestSoga.Create();

        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob", "bob", "alice", " bob ");

        Assert.Equal(["alice", "bob"], group.ParticipantUserIds);
    }

    [Fact]
    public async Task Create_ValidatesTitleParticipantsAndSize()
    {
        using var soga = TestSoga.Create(o => o.Groups.MaximumParticipants = 3);
        var workflow = soga.Get<GroupWorkflow>();

        var noTitle = await workflow.CreateAsync(TestSoga.Alice, new("  ", ["bob"]), default);
        var longTitle = await workflow.CreateAsync(TestSoga.Alice, new(new string('t', 101), ["bob"]), default);
        var badMember = await workflow.CreateAsync(TestSoga.Alice, new("T", [" "]), default);
        var tooBig = await workflow.CreateAsync(TestSoga.Alice, new("T", ["b", "c", "d"]), default);
        var missing = await workflow.CreateAsync(TestSoga.Alice, null, default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, noTitle.Error?.Code);
        Assert.Equal(SogaErrorCodes.InvalidRequest, longTitle.Error?.Code);
        Assert.Equal(SogaErrorCodes.InvalidRequest, badMember.Error?.Code);
        Assert.Equal(SogaErrorCodes.GroupTooLarge, tooBig.Error?.Code);
        Assert.Equal(SogaErrorCodes.InvalidRequest, missing.Error?.Code);
    }

    [Fact]
    public async Task Create_WhenGroupsAreDisabled_IsRefused()
    {
        using var soga = TestSoga.Create(o => o.Groups.Enabled = false);

        var result = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new("T", ["bob"]), default);

        Assert.Equal(SogaErrorCodes.GroupsDisabled, result.Error?.Code);
    }

    [Fact]
    public async Task Create_StoresBoundedMetadataAndRejectsBadMetadata()
    {
        using var soga = TestSoga.Create();
        var workflow = soga.Get<GroupWorkflow>();
        var good = new Dictionary<string, JsonElement> { ["project"] = JsonDocument.Parse("\"apollo\"").RootElement };
        var bad = new Dictionary<string, JsonElement> { ["nested"] = JsonDocument.Parse("{\"x\":1}").RootElement };

        var created = await workflow.CreateAsync(TestSoga.Alice, new("T", ["bob"], "case:9", good), default);
        var rejected = await workflow.CreateAsync(TestSoga.Alice, new("T", ["bob"], null, bad), default);

        Assert.Equal("apollo", created.Value.Metadata!["project"].GetString());
        Assert.Equal("case:9", created.Value.ExternalReference);
        Assert.Equal(SogaErrorCodes.MetadataInvalid, rejected.Error?.Code);
    }

    [Fact]
    public async Task Add_ByOwnerAndAdmin_RespectsWhoMayGrantWhat()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob", "carol");
        var workflow = soga.Get<GroupWorkflow>();
        await workflow.ChangeRoleAsync(TestSoga.Alice, group.Id, "bob", new("admin"), default);

        var adminAddsMember = await workflow.AddParticipantAsync(TestSoga.Bob, group.Id, new("dave"), default);
        var adminGrantsAdmin = await workflow.AddParticipantAsync(TestSoga.Bob, group.Id, new("erin", "admin"), default);
        var memberAdds = await workflow.AddParticipantAsync(TestSoga.Carol, group.Id, new("frank"), default);
        var ownerGrantsAdmin = await workflow.AddParticipantAsync(TestSoga.Alice, group.Id, new("erin", "admin"), default);

        Assert.Equal("member", Role(adminAddsMember.Value!, "dave"));
        Assert.Equal(SogaErrorCodes.AuthorizationDenied, adminGrantsAdmin.Error?.Code);
        Assert.Equal(SogaErrorCodes.AuthorizationDenied, memberAdds.Error?.Code);
        Assert.Equal("admin", Role(ownerGrantsAdmin.Value!, "erin"));
    }

    [Fact]
    public async Task Add_WithAnUnknownRole_IsRejectedNotDowngraded()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");

        var result = await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group.Id, new("carol", "superadmin"), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task Add_RepeatedIsIdempotent()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var workflow = soga.Get<GroupWorkflow>();

        await workflow.AddParticipantAsync(TestSoga.Alice, group.Id, new("carol"), default);
        var again = await workflow.AddParticipantAsync(TestSoga.Alice, group.Id, new("carol"), default);

        Assert.Equal(3, again.Value!.ParticipantCount);
    }

    [Fact]
    public async Task Add_WhenGroupIsFull_ReturnsTooLarge()
    {
        using var soga = TestSoga.Create(o => o.Groups.MaximumParticipants = 2);
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");

        var result = await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group.Id, new("carol"), default);

        Assert.Equal(SogaErrorCodes.GroupTooLarge, result.Error?.Code);
    }

    [Fact]
    public async Task Operations_ByNonParticipants_AlwaysLookLikeNotFound()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var workflow = soga.Get<GroupWorkflow>();

        var results = new[]
        {
            (await workflow.AddParticipantAsync(TestSoga.Carol, group.Id, new("carol"), default)).Error?.Code,
            (await workflow.RemoveParticipantAsync(TestSoga.Carol, group.Id, "bob", default)).Error?.Code,
            (await workflow.ChangeRoleAsync(TestSoga.Carol, group.Id, "bob", new("owner"), default)).Error?.Code,
            (await workflow.UpdateAsync(TestSoga.Carol, group.Id, new("Hijacked"), default)).Error?.Code,
            (await workflow.ArchiveAsync(TestSoga.Carol, group.Id, true, default)).Error?.Code,
            (await workflow.ArchiveAsync(TestSoga.Carol, Guid.NewGuid(), true, default)).Error?.Code,
        };

        Assert.All(results, code => Assert.Equal(SogaErrorCodes.ConversationNotFound, code));
    }

    [Fact]
    public async Task GroupOperations_OnADirectConversation_ReturnNotGroup()
    {
        using var soga = TestSoga.Create();
        var direct = (await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value;

        var result = await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, direct.Id, new("carol"), default);

        Assert.Equal(SogaErrorCodes.ConversationNotGroup, result.Error?.Code);
    }

    [Fact]
    public async Task Remove_ByOwnerCutsAccessAtOnce()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var messages = soga.Get<MessageWorkflow>();
        await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "before");

        var removed = await soga.Get<GroupWorkflow>().RemoveParticipantAsync(TestSoga.Alice, group.Id, "bob", default);

        Assert.Equal(1, removed.Value!.ParticipantCount);
        Assert.Equal(SogaErrorCodes.ConversationNotFound, (await soga.Get<ConversationWorkflow>().GetAsync(TestSoga.Bob, group.Id, default)).Error?.Code);
        Assert.Equal(SogaErrorCodes.ConversationNotFound, (await messages.GetHistoryAsync(TestSoga.Bob, group.Id, null, null, default)).Error?.Code);
        Assert.Equal(SogaErrorCodes.ConversationNotFound, (await messages.SendAsync(TestSoga.Bob, group.Id, new("c2", "still here?"), default)).Error?.Code);
    }

    [Fact]
    public async Task Leave_ReturnsNoConversationBecauseTheCallerLostAccess()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");

        var left = await soga.Get<GroupWorkflow>().RemoveParticipantAsync(TestSoga.Bob, group.Id, "bob", default);

        Assert.True(left.IsSuccess);
        Assert.Null(left.Value);
    }

    [Fact]
    public async Task Leave_TheLastOwnerIsRefusedUntilOwnershipIsTransferred()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var workflow = soga.Get<GroupWorkflow>();

        var refused = await workflow.RemoveParticipantAsync(TestSoga.Alice, group.Id, "alice", default);
        await workflow.ChangeRoleAsync(TestSoga.Alice, group.Id, "bob", new("owner"), default);
        var allowed = await workflow.RemoveParticipantAsync(TestSoga.Alice, group.Id, "alice", default);

        Assert.Equal(SogaErrorCodes.GroupLastOwner, refused.Error?.Code);
        Assert.True(allowed.IsSuccess);
        Assert.Equal("owner", (await soga.Get<ConversationWorkflow>().GetAsync(TestSoga.Bob, group.Id, default)).Value.MyRole);
    }

    [Fact]
    public async Task Remove_AdminsCannotRemoveOwnersOrOtherAdmins()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob", "carol", "dave");
        var workflow = soga.Get<GroupWorkflow>();
        await workflow.ChangeRoleAsync(TestSoga.Alice, group.Id, "bob", new("admin"), default);
        await workflow.ChangeRoleAsync(TestSoga.Alice, group.Id, "carol", new("admin"), default);

        var removeOwner = await workflow.RemoveParticipantAsync(TestSoga.Bob, group.Id, "alice", default);
        var removeAdmin = await workflow.RemoveParticipantAsync(TestSoga.Bob, group.Id, "carol", default);
        var removeMember = await workflow.RemoveParticipantAsync(TestSoga.Bob, group.Id, "dave", default);

        Assert.Equal(SogaErrorCodes.AuthorizationDenied, removeOwner.Error?.Code);
        Assert.Equal(SogaErrorCodes.AuthorizationDenied, removeAdmin.Error?.Code);
        Assert.True(removeMember.IsSuccess);
    }

    [Fact]
    public async Task Remove_RepeatedAndReAddingAreIdempotentAndRestoreAccess()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var workflow = soga.Get<GroupWorkflow>();

        await workflow.RemoveParticipantAsync(TestSoga.Alice, group.Id, "bob", default);
        var removeAgain = await workflow.RemoveParticipantAsync(TestSoga.Alice, group.Id, "bob", default);
        var neverMember = await workflow.RemoveParticipantAsync(TestSoga.Alice, group.Id, "zed", default);
        var reAdded = await workflow.AddParticipantAsync(TestSoga.Alice, group.Id, new("bob"), default);

        Assert.True(removeAgain.IsSuccess);
        Assert.Equal(SogaErrorCodes.ParticipantNotFound, neverMember.Error?.Code);
        Assert.Equal(2, reAdded.Value!.ParticipantCount);
        Assert.True((await soga.Get<ConversationWorkflow>().GetAsync(TestSoga.Bob, group.Id, default)).IsSuccess);
    }

    [Fact]
    public async Task ChangeRole_OnlyOwnersAndNeverLeavesAGroupWithoutAnOwner()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var workflow = soga.Get<GroupWorkflow>();

        var promoted = await workflow.ChangeRoleAsync(TestSoga.Alice, group.Id, "bob", new("admin"), default);
        var byAdmin = await workflow.ChangeRoleAsync(TestSoga.Bob, group.Id, "bob", new("owner"), default);
        var demoteSelf = await workflow.ChangeRoleAsync(TestSoga.Alice, group.Id, "alice", new("member"), default);
        var unknown = await workflow.ChangeRoleAsync(TestSoga.Alice, group.Id, "bob", new("emperor"), default);

        Assert.Equal("admin", Role(promoted.Value!, "bob"));
        Assert.Equal(SogaErrorCodes.AuthorizationDenied, byAdmin.Error?.Code);
        Assert.Equal(SogaErrorCodes.GroupLastOwner, demoteSelf.Error?.Code);
        Assert.Equal(SogaErrorCodes.InvalidRequest, unknown.Error?.Code);
    }

    [Fact]
    public async Task Update_ChangesTitleAndMetadataForAdminsOnly()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var workflow = soga.Get<GroupWorkflow>();
        var metadata = new Dictionary<string, JsonElement> { ["room"] = JsonDocument.Parse("12").RootElement };

        var byMember = await workflow.UpdateAsync(TestSoga.Bob, group.Id, new("Bob's"), default);
        var byOwner = await workflow.UpdateAsync(TestSoga.Alice, group.Id, new("Renamed", metadata), default);
        var empty = await workflow.UpdateAsync(TestSoga.Alice, group.Id, new(), default);

        Assert.Equal(SogaErrorCodes.AuthorizationDenied, byMember.Error?.Code);
        Assert.Equal(("Renamed", 12), (byOwner.Value!.Title, byOwner.Value.Metadata!["room"].GetInt32()));
        Assert.Equal(SogaErrorCodes.InvalidRequest, empty.Error?.Code);
    }

    [Fact]
    public async Task Archive_StopsNewMessagesButKeepsHistoryReadableAndCanBeUndone()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var messages = soga.Get<MessageWorkflow>();
        var workflow = soga.Get<GroupWorkflow>();
        await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "hello");

        var byMember = await workflow.ArchiveAsync(TestSoga.Bob, group.Id, true, default);
        var archived = await workflow.ArchiveAsync(TestSoga.Alice, group.Id, true, default);
        var send = await messages.SendAsync(TestSoga.Bob, group.Id, new("c2", "late"), default);
        var history = await messages.GetHistoryAsync(TestSoga.Bob, group.Id, null, null, default);
        var restored = await workflow.ArchiveAsync(TestSoga.Alice, group.Id, false, default);
        var sendAgain = await messages.SendAsync(TestSoga.Bob, group.Id, new("c2", "late"), default);

        Assert.Equal(SogaErrorCodes.AuthorizationDenied, byMember.Error?.Code);
        Assert.NotNull(archived.Value!.ArchivedAt);
        Assert.Equal(SogaErrorCodes.ConversationArchived, send.Error?.Code);
        Assert.Single(history.Value.Items);
        Assert.Null(restored.Value!.ArchivedAt);
        Assert.True(sendAgain.IsSuccess);
    }

    [Fact]
    public async Task Membership_ChangesAreDurableEventsThatSyncRecovers()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var workflow = soga.Get<GroupWorkflow>();
        var sync = soga.Get<SyncWorkflow>();
        var cursor = (await sync.SyncAsync(TestSoga.Bob, null, null, default)).Value.Cursor;

        await workflow.AddParticipantAsync(TestSoga.Alice, group.Id, new("carol"), default);
        await workflow.ChangeRoleAsync(TestSoga.Alice, group.Id, "carol", new("admin"), default);
        await workflow.UpdateAsync(TestSoga.Alice, group.Id, new("Renamed"), default);
        await workflow.ArchiveAsync(TestSoga.Alice, group.Id, true, default);

        var events = (await sync.SyncAsync(TestSoga.Bob, cursor, null, default)).Value.Events;

        Assert.Equal(
            [SogaEventTypes.ParticipantChanged, SogaEventTypes.ParticipantChanged, SogaEventTypes.ConversationUpdated, SogaEventTypes.ConversationArchived],
            events.Select(e => e.Type));
        Assert.Equal(["added", "role_changed"], events.Take(2).Select(e => SogaEventSerializer.ReadData<ParticipantChangedData>(e).Change));
    }

    [Fact]
    public async Task RemovedParticipant_LearnsTheyWereRemovedThroughSyncButNothingElse()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var sync = soga.Get<SyncWorkflow>();
        var cursor = (await sync.SyncAsync(TestSoga.Bob, null, null, default)).Value.Cursor;

        await soga.Get<GroupWorkflow>().RemoveParticipantAsync(TestSoga.Alice, group.Id, "bob", default);
        await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "secret after removal");
        await soga.Get<GroupWorkflow>().UpdateAsync(TestSoga.Alice, group.Id, new("Secret plan"), default);

        var events = (await sync.SyncAsync(TestSoga.Bob, cursor, null, default)).Value.Events;

        var only = Assert.Single(events);
        Assert.Equal(new ParticipantChangedData("bob", "removed", null, "alice"), SogaEventSerializer.ReadData<ParticipantChangedData>(only));
    }

    [Fact]
    public async Task NewMember_StartsCaughtUpButCanReadHistory()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        for (var i = 1; i <= 3; i++)
        {
            await soga.SendAsync(TestSoga.Alice, group.Id, $"c{i}", $"old {i}");
        }

        await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group.Id, new("carol"), default);

        var forCarol = (await soga.Get<ConversationWorkflow>().GetAsync(TestSoga.Carol, group.Id, default)).Value;
        var history = (await soga.Get<MessageWorkflow>().GetHistoryAsync(TestSoga.Carol, group.Id, null, null, default)).Value;
        Assert.Equal(0, forCarol.UnreadCount);
        Assert.Equal(3, history.Items.Count);
    }

    [Fact]
    public async Task SystemMessages_AreOnlyPostedWhenConfigured()
    {
        using var off = TestSoga.Create();
        using var on = TestSoga.Create(o => o.Groups.PostSystemMessages = true);

        foreach (var (soga, expected) in new[] { (off, 0), (on, 1) })
        {
            var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
            await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group.Id, new("carol"), default);

            var history = (await soga.Get<MessageWorkflow>().GetHistoryAsync(TestSoga.Alice, group.Id, null, null, default)).Value;

            Assert.Equal(expected, history.Items.Count);
            if (expected == 1)
            {
                var message = history.Items.Single();
                Assert.Equal(SogaMessageKinds.System, message.Kind);
                using var body = JsonDocument.Parse(message.Content);
                Assert.Equal("participant_added", body.RootElement.GetProperty("code").GetString());
                Assert.Equal("carol", body.RootElement.GetProperty("target").GetString());
            }
        }
    }

    [Fact]
    public async Task Audit_RecordsEveryPrivilegedChangeWithoutMessageContent()
    {
        var sink = new RecordingAuditSink();
        using var soga = TestSoga.Create(services: s => s.AddSingleton<ISogaAuditSink>(sink));
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var workflow = soga.Get<GroupWorkflow>();

        await workflow.AddParticipantAsync(TestSoga.Alice, group.Id, new("carol", "admin"), default);
        await workflow.AddParticipantAsync(TestSoga.Alice, group.Id, new("carol"), default);
        await workflow.ChangeRoleAsync(TestSoga.Alice, group.Id, "bob", new("admin"), default);
        await workflow.RemoveParticipantAsync(TestSoga.Alice, group.Id, "carol", default);
        await workflow.UpdateAsync(TestSoga.Alice, group.Id, new("Renamed"), default);
        await workflow.ArchiveAsync(TestSoga.Alice, group.Id, true, default);
        await workflow.RemoveParticipantAsync(TestSoga.Bob, group.Id, "alice", default);

        Assert.Equal(
            ["group.created", "participant.added", "participant.role_changed", "participant.removed", "group.updated", "group.archived"],
            sink.Entries.Select(e => e.Action));
        Assert.Equal("role=admin", sink.Entries[1].Detail);
        Assert.All(sink.Entries, e => Assert.Equal("alice", e.ActorUserId));
    }

    [Fact]
    public async Task Audit_AFailingSinkNeverUndoesTheCompletedOperation()
    {
        using var soga = TestSoga.Create(services: s => s.AddSingleton<ISogaAuditSink, ThrowingAuditSink>());

        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var added = await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group.Id, new("carol"), default);

        Assert.True(added.IsSuccess);
    }

    [Fact]
    public async Task ConcurrentMembershipChanges_HaveADeterministicOutcome()
    {
        using var soga = TestSoga.Create(o => o.Groups.MaximumParticipants = 21);
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var store = soga.Get<ISogaStore>();
        var ids = soga.Get<Soga.Abstractions.ISogaIdGenerator>();

        // Twenty adds race into a group of 2 that is capped at 21: whatever the interleaving, exactly 19 fit.
        var tasks = Enumerable.Range(1, 20).Select(i => Task.Run(() => store.MutateGroupAsync(
            new GroupMutationCommand(TestSoga.Tenant, "alice", group.Id, new AddParticipantMutation($"user{i}", SogaRole.Member), 21, false, Guid.NewGuid(), ids, DateTimeOffset.UtcNow),
            default)));
        var outcomes = await Task.WhenAll(tasks);

        Assert.Equal(19, outcomes.Count(o => o.Error == GroupError.None && o.Changed));
        Assert.Equal(1, outcomes.Count(o => o.Error == GroupError.TooLarge));
        var final = (await soga.Get<ConversationWorkflow>().GetAsync(TestSoga.Alice, group.Id, default)).Value;
        Assert.Equal(21, final.ParticipantCount);
    }

    [Fact]
    public async Task ConcurrentLastOwnerDepartures_NeverLeaveAGroupWithoutAnOwner()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var workflow = soga.Get<GroupWorkflow>();
        await workflow.ChangeRoleAsync(TestSoga.Alice, group.Id, "bob", new("owner"), default);

        // Both owners try to leave at the same moment. Exactly one may succeed.
        var results = await Task.WhenAll(
            Task.Run(() => workflow.RemoveParticipantAsync(TestSoga.Alice, group.Id, "alice", default)),
            Task.Run(() => workflow.RemoveParticipantAsync(TestSoga.Bob, group.Id, "bob", default)));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.Equal(1, results.Count(r => r.Error?.Code == SogaErrorCodes.GroupLastOwner));
    }

    private sealed class RecordingAuditSink : ISogaAuditSink
    {
        public List<SogaAuditEntry> Entries { get; } = [];

        public ValueTask WriteAsync(SogaAuditEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingAuditSink : ISogaAuditSink
    {
        public ValueTask WriteAsync(SogaAuditEntry entry, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("audit store down");
    }
}
