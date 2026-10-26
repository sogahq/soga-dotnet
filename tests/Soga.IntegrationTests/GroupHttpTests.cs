using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Soga.Authorization;
using Soga.Contracts;
using Soga.Errors;
using Soga.Extensibility;

namespace Soga.IntegrationTests;

public sealed class GroupHttpTests
{
    private const string P = SogaTestServer.Prefix;

    [Fact]
    public async Task Group_CanBeCreatedManagedAndReadThroughTheHttpApi()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var bob = server.Client("bob");

        var created = await alice.PostAsJsonAsync($"{P}/conversations/groups", new CreateGroupRequest("Launch team", ["bob", "carol"], "project:apollo"));
        var group = await created.ReadAsync<ConversationResponse>();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"{P}/conversations/{group.Id}", created.Headers.Location?.OriginalString);
        Assert.Equal(("owner", "Launch team", 3), (group.MyRole, group.Title, group.ParticipantCount));

        var added = await (await alice.AddParticipantAsync(group.Id, "dave", "admin")).ReadAsync<ConversationResponse>();
        Assert.Equal("admin", added.Participants!.Single(p => p.UserId == "dave").Role);

        var renamed = await (await alice.PatchAsync($"{P}/conversations/{group.Id}", JsonContent.Create(new UpdateConversationRequest("Launch crew")))).ReadAsync<ConversationResponse>();
        Assert.Equal("Launch crew", renamed.Title);

        var forBob = await (await bob.GetAsync($"{P}/conversations/{group.Id}")).ReadAsync<ConversationResponse>();
        Assert.Equal(("member", 4), (forBob.MyRole, forBob.ParticipantCount));

        var archived = await (await alice.PostAsync($"{P}/conversations/{group.Id}/archive", null)).ReadAsync<ConversationResponse>();
        Assert.NotNull(archived.ArchivedAt);
        var refused = await bob.PostAsJsonAsync($"{P}/conversations/{group.Id}/messages", new SendMessageRequest("b1", "too late"));
        Assert.Equal((HttpStatusCode.Conflict, SogaErrorCodes.ConversationArchived), (refused.StatusCode, await refused.ErrorCodeAsync()));
        var restored = await (await alice.PostAsync($"{P}/conversations/{group.Id}/unarchive", null)).ReadAsync<ConversationResponse>();
        Assert.Null(restored.ArchivedAt);
    }

    [Fact]
    public async Task Removing_AndLeaving_ReturnTheRightStatusAndCutAccess()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var group = await alice.CreateGroupAsync("Team", "bob", "carol");

        var removed = await alice.RemoveParticipantAsync(group.Id, "bob");
        var left = await server.Client("carol").RemoveParticipantAsync(group.Id, "carol");
        var bobAfter = await server.Client("bob").GetAsync($"{P}/conversations/{group.Id}");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(2, (await removed.ReadAsync<ConversationResponse>()).ParticipantCount);
        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bobAfter.StatusCode);
    }

    [Fact]
    public async Task RoleEscalationAttempts_AreAllRefused()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var bob = server.Client("bob");
        var group = await alice.CreateGroupAsync("Team", "bob", "carol");
        await alice.ChangeRoleAsync(group.Id, "carol", "admin");

        // A member promotes themselves, an admin promotes to owner, an admin adds an admin, a member adds anyone.
        var selfPromote = await bob.ChangeRoleAsync(group.Id, "bob", "owner");
        var adminPromotes = await server.Client("carol").ChangeRoleAsync(group.Id, "bob", "owner");
        var adminAddsAdmin = await server.Client("carol").AddParticipantAsync(group.Id, "erin", "admin");
        var memberAdds = await bob.AddParticipantAsync(group.Id, "erin");
        var adminRemovesOwner = await server.Client("carol").RemoveParticipantAsync(group.Id, "alice");
        var unknownRole = await alice.ChangeRoleAsync(group.Id, "bob", "god");

        foreach (var response in new[] { selfPromote, adminPromotes, adminAddsAdmin, memberAdds, adminRemovesOwner })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(SogaErrorCodes.AuthorizationDenied, await response.ErrorCodeAsync());
        }

        Assert.Equal((HttpStatusCode.BadRequest, SogaErrorCodes.InvalidRequest), (unknownRole.StatusCode, await unknownRole.ErrorCodeAsync()));
        var check = await (await alice.GetAsync($"{P}/conversations/{group.Id}")).ReadAsync<ConversationResponse>();
        Assert.Equal("member", check.Participants!.Single(p => p.UserId == "bob").Role);
        Assert.DoesNotContain(check.Participants!, p => p.UserId == "erin");
    }

    [Fact]
    public async Task TheLastOwner_CannotLeaveOrBeDemotedOverHttp()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var group = await alice.CreateGroupAsync("Team", "bob");

        var leave = await alice.RemoveParticipantAsync(group.Id, "alice");
        var demote = await alice.ChangeRoleAsync(group.Id, "alice", "member");

        Assert.Equal((HttpStatusCode.Conflict, SogaErrorCodes.GroupLastOwner), (leave.StatusCode, await leave.ErrorCodeAsync()));
        Assert.Equal(SogaErrorCodes.GroupLastOwner, await demote.ErrorCodeAsync());
    }

    [Fact]
    public async Task Outsider_SeesIdenticalNotFoundForEveryGroupRoute()
    {
        await using var server = await SogaTestServer.StartAsync();
        var group = await server.Client("alice").CreateGroupAsync("Secret", "bob");
        var mallory = server.Client("mallory");
        var real = group.Id;
        var unknown = Guid.NewGuid();

        var probes = new Func<Guid, Task<HttpResponseMessage>>[]
        {
            id => mallory.AddParticipantAsync(id, "mallory", "owner"),
            id => mallory.RemoveParticipantAsync(id, "bob"),
            id => mallory.ChangeRoleAsync(id, "mallory", "owner"),
            id => mallory.PatchAsync($"{P}/conversations/{id}", JsonContent.Create(new UpdateConversationRequest("Owned"))),
            id => mallory.PostAsync($"{P}/conversations/{id}/archive", null),
            id => mallory.PatchAsync($"{P}/conversations/{id}/messages/{Guid.NewGuid()}", JsonContent.Create(new EditMessageRequest("x"))),
            id => mallory.DeleteAsync($"{P}/conversations/{id}/messages/{Guid.NewGuid()}"),
        };

        foreach (var probe in probes)
        {
            var forReal = await probe(real);
            var forUnknown = await probe(unknown);

            Assert.Equal((HttpStatusCode.NotFound, SogaErrorCodes.ConversationNotFound), (forReal.StatusCode, await forReal.ErrorCodeAsync()));
            Assert.Equal(forUnknown.StatusCode, forReal.StatusCode);
        }
    }

    [Fact]
    public async Task GroupOperationsOnADirectConversation_AreRefused()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var direct = await alice.OpenConversationAsync("bob");

        var response = await alice.AddParticipantAsync(direct.Id, "carol");

        Assert.Equal((HttpStatusCode.Conflict, SogaErrorCodes.ConversationNotGroup), (response.StatusCode, await response.ErrorCodeAsync()));
    }

    [Fact]
    public async Task GroupCreation_IsValidatedAndBounded()
    {
        await using var server = await SogaTestServer.StartAsync(o => o.Groups.MaximumParticipants = 3);
        var alice = server.Client("alice");

        var noTitle = await alice.PostAsJsonAsync($"{P}/conversations/groups", new CreateGroupRequest(" ", ["bob"]));
        var tooBig = await alice.PostAsJsonAsync($"{P}/conversations/groups", new CreateGroupRequest("T", ["a", "b", "c"]));
        var badMeta = await alice.PostAsync(
            $"{P}/conversations/groups",
            new StringContent("{\"title\":\"T\",\"participantUserIds\":[],\"metadata\":{\"x\":{\"nested\":1}}}", Encoding.UTF8, "application/json"));

        Assert.Equal(SogaErrorCodes.InvalidRequest, await noTitle.ErrorCodeAsync());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, SogaErrorCodes.GroupTooLarge), (tooBig.StatusCode, await tooBig.ErrorCodeAsync()));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, SogaErrorCodes.MetadataInvalid), (badMeta.StatusCode, await badMeta.ErrorCodeAsync()));
    }

    [Fact]
    public async Task GroupsCanBeDisabled()
    {
        await using var server = await SogaTestServer.StartAsync(o => o.Groups.Enabled = false);

        var response = await server.Client("alice").PostAsJsonAsync($"{P}/conversations/groups", new CreateGroupRequest("T", ["bob"]));

        Assert.Equal((HttpStatusCode.Forbidden, SogaErrorCodes.GroupsDisabled), (response.StatusCode, await response.ErrorCodeAsync()));
    }

    [Fact]
    public async Task UserIdsWithSpecialCharacters_AreHandledInRoutes()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var group = await alice.CreateGroupAsync("Team", "bob@example.com");

        var removed = await alice.RemoveParticipantAsync(group.Id, "bob@example.com");

        Assert.Equal(1, (await removed.ReadAsync<ConversationResponse>()).ParticipantCount);
    }

    [Fact]
    public async Task Message_EditDeleteAndReply_WorkThroughTheHttpApi()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var bob = server.Client("bob");
        var group = await alice.CreateGroupAsync("Team", "bob");
        var original = await alice.SendAsync(group.Id, "c1", "hello wrld");

        var reply = await (await bob.PostAsJsonAsync($"{P}/conversations/{group.Id}/messages", new SendMessageRequest("b1", "hi!", original.Id))).ReadAsync<MessageResponse>();
        Assert.Equal((original.Id, "alice"), (reply.ReplyTo!.MessageId, reply.ReplyTo.SenderId));

        var edit = await alice.PatchAsync($"{P}/conversations/{group.Id}/messages/{original.Id}", JsonContent.Create(new EditMessageRequest("hello world")));
        var edited = await edit.ReadAsync<MessageResponse>();
        Assert.Equal(("hello world", original.Sequence, true), (edited.Content, edited.Sequence, edited.EditedAt is not null));

        var byOther = await bob.PatchAsync($"{P}/conversations/{group.Id}/messages/{original.Id}", JsonContent.Create(new EditMessageRequest("hijack")));
        Assert.Equal((HttpStatusCode.Forbidden, SogaErrorCodes.AuthorizationDenied), (byOther.StatusCode, await byOther.ErrorCodeAsync()));

        var deleted = await alice.DeleteAsync($"{P}/conversations/{group.Id}/messages/{original.Id}");
        var deletedAgain = await alice.DeleteAsync($"{P}/conversations/{group.Id}/messages/{original.Id}");
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (deleted.StatusCode, deletedAgain.StatusCode));

        var history = await (await bob.GetAsync($"{P}/conversations/{group.Id}/messages")).ReadAsync<MessagePageResponse>();
        var tombstone = history.Items.Single(m => m.Id == original.Id);
        Assert.Equal((true, string.Empty), (tombstone.Deleted, tombstone.Content));
        Assert.True(history.Items.Single(m => m.Id == reply.Id).ReplyTo!.Deleted);

        var revive = await alice.PatchAsync($"{P}/conversations/{group.Id}/messages/{original.Id}", JsonContent.Create(new EditMessageRequest("back")));
        Assert.Equal((HttpStatusCode.Conflict, SogaErrorCodes.MessageNotEditable), (revive.StatusCode, await revive.ErrorCodeAsync()));
    }

    [Fact]
    public async Task Reply_ToAnotherConversationsMessage_IsRefused()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var one = await alice.CreateGroupAsync("One", "bob");
        var two = await alice.CreateGroupAsync("Two", "bob");
        var elsewhere = await alice.SendAsync(two.Id, "c1", "other");

        var response = await alice.PostAsJsonAsync($"{P}/conversations/{one.Id}/messages", new SendMessageRequest("c2", "x", elsewhere.Id));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, SogaErrorCodes.ReplyInvalid), (response.StatusCode, await response.ErrorCodeAsync()));
    }

    [Fact]
    public async Task ConversationList_SupportsFiltersAndCursorPaging()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var first = await alice.CreateGroupAsync("A", "bob");
        var second = await alice.CreateGroupAsync("B", "bob");
        var direct = await alice.OpenConversationAsync("carol", "order:1");
        await alice.PostAsync($"{P}/conversations/{first.Id}/archive", null);

        var groupsOnly = await (await alice.GetAsync($"{P}/conversations?kind=group")).ReadAsync<ConversationPageResponse>();
        var archived = await (await alice.GetAsync($"{P}/conversations?state=archived")).ReadAsync<ConversationPageResponse>();
        var byReference = await (await alice.GetAsync($"{P}/conversations?externalReference=order:1&state=all")).ReadAsync<ConversationPageResponse>();
        var page1 = await (await alice.GetAsync($"{P}/conversations?state=all&limit=2")).ReadAsync<ConversationPageResponse>();
        var page2 = await (await alice.GetAsync($"{P}/conversations?state=all&limit=2&cursor={Uri.EscapeDataString(page1.NextCursor!)}")).ReadAsync<ConversationPageResponse>();
        var badFilter = await alice.GetAsync($"{P}/conversations?kind=nonsense");
        var badCursor = await alice.GetAsync($"{P}/conversations?cursor=garbage");

        Assert.Equal([second.Id], groupsOnly.Items.Select(c => c.Id));
        Assert.Equal([first.Id], archived.Items.Select(c => c.Id));
        Assert.Equal([direct.Id], byReference.Items.Select(c => c.Id));
        Assert.Equal(3, page1.Items.Count + page2.Items.Count);
        Assert.Null(page2.NextCursor);
        Assert.Equal(SogaErrorCodes.InvalidRequest, await badFilter.ErrorCodeAsync());
        Assert.Equal(SogaErrorCodes.CursorInvalid, await badCursor.ErrorCodeAsync());
    }

    [Fact]
    public async Task HostPolicy_AppliesToHttpAndOnlyEverRestricts()
    {
        await using var server = await SogaTestServer.StartAsync(
            services: s => s.AddSingleton<ISogaAuthorizationPolicy, NoGroupsForMallory>());
        var mallory = server.Client("mallory");

        var denied = await mallory.PostAsJsonAsync($"{P}/conversations/groups", new CreateGroupRequest("T", ["bob"]));
        var stillFine = await server.Client("alice").PostAsJsonAsync($"{P}/conversations/groups", new CreateGroupRequest("T", ["bob"]));
        var direct = await mallory.PostAsJsonAsync($"{P}/conversations/direct", new CreateDirectConversationRequest("bob"));

        Assert.Equal((HttpStatusCode.Forbidden, SogaErrorCodes.AuthorizationDenied), (denied.StatusCode, await denied.ErrorCodeAsync()));
        Assert.Equal(HttpStatusCode.Created, stillFine.StatusCode);
        Assert.Equal(HttpStatusCode.Created, direct.StatusCode);
    }

    [Fact]
    public async Task HostValidator_RejectsMessagesWithAMachineReadableReason()
    {
        await using var server = await SogaTestServer.StartAsync(services: s => s.AddSingleton<ISogaMessageValidator, NoShouting>());
        var alice = server.Client("alice");
        var group = await alice.CreateGroupAsync("Team", "bob");

        var response = await alice.PostAsJsonAsync($"{P}/conversations/{group.Id}/messages", new SendMessageRequest("c1", "STOP SHOUTING"));

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(SogaErrorCodes.MessageRejected, problem.GetProperty("code").GetString());
        Assert.Equal("all_caps", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task SystemMessages_FromTrustedHostCode_AppearInHistoryAndCannotBeForgedByClients()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var group = await alice.CreateGroupAsync("Team", "bob");
        using var scope = server.Services.CreateScope();

        var system = await scope.ServiceProvider.GetRequiredService<ISogaSystemMessages>().SendAsync(SogaTestServer.DefaultTenant, group.Id, "Order shipped");
        var history = await (await alice.GetAsync($"{P}/conversations/{group.Id}/messages")).ReadAsync<MessagePageResponse>();
        var forgedEdit = await alice.PatchAsync($"{P}/conversations/{group.Id}/messages/{system!.Id}", JsonContent.Create(new EditMessageRequest("forged")));
        var forgedDelete = await alice.DeleteAsync($"{P}/conversations/{group.Id}/messages/{system.Id}");

        Assert.Equal(SogaMessageKinds.System, history.Items.Single().Kind);
        Assert.Equal(HttpStatusCode.Forbidden, forgedEdit.StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, SogaErrorCodes.MessageNotDeletable), (forgedDelete.StatusCode, await forgedDelete.ErrorCodeAsync()));
    }

    [Fact]
    public async Task Sync_RecoversEditsDeletesAndMembershipChangesForAnOfflineClient()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var bob = server.Client("bob");
        var carol = server.Client("carol");
        var group = await alice.CreateGroupAsync("Team", "bob", "carol");
        var message = await alice.SendAsync(group.Id, "c1", "v1");
        var cursor = (await (await bob.GetAsync($"{P}/sync")).ReadAsync<SyncResponse>()).Cursor;
        var carolCursor = (await (await carol.GetAsync($"{P}/sync")).ReadAsync<SyncResponse>()).Cursor;

        await alice.PatchAsync($"{P}/conversations/{group.Id}/messages/{message.Id}", JsonContent.Create(new EditMessageRequest("v2")));
        await alice.AddParticipantAsync(group.Id, "dave");
        await alice.DeleteAsync($"{P}/conversations/{group.Id}/messages/{message.Id}");
        await alice.RemoveParticipantAsync(group.Id, "bob");

        var forCarol = await (await carol.GetAsync($"{P}/sync?cursor={Uri.EscapeDataString(carolCursor)}")).ReadAsync<SyncResponse>();
        var forBob = await (await bob.GetAsync($"{P}/sync?cursor={Uri.EscapeDataString(cursor)}")).ReadAsync<SyncResponse>();

        // A remaining member recovers every change in order.
        Assert.Equal(
            [SogaEventTypes.MessageUpdated, SogaEventTypes.ParticipantChanged, SogaEventTypes.MessageDeleted, SogaEventTypes.ParticipantChanged],
            forCarol.Events.Select(e => e.Type));

        // The removed user has no access to the group any more, including its earlier changes. They receive exactly one
        // thing: the notice that they were removed.
        var removal = Assert.Single(forBob.Events);
        Assert.Equal(SogaEventTypes.ParticipantChanged, removal.Type);
        Assert.Equal("removed", removal.Data.GetProperty("change").GetString());
    }

    private sealed class NoGroupsForMallory : ISogaAuthorizationPolicy
    {
        public ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(!(context.Action == SogaAction.CreateGroup && context.UserId == "mallory"));
    }

    private sealed class NoShouting : ISogaMessageValidator
    {
        public ValueTask<SogaValidationResult> ValidateAsync(SogaMessageValidationContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(string.Equals(context.Text, context.Text.ToUpperInvariant(), StringComparison.Ordinal) && context.Text.Any(char.IsLetter)
                ? SogaValidationResult.Reject("all_caps")
                : SogaValidationResult.Accepted);
    }
}
