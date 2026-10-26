using Microsoft.EntityFrameworkCore;
using Soga.Abstractions;
using Soga.Contracts;
using Soga.EntityFrameworkCore.Entities;
using Soga.Persistence;
using Xunit;

namespace Soga.EntityFrameworkCore.Tests;

/// <summary>
/// Retries after an edit (ADR 0011) and commit-ordered outbox claims (ADR 0009), on every database provider.
/// </summary>
public abstract class EfRetryAndOrderingTestsBase(EfStoreFixture fixture) : IDisposable
{
    private const string Tenant = "t1";
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly EfStoreFixture _fixture = fixture;

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [SkippableFact]
    public async Task Retry_AfterAnEdit_ReturnsTheMessageButTheEditedTextIsAConflict()
    {
        var conversation = await CreateConversationAsync();
        var sent = (await SendAsync(conversation, "c1", "hello")).Message!;
        await _fixture.CreateStore().EditMessageAsync(
            new(Tenant, "alice", conversation, sent.Id, "hello, edited", Guid.NewGuid(), new SogaIdGenerator(), Now, null, false),
            default);

        var retry = await SendAsync(conversation, "c1", "hello");
        var editedText = await SendAsync(conversation, "c1", "hello, edited");

        Assert.Equal((AppendStatus.Duplicate, sent.Id, "hello, edited"), (retry.Status, retry.Message!.Id, retry.Message.Content));
        Assert.Equal(AppendStatus.IdempotencyConflict, editedText.Status);
    }

    [SkippableFact]
    public async Task Delete_ErasesTheFingerprintAndAnyRetryReturnsTheTombstone()
    {
        var conversation = await CreateConversationAsync();
        var sent = (await SendAsync(conversation, "c1", "secret")).Message!;

        await _fixture.CreateStore().DeleteMessageAsync(
            new(Tenant, "alice", conversation, sent.Id, true, true, Guid.NewGuid(), Now),
            default);

        await using var context = _fixture.CreateContext();
        var row = await context.Set<MessageRow>().SingleAsync(m => m.Id == sent.Id);
        Assert.Equal((string.Empty, (string?)null), (row.Content, row.ContentFingerprint));
        Assert.Equal(AppendStatus.Duplicate, (await SendAsync(conversation, "c1", "anything")).Status);
    }

    [SkippableFact]
    public async Task Fingerprint_IsStoredForEveryNewMessage()
    {
        var conversation = await CreateConversationAsync();
        var sent = (await SendAsync(conversation, "c1", "hello")).Message!;

        await using var context = _fixture.CreateContext();
        var row = await context.Set<MessageRow>().SingleAsync(m => m.Id == sent.Id);
        Assert.Equal(SogaText.Fingerprint("hello"), row.ContentFingerprint);
    }

    [SkippableFact]
    public async Task OutboxClaim_WhenEventsShareATimestamp_ReturnsThemInCommitOrderNotIdOrder()
    {
        var conversationId = Guid.NewGuid();

        // The later event gets the smaller ID, so ordering by ID would publish the message before its conversation.
        await _fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new(Tenant, "alice", "bob", null, conversationId, Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff"), Now), default);
        await _fixture.CreateStore().AppendMessageAsync(
            new(Tenant, "alice", conversationId, "c1", "hi", Guid.NewGuid(), Guid.Parse("00000000-0000-7000-8000-000000000001"), Now), default);

        var claimed = await _fixture.CreateStore().ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default);

        Assert.Equal([SogaEventTypes.ConversationCreated, SogaEventTypes.MessageCreated], claimed.Select(c => c.EventType));
    }

    [SkippableFact]
    public async Task OutboxClaim_StillPutsOlderEventsFirstAcrossTenants()
    {
        await _fixture.CreateStore().GetOrCreateDirectConversationAsync(new("t2", "alice", "bob", null, Guid.NewGuid(), Guid.NewGuid(), Now.AddSeconds(5)), default);
        await _fixture.CreateStore().GetOrCreateDirectConversationAsync(new("t1", "alice", "bob", null, Guid.NewGuid(), Guid.NewGuid(), Now), default);

        var claimed = await _fixture.CreateStore().ClaimOutboxAsync(Now.AddMinutes(1), 10, TimeSpan.FromSeconds(30), default);

        Assert.Equal(["t1", "t2"], claimed.Select(c => c.TenantId));
    }

    private async Task<Guid> CreateConversationAsync()
    {
        var outcome = await _fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new(Tenant, "alice", "bob", null, Guid.NewGuid(), Guid.NewGuid(), Now),
            default);
        return outcome.Conversation.Id;
    }

    private Task<AppendMessageOutcome> SendAsync(Guid conversationId, string clientId, string text) =>
        _fixture.CreateStore().AppendMessageAsync(
            new(Tenant, "alice", conversationId, clientId, text, Guid.NewGuid(), Guid.NewGuid(), Now),
            default);
}
