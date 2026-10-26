using Soga.Contracts;
using Soga.Conversations;
using Soga.Errors;
using Soga.Groups;
using Soga.Identity;
using Soga.Messages;

namespace Soga.Tests.Conversations;

public sealed class ConversationListTests
{
    private static async Task<List<ConversationResponse>> ListAllAsync(TestSoga soga, SogaCaller caller, int pageSize, string? kind = null, string? state = null, string? reference = null)
    {
        var all = new List<ConversationResponse>();
        string? cursor = null;
        do
        {
            var page = (await soga.Get<ConversationWorkflow>().ListAsync(caller, new(pageSize, cursor, kind, state, reference), default)).Value;
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return all;
    }

    [Fact]
    public async Task List_ReturnsALastMessagePreviewUnreadCountAndRole()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        await soga.SendAsync(TestSoga.Bob, group.Id, "b1", new string('x', 300));

        var item = (await soga.Get<ConversationWorkflow>().ListAsync(TestSoga.Alice, new(), default)).Value.Items.Single();

        Assert.Equal((1L, "owner", 2), (item.UnreadCount, item.MyRole, item.ParticipantCount));
        Assert.Equal(("bob", 100, false), (item.LastMessage!.SenderId, item.LastMessage.Snippet.Length, item.LastMessage.Deleted));
    }

    [Fact]
    public async Task List_GroupsOmitTheParticipantListButDirectConversationsKeepIt()
    {
        using var soga = TestSoga.Create();
        await soga.CreateGroupAsync(TestSoga.Alice, "bob", "carol");
        await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("dave"), default);

        var items = (await soga.Get<ConversationWorkflow>().ListAsync(TestSoga.Alice, new(), default)).Value.Items;

