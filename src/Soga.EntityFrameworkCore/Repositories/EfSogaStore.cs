using Microsoft.EntityFrameworkCore;
using Soga.Contracts;
using Soga.Conversations;
using Soga.EntityFrameworkCore.Configuration;
using Soga.EntityFrameworkCore.Entities;
using Soga.Groups;
using Soga.Messages;
using Soga.Persistence;
using Soga.Synchronization;

namespace Soga.EntityFrameworkCore.Repositories;

/// <summary>
/// The Entity Framework Core implementation of <see cref="ISogaStore"/>, which stores Soga data in the host's
/// relational database.
/// </summary>
/// <typeparam name="TContext">The host database context, whose model includes the Soga tables.</typeparam>
/// <remarks>
/// <para>
/// Every write commits the entity change, its durable sync change, and its outbox event in one transaction.
/// Ordering, idempotency, and uniqueness are enforced by database constraints; application prechecks only make the
/// common path cheap.
/// </para>
/// <para>
/// Group changes lock the conversation row and are decided by the same pure <see cref="GroupRules"/> that the
/// in-memory store uses.
/// </para>
/// <para>
/// The class is split across files by concern: conversations and groups here, and messages, transactions, sync, and
/// outbox in their own partial files.
/// </para>
/// </remarks>
internal sealed partial class EfSogaStore<TContext> : ISogaStore
    where TContext : DbContext
{
    /// <summary>
    /// The host database context.
    /// </summary>
    private readonly TContext _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="EfSogaStore{TContext}"/> class.
    /// </summary>
    /// <param name="db">The host database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The context's model is missing the Soga tables or is unsafe.</exception>
    public EfSogaStore(TContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        SogaEfModelCheck.Validate(db);
        _db = db;
    }

    /// <inheritdoc />
    public bool IsDurable => true;

    /// <inheritdoc />
    public Task<DirectConversationOutcome> GetOrCreateDirectConversationAsync(
        CreateDirectConversationCommand command,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            async token =>
            {
                var key = DirectConversationKey.Create(command.UserId, command.OtherUserId, command.ExternalReference);

                var existingId = await _db.Set<ConversationRow>()
                    .AsNoTracking()
                    .Where(c => c.TenantId == command.TenantId && c.DirectKey == key)
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefaultAsync(token);

                if (existingId is { } id)
                {
                    var existing = await LoadOneAsync(command.TenantId, command.UserId, id, token);
                    return new DirectConversationOutcome(existing!, false);
                }

                // Allocate the change sequence before adding entities: the first write for a tenant saves its counter row.
                var changeSequence = await AllocateChangeSequenceAsync(command.TenantId, token);
                var participants = new[] { command.UserId, command.OtherUserId }.Order(StringComparer.Ordinal).ToArray();

                _db.Add(new ConversationRow
                {
                    Id = command.ConversationId,
                    TenantId = command.TenantId,
                    DirectKey = key,
                    Kind = SogaConversationKinds.Direct,
                    ExternalReference = command.ExternalReference,
                    MetadataJson = command.MetadataJson,
                    CreatedAt = command.Now,
                    LastActivityAt = command.Now,
                });

                foreach (var participant in participants)
                {
                    _db.Add(new ParticipantRow
                    {
                        TenantId = command.TenantId,
                        ConversationId = command.ConversationId,
                        UserId = participant,
                        Role = SogaRole.Member,
                        JoinedAt = command.Now,
                    });
                }

                Enqueue(
                    command.TenantId,
                    changeSequence,
                    SogaEventFactory.ConversationCreated(
                        command.EventId,
                        command.ConversationId,
                        participants,
                        command.ExternalReference,
                        command.Now));

                await _db.SaveChangesAsync(token);

                var created = await LoadOneAsync(command.TenantId, command.UserId, command.ConversationId, token);
                return new DirectConversationOutcome(created!, true);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<ConversationRecord> CreateGroupAsync(CreateGroupCommand command, CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            async token =>
            {
                var changeSequence = await AllocateChangeSequenceAsync(command.TenantId, token);
                var others = command.OtherParticipantIds
                    .Distinct(StringComparer.Ordinal)
                    .Where(id => !string.Equals(id, command.UserId, StringComparison.Ordinal))
                    .ToList();
                var everyone = new[] { command.UserId }.Concat(others).Order(StringComparer.Ordinal).ToArray();

                _db.Add(new ConversationRow
                {
                    Id = command.ConversationId,
                    TenantId = command.TenantId,
                    DirectKey = $"g:{command.ConversationId:N}",
                    Kind = SogaConversationKinds.Group,
                    Title = command.Title,
                    ExternalReference = command.ExternalReference,
                    MetadataJson = command.MetadataJson,
                    CreatedAt = command.Now,
                    LastActivityAt = command.Now,
                });

                _db.Add(NewParticipant(command.TenantId, command.ConversationId, command.UserId, SogaRole.Owner, command.Now, 0));
                foreach (var other in others)
                {
                    _db.Add(NewParticipant(command.TenantId, command.ConversationId, other, SogaRole.Member, command.Now, 0));
                }

                Enqueue(
                    command.TenantId,
                    changeSequence,
                    SogaEventFactory.ConversationCreated(
                        command.EventId,
                        command.ConversationId,
                        everyone,
                        command.ExternalReference,
                        command.Now,
                        SogaConversationKinds.Group,
                        command.Title));

                await _db.SaveChangesAsync(token);
                return (await LoadOneAsync(command.TenantId, command.UserId, command.ConversationId, token))!;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<GroupMutationOutcome> MutateGroupAsync(GroupMutationCommand command, CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            async token =>
            {
                // A no-op UPDATE takes the conversation row lock and holds it until commit, so concurrent membership,
                // role, and ownership changes on one group run one at a time and each sees the previous result.
                var locked = await _db.Set<ConversationRow>()
                    .Where(c => c.TenantId == command.TenantId && c.Id == command.ConversationId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSequence, c => c.LastSequence), token);
                if (locked == 0)
                {
                    return new GroupMutationOutcome(GroupError.NotFound, false, null);
                }

                var conversation = await _db.Set<ConversationRow>().AsNoTracking()
                    .SingleAsync(c => c.TenantId == command.TenantId && c.Id == command.ConversationId, token);
                var participants = await _db.Set<ParticipantRow>().AsNoTracking()
                    .Where(p => p.TenantId == command.TenantId && p.ConversationId == command.ConversationId)
                    .ToListAsync(token);

                var snapshot = new GroupSnapshot(
                    conversation.Kind == SogaConversationKinds.Group,
                    conversation.ArchivedAt is not null,
                    conversation.Title,
                    conversation.MetadataJson,
                    [.. participants.Select(p => new ParticipantSnapshot(p.UserId, p.Role, p.LeftAt is null))]);

                var decision = GroupRules.Decide(snapshot, command.ActorId, command.Mutation, command.MaximumParticipants);
                if (decision.Error != GroupError.None)
                {
                    return new GroupMutationOutcome(decision.Error, false, null);
                }

                if (!decision.Changed)
                {
                    return new GroupMutationOutcome(
                        GroupError.None,
                        false,
                        await LoadOneAsync(command.TenantId, command.ActorId, command.ConversationId, token));
                }

                await ApplyMutationAsync(command, conversation, participants, token);

                var first = true;
                foreach (var plan in decision.Events)
                {
                    var eventId = first ? command.EventId : command.Ids.NewId();
                    first = false;
                    var changeSequence = await AllocateChangeSequenceAsync(command.TenantId, token);
                    Enqueue(
                        command.TenantId,
                        changeSequence,
                        SogaEventFactory.FromPlan(eventId, command.ConversationId, plan, command.Now),
                        plan.TargetUserId);

                    if (command.PostSystemMessage && plan.Data is ParticipantChangedData participantChange)
                    {
                        await AddSystemMessageAsync(
                            command,
                            SystemMessageContent.ForParticipantChange(participantChange),
                            token);
                    }
                }

                await _db.SaveChangesAsync(token);

                // The actor may have just removed themselves: then there is nothing left they may see.
                return new GroupMutationOutcome(
                    GroupError.None,
                    true,
                    await LoadOneAsync(command.TenantId, command.ActorId, command.ConversationId, token));
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<ConversationAccess?> GetAccessAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var access = await (
            from participant in _db.Set<ParticipantRow>().AsNoTracking()
            join conversation in _db.Set<ConversationRow>().AsNoTracking()
                on new { participant.TenantId, Id = participant.ConversationId }
                equals new { conversation.TenantId, conversation.Id }
            where participant.TenantId == tenantId &&
                participant.UserId == userId &&
                participant.ConversationId == conversationId &&
                participant.LeftAt == null
            select new
            {
                conversation.Kind,
                participant.Role,
                Archived = conversation.ArchivedAt != null,
                conversation.ExternalReference,
            }).FirstOrDefaultAsync(cancellationToken);

        return access is null ? null : new ConversationAccess(access.Kind, access.Role, access.Archived, access.ExternalReference);
    }

    /// <inheritdoc />
    public Task<ConversationRecord?> GetConversationAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken) => LoadOneAsync(tenantId, userId, conversationId, cancellationToken);

    /// <inheritdoc />
    public async Task<ConversationPage> ListConversationsAsync(ListConversationsQuery query, CancellationToken cancellationToken)
    {
        var (items, hasMore) = await LoadConversationsAsync(
            query.TenantId,
            query.UserId,
            conversationId: null,
            query.Kind,
            query.State,
            query.ExternalReference,
            query.AfterActivity,
            query.SkipTies,
            query.Limit,
            allParticipants: false,
            cancellationToken);
        return new ConversationPage(items, hasMore);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListActiveConversationIdsAsync(
        string tenantId,
        string userId,
        int limit,
        CancellationToken cancellationToken)
    {
        var ids = await (
            from participant in _db.Set<ParticipantRow>().AsNoTracking()
            join conversation in _db.Set<ConversationRow>().AsNoTracking()
                on new { participant.TenantId, Id = participant.ConversationId }
                equals new { conversation.TenantId, conversation.Id }
            where participant.TenantId == tenantId && participant.UserId == userId && participant.LeftAt == null
            orderby conversation.LastActivityAt descending, conversation.Id descending
            select conversation.Id).Take(limit).ToListAsync(cancellationToken);
        return ids;
    }

    /// <inheritdoc />
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        await _db.Database.CanConnectAsync(cancellationToken);

    /// <summary>
    /// Creates a new participant row.
    /// </summary>
    /// <param name="tenantId">The tenant of the conversation.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="userId">The participant's user ID.</param>
    /// <param name="role">The participant's role.</param>
    /// <param name="now">The time the participant joins.</param>
    /// <param name="lastReadSequence">The initial read position.</param>
    /// <returns>The new, untracked row.</returns>
    private static ParticipantRow NewParticipant(
        string tenantId,
        Guid conversationId,
        string userId,
        SogaRole role,
        DateTimeOffset now,
        long lastReadSequence) => new()
    {
        TenantId = tenantId,
        ConversationId = conversationId,
        UserId = userId,
        Role = role,
        JoinedAt = now,
        LastReadSequence = lastReadSequence,
    };

    /// <summary>
    /// Writes an accepted group change to the tracked rows.
    /// </summary>
    /// <param name="command">The group change.</param>
    /// <param name="conversation">The locked conversation row.</param>
    /// <param name="participants">Every current and former participant row of the group.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>Only call this after <see cref="GroupRules.Decide"/> has accepted the change.</remarks>
    private async Task ApplyMutationAsync(
        GroupMutationCommand command,
        ConversationRow conversation,
        List<ParticipantRow> participants,
        CancellationToken cancellationToken)
    {
        var tenantId = command.TenantId;
        var conversationId = command.ConversationId;

        switch (command.Mutation)
        {
            case AddParticipantMutation add:
                if (participants.Exists(p => p.UserId == add.UserId))
                {
                    // Re-adding someone who left reactivates their row. New members start caught up: history is
                    // visible to them, but it does not arrive as a wall of unread messages.
                    await _db.Set<ParticipantRow>()
                        .Where(p => p.TenantId == tenantId && p.ConversationId == conversationId && p.UserId == add.UserId)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(p => p.LeftAt, (DateTimeOffset?)null)
                                .SetProperty(p => p.Role, add.Role)
                                .SetProperty(p => p.JoinedAt, command.Now)
                                .SetProperty(p => p.LastReadSequence, conversation.LastSequence),
                            cancellationToken);
                }
                else
                {
                    _db.Add(NewParticipant(tenantId, conversationId, add.UserId, add.Role, command.Now, conversation.LastSequence));
                }

                break;

            case RemoveParticipantMutation remove:
                await _db.Set<ParticipantRow>()
                    .Where(p => p.TenantId == tenantId && p.ConversationId == conversationId && p.UserId == remove.UserId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeftAt, (DateTimeOffset?)command.Now), cancellationToken);
                break;

            case ChangeRoleMutation change:
                await _db.Set<ParticipantRow>()
                    .Where(p => p.TenantId == tenantId && p.ConversationId == conversationId && p.UserId == change.UserId && p.LeftAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Role, change.Role), cancellationToken);
                break;

            case UpdateConversationMutation update:
                var rows = _db.Set<ConversationRow>().Where(c => c.TenantId == tenantId && c.Id == conversationId);
                if (update.SetTitle)
                {
                    await rows.ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, update.Title), cancellationToken);
                }

                if (update.SetMetadata)
                {
                    await rows.ExecuteUpdateAsync(s => s.SetProperty(c => c.MetadataJson, update.MetadataJson), cancellationToken);
                }

                break;

            case ArchiveMutation archive:
                await _db.Set<ConversationRow>()
                    .Where(c => c.TenantId == tenantId && c.Id == conversationId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(c => c.ArchivedAt, archive.Archived ? (DateTimeOffset?)command.Now : null),
                        cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Adds a system message, with its change and outbox rows, to the current transaction.
    /// </summary>
    /// <param name="command">The group change that the message describes.</param>
    /// <param name="content">The system message body.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task AddSystemMessageAsync(GroupMutationCommand command, string content, CancellationToken cancellationToken)
    {
        var messageId = command.Ids.NewId();
        var sequence = await AllocateMessageSequenceAsync(command.TenantId, command.ConversationId, command.Now, cancellationToken);
        var changeSequence = await AllocateChangeSequenceAsync(command.TenantId, cancellationToken);
        var clientMessageId = $"sys:{messageId:N}";

        _db.Add(new MessageRow
        {
            Id = messageId,
            TenantId = command.TenantId,
            ConversationId = command.ConversationId,
            Sequence = sequence,
            SenderId = SogaSystemSender.UserId,
            ClientMessageId = clientMessageId,
            Kind = SogaMessageKinds.System,
            Content = content,
            ContentFingerprint = SogaText.Fingerprint(content),
            CreatedAt = command.Now,
        });

        var record = new MessageRecord(
            messageId,
            command.ConversationId,
            sequence,
            SogaSystemSender.UserId,
            clientMessageId,
            content,
            command.Now,
            SogaMessageKinds.System);
        Enqueue(command.TenantId, changeSequence, SogaEventFactory.MessageCreated(command.Ids.NewId(), record, command.Now));
    }

    /// <summary>
    /// Loads one conversation, with its participants, as seen by one of its active participants.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The viewing user.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the conversation, or
    /// <see langword="null"/> when the user is not an active participant.
    /// </returns>
    private async Task<ConversationRecord?> LoadOneAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var (items, _) = await LoadConversationsAsync(
            tenantId,
            userId,
            conversationId,
            kind: null,
            ConversationStateFilter.All,
            reference: null,
            afterActivity: null,
            skip: 0,
            limit: 1,
            allParticipants: true,
            cancellationToken);
        return items.Count == 0 ? null : items[0];
    }

    /// <summary>
    /// Loads the conversations a user participates in, most recent activity first.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The viewing user.</param>
    /// <param name="conversationId">Load only this conversation, or <see langword="null"/> for all.</param>
    /// <param name="kind">An optional kind filter.</param>
    /// <param name="state">The archive state filter.</param>
    /// <param name="reference">An optional external reference filter.</param>
    /// <param name="afterActivity">Continue at or before this activity time, or <see langword="null"/> for the first page.</param>
    /// <param name="skip">How many conversations with exactly <paramref name="afterActivity"/> to skip.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="allParticipants">
    /// <see langword="true"/> to load the participant list of every conversation; otherwise, only direct conversations
    /// get one.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the page and whether more follow.
    /// </returns>
    /// <remarks>
    /// The number of queries is fixed regardless of page size: one projection with unread counts, then batched lookups
    /// for participant counts, participant lists, and last messages.
    /// </remarks>
    private async Task<(List<ConversationRecord> Items, bool HasMore)> LoadConversationsAsync(
        string tenantId,
        string userId,
        Guid? conversationId,
        string? kind,
        ConversationStateFilter state,
        string? reference,
        DateTimeOffset? afterActivity,
        int skip,
        int limit,
        bool allParticipants,
        CancellationToken cancellationToken)
    {
        var conversations = _db.Set<ConversationRow>().AsNoTracking().Where(c => c.TenantId == tenantId);
        if (conversationId is { } onlyId)
        {
            conversations = conversations.Where(c => c.Id == onlyId);
        }

        if (kind is not null)
        {
            conversations = conversations.Where(c => c.Kind == kind);
        }

        if (reference is not null)
        {
            conversations = conversations.Where(c => c.ExternalReference == reference);
        }

        if (state == ConversationStateFilter.Active)
        {
            conversations = conversations.Where(c => c.ArchivedAt == null);
        }
        else if (state == ConversationStateFilter.Archived)
        {
            conversations = conversations.Where(c => c.ArchivedAt != null);
        }

        if (afterActivity is { } after)
        {
            conversations = conversations.Where(c => c.LastActivityAt <= after);
        }

        var summaries =
            from participant in _db.Set<ParticipantRow>().AsNoTracking()
            join conversation in conversations
                on new { participant.TenantId, Id = participant.ConversationId }
                equals new { conversation.TenantId, conversation.Id }
            where participant.TenantId == tenantId && participant.UserId == userId && participant.LeftAt == null
            orderby conversation.LastActivityAt descending, conversation.Id descending
            select new
            {
                conversation.Id,
                conversation.Kind,
                conversation.Title,
                conversation.ExternalReference,
                conversation.MetadataJson,
                conversation.CreatedAt,
                conversation.LastActivityAt,
                conversation.ArchivedAt,
                conversation.LastSequence,
                participant.LastReadSequence,
                participant.Role,
                Unread = _db.Set<MessageRow>().Count(m =>
                    m.TenantId == conversation.TenantId &&
                    m.ConversationId == conversation.Id &&
                    m.Sequence > participant.LastReadSequence &&
                    m.SenderId != userId &&
                    m.DeletedAt == null),
            };

        // Fetch one extra row to learn whether another page exists.
        var rows = await summaries.Skip(skip).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        rows = [.. rows.Take(limit)];
        if (rows.Count == 0)
        {
            return ([], false);
        }

        var ids = rows.Select(r => r.Id).ToList();

        var counts = await _db.Set<ParticipantRow>().AsNoTracking()
            .Where(p => p.TenantId == tenantId && ids.Contains(p.ConversationId) && p.LeftAt == null)
            .GroupBy(p => p.ConversationId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Id, g => g.Count, cancellationToken);

        var listIds = rows.Where(r => allParticipants || r.Kind == SogaConversationKinds.Direct).Select(r => r.Id).ToList();
        var participantRows = listIds.Count == 0
            ? []
            : await _db.Set<ParticipantRow>().AsNoTracking()
                .Where(p => p.TenantId == tenantId && listIds.Contains(p.ConversationId) && p.LeftAt == null)
                .Select(p => new { p.ConversationId, p.UserId, p.Role, p.JoinedAt })
                .ToListAsync(cancellationToken);
        var participantsByConversation = participantRows
            .GroupBy(p => p.ConversationId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ParticipantInfo>)[.. g
                    .OrderBy(p => p.UserId, StringComparer.Ordinal)
                    .Select(p => new ParticipantInfo(p.UserId, p.Role, p.JoinedAt))]);

        var lastMessages = await (
            from message in _db.Set<MessageRow>().AsNoTracking()
            join conversation in _db.Set<ConversationRow>().AsNoTracking()
                on new { message.TenantId, Id = message.ConversationId }
                equals new { conversation.TenantId, conversation.Id }
            where conversation.TenantId == tenantId && ids.Contains(conversation.Id) && message.Sequence == conversation.LastSequence
            select new
            {
                message.Id,
                message.ConversationId,
                message.Sequence,
                message.SenderId,
                message.Kind,
                message.Content,
                message.CreatedAt,
                Deleted = message.DeletedAt != null,
            }).ToListAsync(cancellationToken);
        var lastByConversation = lastMessages.ToDictionary(
            m => m.ConversationId,
            m => new MessageSummary(m.Id, m.Sequence, m.SenderId, m.Kind, SogaText.Snippet(m.Content), m.CreatedAt, m.Deleted));

        var records = rows.Select(r => new ConversationRecord(
            r.Id,
            r.Kind,
            r.Title,
            r.ExternalReference,
            r.MetadataJson,
            participantsByConversation.GetValueOrDefault(r.Id, []),
            counts.GetValueOrDefault(r.Id),
            r.CreatedAt,
            r.LastActivityAt,
            r.ArchivedAt,
            r.LastSequence,
            r.LastReadSequence,
            r.Unread,
            r.Role,
            lastByConversation.GetValueOrDefault(r.Id))).ToList();

        return (records, hasMore);
    }
}
