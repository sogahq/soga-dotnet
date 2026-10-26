using Microsoft.Extensions.DependencyInjection;
using Soga.Authorization;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Errors;
using Soga.Groups;
using Soga.Messages;

namespace Soga.Tests.Authorization;

/// <summary>
/// Runs every protected action through a host policy. A policy that denies an action must stop it, a non-participant
/// must still only ever see "not found", and the policy must receive accurate context.
/// </summary>
public sealed class HostPolicyMatrixTests
{
    /// <summary>Performs one action as the caller and returns the error code, or <see langword="null"/> on success.</summary>
    private static readonly Dictionary<SogaAction, Func<TestSoga, Identity.SogaCaller, Guid, Guid, Task<string?>>> Runners = new()
    {
        [SogaAction.CreateDirectConversation] = async (s, c, _, _) => Code(await s.Get<ConversationWorkflow>().CreateDirectAsync(c, new CreateDirectConversationRequest("erin"), default)),
        [SogaAction.CreateGroup] = async (s, c, _, _) => Code(await s.Get<GroupWorkflow>().CreateAsync(c, new CreateGroupRequest("New", ["erin"]), default)),
        [SogaAction.SendMessage] = async (s, c, g, _) => Code(await s.Get<MessageWorkflow>().SendAsync(c, g, new SendMessageRequest("x1", "hi"), default)),
        [SogaAction.EditMessage] = async (s, c, g, m) => Code(await s.Get<MessageWorkflow>().EditAsync(c, g, m, new EditMessageRequest("edited"), default)),
        [SogaAction.DeleteMessage] = async (s, c, g, m) => Code(await s.Get<MessageWorkflow>().DeleteAsync(c, g, m, default)),
        [SogaAction.AddParticipant] = async (s, c, g, _) => Code(await s.Get<GroupWorkflow>().AddParticipantAsync(c, g, new AddParticipantRequest("erin"), default)),
        [SogaAction.RemoveParticipant] = async (s, c, g, _) => Code(await s.Get<GroupWorkflow>().RemoveParticipantAsync(c, g, "carol", default)),
        [SogaAction.ChangeRole] = async (s, c, g, _) => Code(await s.Get<GroupWorkflow>().ChangeRoleAsync(c, g, "bob", new ChangeRoleRequest("admin"), default)),
        [SogaAction.UpdateConversation] = async (s, c, g, _) => Code(await s.Get<GroupWorkflow>().UpdateAsync(c, g, new UpdateConversationRequest("Renamed"), default)),
        [SogaAction.ArchiveConversation] = async (s, c, g, _) => Code(await s.Get<GroupWorkflow>().ArchiveAsync(c, g, archived: true, default)),
    };

    public static TheoryData<SogaAction> Actions => [.. Runners.Keys];

    public static TheoryData<SogaAction> ConversationActions =>
        [.. Runners.Keys.Where(a => a is not (SogaAction.CreateDirectConversation or SogaAction.CreateGroup))];

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Action_WhenAPolicyAllowsIt_Succeeds(SogaAction action)
    {
        var run = Runners[action];
        var policy = new DenyPolicy(denied: null);
        var (soga, group, message) = await ArrangeAsync(policy);
        using (soga)
        {
            Assert.Null(await run(soga, TestSoga.Alice, group, message));
            Assert.Contains(policy.Seen, c => c.Action == action);
        }
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Action_WhenAPolicyDeniesIt_FailsWithAuthorizationDenied(SogaAction action)
    {
        var run = Runners[action];
        var (soga, group, message) = await ArrangeAsync(new DenyPolicy(action));
        using (soga)
        {
            Assert.Equal(SogaErrorCodes.AuthorizationDenied, await run(soga, TestSoga.Alice, group, message));
        }
    }

    [Theory]
    [MemberData(nameof(ConversationActions))]
    public async Task Action_WhenTheCallerIsNotAParticipant_ReturnsNotFoundEvenThoughThePolicyWouldDeny(SogaAction action)
    {
        var run = Runners[action];
        var policy = new DenyPolicy(action);
        var (soga, group, message) = await ArrangeAsync(policy);
        using (soga)
        {
            Assert.Equal(SogaErrorCodes.ConversationNotFound, await run(soga, TestSoga.Dave, group, message));
            Assert.DoesNotContain(policy.Seen, c => c.UserId == "dave");
        }
    }

    [Theory]
    [MemberData(nameof(ConversationActions))]
    public async Task Action_WithoutAnyPolicy_StillRejectsNonParticipants(SogaAction action)
    {
        var run = Runners[action];
        _ = action;
        var (soga, group, message) = await ArrangeAsync(policy: null, registerPolicy: false);
        using (soga)
        {
            Assert.Equal(SogaErrorCodes.ConversationNotFound, await run(soga, TestSoga.Dave, group, message));
        }
    }

    [Fact]
    public async Task Policy_ReceivesTheTenantRoleTargetExternalReferenceAndMessage()
    {
        var policy = new DenyPolicy(denied: null);
        using var soga = TestSoga.Create(services: s => s.AddSingleton<ISogaAuthorizationPolicy>(policy));
        var group = (await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("Deal", ["bob"], "order-7"), default)).Value;
        var message = await soga.SendAsync(TestSoga.Alice, group.Id, "m1", "hi");

        await soga.Get<GroupWorkflow>().RemoveParticipantAsync(TestSoga.Alice, group.Id, "bob", default);
        await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Alice, group.Id, message.Id, default);

