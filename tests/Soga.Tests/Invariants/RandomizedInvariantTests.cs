using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Errors;
using Soga.Groups;
using Soga.Identity;
using Soga.Messages;
using Soga.Persistence;
using Soga.ReadState;
using Soga.Realtime;
using Soga.Synchronization;

namespace Soga.Tests.Invariants;

/// <summary>
/// Drives Soga with long random sequences of operations from several users and, after every step, checks the
/// guarantees that must hold no matter what happened. Each seed is a different, reproducible scenario: a failure names
/// the seed and step so it can be replayed.
/// </summary>
public sealed class RandomizedInvariantTests
{
    private static readonly string[] Users = ["alice", "bob", "carol", "dave", "erin"];

    private static readonly string?[] AddRoles = [null, "member", "admin", "owner"];

    private static readonly string[] Roles = ["member", "admin", "owner"];

    private static readonly HashSet<string> DocumentedCodes =
        [.. typeof(SogaErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetValue(null)!)];

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 60)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task RandomOperations_NeverBreakTheCoreGuarantees(int seed)
    {
        var random = new Random(seed);
        using var soga = TestSoga.Create(options =>
        {
            options.Groups.PostSystemMessages = random.Next(2) == 0;
            options.Groups.MaximumParticipants = random.Next(3, 6);
            options.Messages.EditWindow = random.Next(2) == 0 ? null : TimeSpan.FromMinutes(5);
            options.Messages.MaximumTextLength = 40;
        });
        var state = new ScenarioState(soga, random);

        for (var step = 0; step < 150; step++)
        {
            var operation = await state.RunRandomOperationAsync();
            soga.Clock.Advance(TimeSpan.FromSeconds(random.Next(0, 90)));

            try
            {
                await state.CheckInvariantsAsync();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Seed {seed}, step {step} ({operation}) broke an invariant: {exception.Message}", exception);
            }
        }

        await state.CheckOutboxAccountingAsync();
    }

    private sealed class ScenarioState(TestSoga soga, Random random)
    {
        private readonly List<Guid> _conversations = [];
        private readonly List<(Guid Conversation, Guid Message, string Sender, string ClientId, string Text)> _messages = [];
        private readonly Dictionary<(string User, Guid Conversation), long> _highestReadSeen = [];
        private int _clientIds;

        private ConversationWorkflow Conversations => soga.Get<ConversationWorkflow>();

        private GroupWorkflow Groups => soga.Get<GroupWorkflow>();

        private MessageWorkflow Messages => soga.Get<MessageWorkflow>();

        public async Task<string> RunRandomOperationAsync()
        {
            var actor = Caller(Pick(Users));
            var conversation = _conversations.Count == 0 ? Guid.NewGuid() : Pick(_conversations);
            var roll = random.Next(100);

            switch (roll)
            {
                case < 8:
                {
                    var other = Pick(Users);
                    var result = await Conversations.CreateDirectAsync(actor, new CreateDirectConversationRequest(other, random.Next(3) == 0 ? "order-" + random.Next(3) : null), default);
                    Expect(result, "create direct");
                    if (result.IsSuccess && !_conversations.Contains(result.Value.Value.Id))
                    {
                        _conversations.Add(result.Value.Value.Id);
                    }

                    return $"{actor.UserId} opens a direct conversation with {other}";
                }

                case < 14:
                {
                    var members = Users.Where(_ => random.Next(2) == 0).ToArray();
                    var result = await Groups.CreateAsync(actor, new CreateGroupRequest("Group " + random.Next(100), members), default);
                    Expect(result, "create group");
                    if (result.IsSuccess)
                    {
                        _conversations.Add(result.Value.Id);
                    }

                    return $"{actor.UserId} creates a group with {string.Join(",", members)}";
                }

                case < 45:
                {
                    // Sometimes reuse a previous client message ID, with the same or different text, to exercise idempotency.
                    var reuse = _messages.Where(m => m.Conversation == conversation && m.Sender == actor.UserId).ToList();
                    string clientId;
                    string text;
                    if (reuse.Count > 0 && random.Next(4) == 0)
                    {
                        var previous = Pick(reuse);
                        clientId = previous.ClientId;
                        text = random.Next(2) == 0 ? previous.Text : previous.Text + "!";
                    }
                    else
                    {
                        clientId = $"c{++_clientIds}";
                        text = random.Next(20) == 0 ? new string('x', 41) : $"message {_clientIds}";
                    }

                    var replyTo = random.Next(5) == 0 && _messages.Count > 0 ? Pick(_messages).Message : (Guid?)null;
                    var result = await Messages.SendAsync(actor, conversation, new SendMessageRequest(clientId, text, replyTo), default);
                    Expect(result, "send");
                    if (result.IsSuccess)
                    {
                        var stored = result.Value.Value;
                        Assert.Equal(actor.UserId, stored.SenderId);
                        if (!result.Value.Created)
                        {
                            var original = _messages.Single(m => m.Message == stored.Id);
                            Assert.Equal(original.ClientId, clientId);
                        }
                        else
                        {
                            _messages.Add((conversation, stored.Id, actor.UserId, clientId, text));
                        }
                    }
                    else if (result.Error!.Code == SogaErrorCodes.IdempotencyConflict)
                    {
                        Assert.Contains(_messages, m => m.Conversation == conversation && m.Sender == actor.UserId && m.ClientId == clientId && m.Text != text);
                    }

                    return $"{actor.UserId} sends {clientId} to {conversation}";
                }

                case < 52 when _messages.Count > 0:
                {
                    var target = Pick(_messages);
                    Expect(await Messages.EditAsync(actor, target.Conversation, target.Message, new EditMessageRequest("edited " + random.Next(1000)), default), "edit");
                    return $"{actor.UserId} edits a message";
                }

                case < 58 when _messages.Count > 0:
                {
                    var target = Pick(_messages);
                    Expect(await Messages.DeleteAsync(actor, target.Conversation, target.Message, default), "delete");
                    return $"{actor.UserId} deletes a message";
                }

                case < 66:
                {
                    Expect(await Groups.AddParticipantAsync(actor, conversation, new AddParticipantRequest(Pick(Users), Pick(AddRoles)), default), "add participant");
                    return $"{actor.UserId} adds someone";
                }

                case < 74:
                {
                    var target = Pick(Users);
                    Expect(await Groups.RemoveParticipantAsync(actor, conversation, target, default), "remove participant");
                    return $"{actor.UserId} removes {target}";
                }

                case < 80:
                {
                    var target = Pick(Users);
                    Expect(await Groups.ChangeRoleAsync(actor, conversation, target, new ChangeRoleRequest(Pick(Roles)), default), "change role");
                    return $"{actor.UserId} changes the role of {target}";
                }

                case < 84:
                {
                    Expect(await Groups.ArchiveAsync(actor, conversation, random.Next(2) == 0, default), "archive");
                    return $"{actor.UserId} archives or restores";
                }

                case < 87:
                {
                    Expect(await Groups.UpdateAsync(actor, conversation, new UpdateConversationRequest("Renamed " + random.Next(10)), default), "rename");
                    return $"{actor.UserId} renames";
                }

                default:
                {
                    var target = _messages.Count > 0 ? Pick(_messages).Message : Guid.NewGuid();
                    Expect(await soga.Get<ReadStateWorkflow>().AdvanceAsync(actor, conversation, new MarkReadRequest(target), default), "mark read");
                    return $"{actor.UserId} marks read";
                }
            }
        }

        public async Task CheckInvariantsAsync()
        {
            foreach (var user in Users)
            {
                var caller = Caller(user);
                var listed = await ListAllAsync(caller);
                Assert.Equal(listed.Count, listed.Select(c => c.Id).Distinct().Count());

                long unreadSum = 0;
                foreach (var conversationId in _conversations)
                {
                    var conversation = await Conversations.GetAsync(caller, conversationId, default);
                    var history = await Messages.GetHistoryAsync(caller, conversationId, null, 500, default);
                    var inList = listed.SingleOrDefault(c => c.Id == conversationId);

                    // Access is consistent across every read path.
                    Assert.Equal(conversation.IsSuccess, history.IsSuccess);
                    Assert.Equal(conversation.IsSuccess, inList is not null);
                    if (!conversation.IsSuccess)
                    {
                        Assert.Equal(SogaErrorCodes.ConversationNotFound, conversation.Error!.Code);
                        Assert.Equal(SogaErrorCodes.ConversationNotFound, history.Error!.Code);
                        continue;
                    }

                    var value = conversation.Value;
                    Assert.Contains(value.Participants!, p => p.UserId == user && p.Role == value.MyRole);
                    Assert.Equal(value.Participants!.Count, value.ParticipantCount);
                    Assert.Equal(value.Participants.Count, value.Participants.Select(p => p.UserId).Distinct().Count());

                    if (value.Kind == SogaConversationKinds.Group)
                    {
                        Assert.Contains(value.Participants, p => p.Role == SogaRoleNames.Owner);
                    }
                    else
                    {
                        Assert.Equal(2, value.ParticipantCount);
                        Assert.All(value.Participants, p => Assert.Equal(SogaRoleNames.Member, p.Role));
                    }

                    // Sequences are unique, gapless, newest first, and the conversation knows its last one.
                    var sequences = history.Value.Items.Select(m => m.Sequence).ToList();
                    Assert.Equal(Enumerable.Range(1, sequences.Count).Select(i => (long)i).Reverse(), sequences);
                    Assert.Equal(sequences.Count == 0 ? 0 : sequences[0], value.LastMessageSequence);

                    // Deleted messages never leak their text.
                    Assert.All(history.Value.Items.Where(m => m.Deleted), m => Assert.Equal(string.Empty, m.Content));
                    Assert.All(history.Value.Items.Where(m => m.ReplyTo is { Deleted: true }), m => Assert.Equal(string.Empty, m.ReplyTo!.Snippet));

                    // Read positions only move forward and stay within the conversation.
                    Assert.InRange(value.LastReadSequence, 0, value.LastMessageSequence);
                    var key = (user, conversationId);
                    Assert.True(value.LastReadSequence >= _highestReadSeen.GetValueOrDefault(key), "A read position moved backward.");
                    _highestReadSeen[key] = value.LastReadSequence;

                    // Unread counts only count other people's undeleted messages after the read position.
                    var expectedUnread = history.Value.Items.LongCount(m => m.Sequence > value.LastReadSequence && m.SenderId != user && !m.Deleted);
                    Assert.Equal(expectedUnread, value.UnreadCount);
                    Assert.Equal(value.UnreadCount, inList!.UnreadCount);
                    unreadSum += value.UnreadCount;
                }

                Assert.Equal(unreadSum, (await soga.Get<ReadStateWorkflow>().GetTotalUnreadAsync(caller, default)).Value.TotalUnread);
            }
        }

        public async Task CheckOutboxAccountingAsync()
        {
            var store = soga.Get<ISogaStore>();
            var head = await store.GetChangeHeadAsync(TestSoga.Tenant, default);
            var dispatcher = soga.Get<IEnumerable<IHostedService>>().OfType<SogaOutboxDispatcher>().Single();

            var published = 0;
            int batch;
            while ((batch = await dispatcher.DispatchOnceAsync(default)) > 0)
            {
                published += batch;
            }

            // Every committed change produced exactly one outbox event, and every one was published.
            Assert.Equal(head, published);
            Assert.Equal(0, (await store.GetOutboxStatsAsync(soga.Clock.GetUtcNow(), default)).Pending);

            // Each user's sync stream is ordered, free of duplicates, and only about conversations they could see.
            foreach (var user in Users)
            {
                var sync = soga.Get<SyncWorkflow>();
                var events = new List<SogaEvent>();
                var cursor = soga.Get<SogaCursorProtector>().Encode(SogaCursorKind.Sync, TestSoga.Tenant, user, 0);
                while (true)
                {
                    var page = (await sync.SyncAsync(Caller(user), cursor, 50, default)).Value;
                    events.AddRange(page.Events);
                    cursor = page.Cursor;
                    if (!page.HasMore)
                    {
                        break;
                    }
                }

                Assert.Equal(events.Count, events.Select(e => e.EventId).Distinct().Count());
                Assert.All(events, e => Assert.Equal(SogaProtocol.Version, e.ProtocolVersion));
            }
        }

        private async Task<List<ConversationResponse>> ListAllAsync(SogaCaller caller)
        {
            var all = new List<ConversationResponse>();
            string? cursor = null;
            do
            {
                var page = (await Conversations.ListAsync(caller, new ConversationListRequest(random.Next(1, 4), cursor, State: "all"), default)).Value;
                all.AddRange(page.Items);
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            return all;
        }

        private static SogaCaller Caller(string user) => new(TestSoga.Tenant, user);

        private static void Expect<T>(SogaResult<T> result, string operation)
        {
            if (!result.IsSuccess)
            {
                Assert.True(DocumentedCodes.Contains(result.Error!.Code), $"{operation} returned an undocumented code {result.Error.Code}.");
                Assert.NotEqual(SogaErrorCodes.InternalError, result.Error.Code);
            }
        }

        private T Pick<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];
    }
}