        var group = items.Single(c => c.Kind == SogaConversationKinds.Group);
        var direct = items.Single(c => c.Kind == SogaConversationKinds.Direct);
        Assert.Equal((3, 0), (group.ParticipantCount, group.Participants!.Count));
        Assert.Equal((2, 2), (direct.ParticipantCount, direct.Participants!.Count));
    }

    [Fact]
    public async Task List_TheLastMessagePreviewOfADeletedMessageIsEmpty()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var message = await soga.SendAsync(TestSoga.Bob, group.Id, "b1", "regret");
        await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Bob, group.Id, message.Id, default);

        var item = (await soga.Get<ConversationWorkflow>().ListAsync(TestSoga.Alice, new(), default)).Value.Items.Single();

        Assert.Equal((true, string.Empty), (item.LastMessage!.Deleted, item.LastMessage.Snippet));
    }

    [Fact]
    public async Task List_FiltersByKindStateAndExternalReference()
    {
        using var soga = TestSoga.Create();
        var workflow = soga.Get<ConversationWorkflow>();
        var groups = soga.Get<GroupWorkflow>();
        var active = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var archived = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        await groups.ArchiveAsync(TestSoga.Alice, archived.Id, true, default);
        var order = (await workflow.CreateDirectAsync(TestSoga.Alice, new("carol", "order:7"), default)).Value.Value;
        var plain = (await workflow.CreateDirectAsync(TestSoga.Alice, new("dave"), default)).Value.Value;

        Assert.Equal([active.Id], (await ListAllAsync(soga, TestSoga.Alice, 10, kind: "group")).Select(c => c.Id));
        Assert.Equal([archived.Id], (await ListAllAsync(soga, TestSoga.Alice, 10, state: "archived")).Select(c => c.Id));
        Assert.Equal(3, (await ListAllAsync(soga, TestSoga.Alice, 10)).Count);
        Assert.Equal(4, (await ListAllAsync(soga, TestSoga.Alice, 10, state: "all")).Count);
        Assert.Equal([order.Id], (await ListAllAsync(soga, TestSoga.Alice, 10, reference: "order:7")).Select(c => c.Id));
        Assert.Contains(plain.Id, (await ListAllAsync(soga, TestSoga.Alice, 10, kind: "direct")).Select(c => c.Id));
    }

    [Fact]
    public async Task List_PagesEveryConversationExactlyOnceEvenWhenTimestampsTie()
    {
        using var soga = TestSoga.Create();
        // The test clock does not move, so every conversation shares one activity time: the worst case for paging.
        var expected = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            expected.Add((await soga.CreateGroupAsync(TestSoga.Alice, $"user{i}")).Id);
        }

        var seen = (await ListAllAsync(soga, TestSoga.Alice, pageSize: 3)).Select(c => c.Id).ToList();

        Assert.Equal(7, seen.Count);
        Assert.Equal(expected.Order(), seen.Order());
    }

    [Fact]
    public async Task List_PagesByMostRecentActivityAcrossPages()
    {
        using var soga = TestSoga.Create();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await soga.CreateGroupAsync(TestSoga.Alice, "bob")).Id);
            soga.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        await soga.SendAsync(TestSoga.Alice, ids[0], "c1", "bump the oldest");

        var seen = (await ListAllAsync(soga, TestSoga.Alice, pageSize: 2)).Select(c => c.Id).ToList();

        Assert.Equal([ids[0], ids[4], ids[3], ids[2], ids[1]], seen);
    }

    [Fact]
    public async Task List_CursorsCannotBeReplayedUnderOtherFiltersUsersOrGarbage()
    {
        using var soga = TestSoga.Create();
        for (var i = 0; i < 4; i++)
        {
            await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        }

        var workflow = soga.Get<ConversationWorkflow>();
        var cursor = (await workflow.ListAsync(TestSoga.Alice, new(2), default)).Value.NextCursor!;

        var otherFilter = await workflow.ListAsync(TestSoga.Alice, new(2, cursor, Kind: "direct"), default);
        var otherUser = await workflow.ListAsync(TestSoga.Bob, new(2, cursor), default);
        var garbage = await workflow.ListAsync(TestSoga.Alice, new(2, "garbage"), default);

        Assert.All([otherFilter, otherUser, garbage], r => Assert.Equal(SogaErrorCodes.CursorInvalid, r.Error?.Code));
    }

    [Theory]
    [InlineData("neither", null)]
    [InlineData(null, "sometimes")]
    public async Task List_RejectsUnknownFilterValues(string? kind, string? state)
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<ConversationWorkflow>().ListAsync(TestSoga.Alice, new(Kind: kind, State: state), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task List_FiltersCannotWidenScopeBeyondMembershipOrTenant()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        await soga.Get<GroupWorkflow>().RemoveParticipantAsync(TestSoga.Alice, group.Id, "bob", default);

        var forBob = await ListAllAsync(soga, TestSoga.Bob, 10, state: "all");
        var otherTenant = (await soga.Get<ConversationWorkflow>().ListAsync(new("tenant-2", "alice"), new(State: "all"), default)).Value.Items;

        Assert.Empty(forBob);
        Assert.Empty(otherTenant);
    }

    [Fact]
    public async Task Create_DirectConversationMetadataIsStoredOnlyWhenCreated()
    {
        using var soga = TestSoga.Create();
        var workflow = soga.Get<ConversationWorkflow>();
        var metadata = new Dictionary<string, System.Text.Json.JsonElement> { ["order"] = System.Text.Json.JsonDocument.Parse("42").RootElement };

        var created = await workflow.CreateDirectAsync(TestSoga.Alice, new("bob", null, metadata), default);
        var again = await workflow.CreateDirectAsync(TestSoga.Bob, new("alice", null, new Dictionary<string, System.Text.Json.JsonElement> { ["order"] = System.Text.Json.JsonDocument.Parse("99").RootElement }), default);

        Assert.Equal(42, created.Value.Value.Metadata!["order"].GetInt32());
        Assert.Equal(42, again.Value.Value.Metadata!["order"].GetInt32());
    }
}