        var remove = policy.Seen.Single(c => c.Action == SogaAction.RemoveParticipant);
        Assert.Equal((TestSoga.Tenant, "alice", group.Id, "bob", "order-7", "owner"), (remove.TenantId, remove.UserId, remove.ConversationId, remove.TargetUserId, remove.ExternalReference, remove.Role));
        Assert.Equal(message.Id, policy.Seen.Single(c => c.Action == SogaAction.DeleteMessage).MessageId);
        Assert.Equal("order-7", policy.Seen.Single(c => c.Action == SogaAction.CreateGroup).ExternalReference);
    }

    [Fact]
    public async Task Policy_ForANewDirectConversation_ReceivesTheOtherUserAndReference()
    {
        var policy = new DenyPolicy(denied: null);
        using var soga = TestSoga.Create(services: s => s.AddSingleton<ISogaAuthorizationPolicy>(policy));

        await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest("bob", "order-9"), default);

        var context = policy.Seen.Single();
        Assert.Equal((SogaAction.CreateDirectConversation, "bob", "order-9", (Guid?)null), (context.Action, context.TargetUserId, context.ExternalReference, context.ConversationId));
    }

    [Fact]
    public async Task Policy_WhenSeveralAreRegistered_EveryOneMustAllow()
    {
        using var soga = TestSoga.Create(services: s =>
        {
            s.AddSingleton<ISogaAuthorizationPolicy>(new DenyPolicy(denied: null));
            s.AddSingleton<ISogaAuthorizationPolicy>(new DenyPolicy(SogaAction.CreateDirectConversation));
        });

        var result = await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest("bob"), default);

        Assert.Equal(SogaErrorCodes.AuthorizationDenied, result.Error?.Code);
    }

    [Fact]
    public async Task Policy_WhenDenied_WritesNothing()
    {
        using var soga = TestSoga.Create(services: s => s.AddSingleton<ISogaAuthorizationPolicy>(new DenyPolicy(SogaAction.CreateDirectConversation)));

        await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest("bob"), default);

        var list = await soga.Get<ConversationWorkflow>().ListAsync(TestSoga.Alice, new ConversationListRequest(), default);
        Assert.Empty(list.Value.Items);
    }

    private static async Task<(TestSoga Soga, Guid Group, Guid Message)> ArrangeAsync(DenyPolicy? policy, bool registerPolicy = true)
    {
        // The policy decides only after the group and message exist, so arrangement is never denied.
        var gate = new DeferredPolicy();
        var soga = registerPolicy
            ? TestSoga.Create(services: s => s.AddSingleton<ISogaAuthorizationPolicy>(gate))
            : TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob", "carol");
        var message = await soga.SendAsync(TestSoga.Alice, group.Id, "m1", "hello");
        gate.Inner = policy;
        return (soga, group.Id, message.Id);
    }

    private static string? Code<T>(SogaResult<T> result) => result.Error?.Code;

    /// <summary>Allows everything except <paramref name="denied"/>, and records what it was asked.</summary>
    private sealed class DenyPolicy(SogaAction? denied) : ISogaAuthorizationPolicy
    {
        public List<SogaAuthorizationContext> Seen { get; } = [];

        public ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken)
        {
            Seen.Add(context);
            return ValueTask.FromResult(context.Action != denied);
        }
    }

    /// <summary>Allows everything until a real policy is plugged in.</summary>
    private sealed class DeferredPolicy : ISogaAuthorizationPolicy
    {
        public DenyPolicy? Inner { get; set; }

        public ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken) =>
            Inner?.IsAllowedAsync(context, cancellationToken) ?? ValueTask.FromResult(true);
    }
}
