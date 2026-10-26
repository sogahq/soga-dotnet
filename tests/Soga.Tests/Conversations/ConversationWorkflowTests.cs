using Soga.Contracts;
using Soga.Conversations;
using Soga.Errors;

namespace Soga.Tests.Conversations;

public sealed class ConversationWorkflowTests
{
    [Fact]
    public async Task CreateDirect_WhenCalledTwice_ReturnsTheSameConversation()
    {
        using var soga = TestSoga.Create();
        var workflow = soga.Get<ConversationWorkflow>();

        var first = await workflow.CreateDirectAsync(TestSoga.Alice, new("bob"), default);
        var second = await workflow.CreateDirectAsync(TestSoga.Alice, new("bob"), default);

        Assert.True(first.Value.Created);
        Assert.False(second.Value.Created);
        Assert.Equal(first.Value.Value.Id, second.Value.Value.Id);
    }

    [Fact]
    public async Task CreateDirect_WhenTheOtherUserStartsIt_ReturnsTheSameConversation()
    {
        using var soga = TestSoga.Create();
        var workflow = soga.Get<ConversationWorkflow>();

        var fromAlice = await workflow.CreateDirectAsync(TestSoga.Alice, new("bob"), default);
        var fromBob = await workflow.CreateDirectAsync(TestSoga.Bob, new("alice"), default);

        Assert.Equal(fromAlice.Value.Value.Id, fromBob.Value.Value.Id);
        Assert.False(fromBob.Value.Created);
    }

    [Fact]
    public async Task CreateDirect_WhenExternalReferenceDiffers_CreatesSeparateConversations()
    {
        using var soga = TestSoga.Create();
        var workflow = soga.Get<ConversationWorkflow>();

        var general = await workflow.CreateDirectAsync(TestSoga.Alice, new("bob"), default);
        var order1 = await workflow.CreateDirectAsync(TestSoga.Alice, new("bob", "order:1"), default);
        var order1Again = await workflow.CreateDirectAsync(TestSoga.Bob, new("alice", " order:1 "), default);

        Assert.NotEqual(general.Value.Value.Id, order1.Value.Value.Id);
        Assert.Equal(order1.Value.Value.Id, order1Again.Value.Value.Id);
        Assert.Equal("order:1", order1.Value.Value.ExternalReference);
    }

    [Fact]
    public async Task CreateDirect_WhenSameTenantDiffers_CreatesSeparateConversations()
    {
        using var soga = TestSoga.Create();
        var workflow = soga.Get<ConversationWorkflow>();

        var inTenantOne = await workflow.CreateDirectAsync(new("tenant-1", "alice"), new("bob"), default);
        var inTenantTwo = await workflow.CreateDirectAsync(new("tenant-2", "alice"), new("bob"), default);

        Assert.NotEqual(inTenantOne.Value.Value.Id, inTenantTwo.Value.Value.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("alice")]
    public async Task CreateDirect_WhenOtherUserIsInvalid_ReturnsInvalidRequest(string? otherUserId)
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<ConversationWorkflow>()
            .CreateDirectAsync(TestSoga.Alice, new(otherUserId!), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task CreateDirect_WhenRequestIsMissing_ReturnsInvalidRequest()
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, null, default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task CreateDirect_WhenExternalReferenceIsTooLong_ReturnsInvalidRequest()
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<ConversationWorkflow>()
            .CreateDirectAsync(TestSoga.Alice, new("bob", new string('x', 201)), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task Get_WhenUserIsNotParticipant_DoesNotRevealConversation()
    {
        using var soga = TestSoga.Create();
        var workflow = soga.Get<ConversationWorkflow>();
        var created = await workflow.CreateDirectAsync(TestSoga.Alice, new("bob"), default);

        var forCarol = await workflow.GetAsync(TestSoga.Carol, created.Value.Value.Id, default);
        var forUnknown = await workflow.GetAsync(TestSoga.Carol, Guid.NewGuid(), default);

        Assert.Equal(SogaErrorCodes.ConversationNotFound, forCarol.Error?.Code);
        Assert.Equal(forUnknown.Error, forCarol.Error);
    }

    [Fact]
    public async Task Get_WhenTenantDiffers_DoesNotRevealConversation()
    {
        using var soga = TestSoga.Create();
        var workflow = soga.Get<ConversationWorkflow>();
        var created = await workflow.CreateDirectAsync(TestSoga.Alice, new("bob"), default);

        var result = await workflow.GetAsync(new("tenant-2", "alice"), created.Value.Value.Id, default);

        Assert.Equal(SogaErrorCodes.ConversationNotFound, result.Error?.Code);
    }

    [Fact]
    public async Task List_ReturnsOnlyTheCallersConversationsMostRecentFirst()
    {
        using var soga = TestSoga.Create();
        var conversations = soga.Get<ConversationWorkflow>();
        var messages = soga.Get<Soga.Messages.MessageWorkflow>();

        var withBob = (await conversations.CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value;
        var withCarol = (await conversations.CreateDirectAsync(TestSoga.Alice, new("carol"), default)).Value.Value;
        await conversations.CreateDirectAsync(TestSoga.Bob, new("carol"), default);
        await messages.SendAsync(TestSoga.Alice, withBob.Id, new("c1", "hi"), default);

        var list = (await conversations.ListAsync(TestSoga.Alice, null, default)).Value;

        Assert.Equal([withBob.Id, withCarol.Id], list.Select(c => c.Id));
    }

    [Fact]
    public async Task List_WhenLimitIsInvalid_ReturnsInvalidRequestAndClampsOversizedLimit()
    {
        using var soga = TestSoga.Create(options =>
        {
            options.Conversations.DefaultPageSize = 2;
            options.Conversations.MaximumPageSize = 2;
        });
        var workflow = soga.Get<ConversationWorkflow>();
        foreach (var user in new[] { "bob", "carol", "dave" })
        {
            await workflow.CreateDirectAsync(TestSoga.Alice, new(user), default);
        }

        var invalid = await workflow.ListAsync(TestSoga.Alice, 0, default);
        var clamped = await workflow.ListAsync(TestSoga.Alice, 100, default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, invalid.Error?.Code);
        Assert.Equal(2, clamped.Value.Count);
    }
}
