using System.Text.Json;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Errors;
using Soga.Extensibility;
using Soga.Groups;
using Soga.Messages;
using Soga.ReadState;
using Soga.Synchronization;

namespace Soga.Tests.Validation;

/// <summary>Bad input must be rejected before anything is stored, with the documented stable code.</summary>
public sealed class WorkflowInputValidationTests
{
    private static readonly string TooLongUserId = new('u', SogaOptions.MaximumUserIdLength + 1);
    private static readonly string TooLongReference = new('r', SogaOptions.MaximumExternalReferenceLength + 1);

    private static readonly Dictionary<string, JsonElement> NestedMetadata = new()
    {
        ["nested"] = JsonSerializer.SerializeToElement(new { a = 1 }),
    };

    // ---- Groups: creation ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateGroup_WhenTitleIsMissing_IsInvalid(string? title)
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest(title!, ["bob"]), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task CreateGroup_WhenTitleIsTooLong_IsInvalid()
    {
        using var soga = TestSoga.Create(o => o.Groups.MaximumTitleLength = 5);

        var result = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("123456", ["bob"]), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task CreateGroup_WhenTitleIsExactlyTheLimitAfterTrimming_Succeeds()
    {
        using var soga = TestSoga.Create(o => o.Groups.MaximumTitleLength = 5);

        var result = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("  12345  ", ["bob"]), default);

        Assert.Equal("12345", result.Value.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CreateGroup_WhenAParticipantIdIsBlank_IsInvalid(string participant)
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("Team", ["bob", participant]), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task CreateGroup_WhenAParticipantIdIsTooLong_IsInvalid()
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("Team", [TooLongUserId]), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task CreateGroup_WhenTheParticipantListIsMissing_CreatesAGroupOfOne()
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("Solo", null!), default);

        Assert.Equal(["alice"], result.Value.ParticipantUserIds);
    }

    [Fact]
    public async Task CreateGroup_WhenTheExternalReferenceIsTooLong_IsInvalid()
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("Team", ["bob"], TooLongReference), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task CreateGroup_WhenTheExternalReferenceIsBlank_StoresNoReference()
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("Team", ["bob"], "   "), default);

        Assert.Null(result.Value.ExternalReference);
    }

    [Fact]
    public async Task CreateGroup_WhenMetadataIsNested_IsRejectedAsInvalidMetadata()
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("Team", ["bob"], null, NestedMetadata), default);

        Assert.Equal(SogaErrorCodes.MetadataInvalid, result.Error?.Code);
    }

    [Fact]
    public async Task CreateGroup_WhenTheGroupWouldExceedTheLimitIncludingTheOwner_IsTooLarge()
    {
        using var soga = TestSoga.Create(o => o.Groups.MaximumParticipants = 3);

        var atLimit = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("Team", ["b", "c"]), default);
        var overLimit = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("Team", ["b", "c", "d"]), default);

        Assert.True(atLimit.IsSuccess);
        Assert.Equal(SogaErrorCodes.GroupTooLarge, overLimit.Error?.Code);
    }

    // ---- Groups: changes ----

    [Fact]
    public async Task UpdateGroup_WhenNothingIsProvided_IsInvalid()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            Assert.Equal(SogaErrorCodes.InvalidRequest, (await soga.Get<GroupWorkflow>().UpdateAsync(TestSoga.Alice, group, new UpdateConversationRequest(), default)).Error?.Code);
            Assert.Equal(SogaErrorCodes.InvalidRequest, (await soga.Get<GroupWorkflow>().UpdateAsync(TestSoga.Alice, group, null, default)).Error?.Code);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateGroup_WhenTheTitleIsBlank_IsInvalid(string title)
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            var result = await soga.Get<GroupWorkflow>().UpdateAsync(TestSoga.Alice, group, new UpdateConversationRequest(title), default);

            Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
        }
    }

    [Fact]
    public async Task UpdateGroup_WhenMetadataIsInvalid_IsRejectedAsInvalidMetadata()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            var result = await soga.Get<GroupWorkflow>().UpdateAsync(TestSoga.Alice, group, new UpdateConversationRequest(null, NestedMetadata), default);

            Assert.Equal(SogaErrorCodes.MetadataInvalid, result.Error?.Code);
        }
    }

    [Fact]
    public async Task UpdateGroup_WhenOnlyMetadataChanges_KeepsTheTitle()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            var metadata = new Dictionary<string, JsonElement> { ["priority"] = JsonSerializer.SerializeToElement(2) };

            var result = await soga.Get<GroupWorkflow>().UpdateAsync(TestSoga.Alice, group, new UpdateConversationRequest(null, metadata), default);

            Assert.Equal("Team", result.Value!.Title);
            Assert.Equal(2, result.Value.Metadata!["priority"].GetInt32());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AddParticipant_WhenTheUserIdIsMissing_IsInvalid(string? userId)
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            var result = await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group, new AddParticipantRequest(userId!), default);

            Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
        }
    }

    [Fact]
    public async Task AddParticipant_WhenTheRequestIsMissing_IsInvalid()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            Assert.Equal(SogaErrorCodes.InvalidRequest, (await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group, null, default)).Error?.Code);
        }
    }

    [Theory]
    [InlineData("superuser")]
    [InlineData("owners")]
    [InlineData("ADMIN ")]
    public async Task AddParticipant_WithAnUnknownRole_IsRejectedButCaseAndSpacesAreTolerated(string role)
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            var result = await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group, new AddParticipantRequest("erin", role), default);

            if (role == "ADMIN ")
            {
                Assert.Equal("admin", result.Value!.Participants!.Single(p => p.UserId == "erin").Role);
            }
            else
            {
                Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RemoveParticipant_WhenTheUserIdIsMissing_IsInvalid(string? userId)
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            Assert.Equal(SogaErrorCodes.InvalidRequest, (await soga.Get<GroupWorkflow>().RemoveParticipantAsync(TestSoga.Alice, group, userId, default)).Error?.Code);
        }
    }

    [Fact]
    public async Task RemoveParticipant_WhenTheUserNeverJoined_IsParticipantNotFound()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            Assert.Equal(SogaErrorCodes.ParticipantNotFound, (await soga.Get<GroupWorkflow>().RemoveParticipantAsync(TestSoga.Alice, group, "zed", default)).Error?.Code);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ChangeRole_WhenTheUserIdIsMissing_IsInvalid(string? userId)
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            var result = await soga.Get<GroupWorkflow>().ChangeRoleAsync(TestSoga.Alice, group, userId, new ChangeRoleRequest("admin"), default);

            Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("king")]
    public async Task ChangeRole_WhenTheRoleIsMissingOrUnknown_IsInvalid(string? role)
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            var request = role is null ? null : new ChangeRoleRequest(role);

            var result = await soga.Get<GroupWorkflow>().ChangeRoleAsync(TestSoga.Alice, group, "bob", request, default);

            Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
        }
    }

    [Fact]
    public async Task ChangeRole_OfSomeoneWhoLeft_IsParticipantNotFound()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            await soga.Get<GroupWorkflow>().RemoveParticipantAsync(TestSoga.Bob, group, "bob", default);

            var result = await soga.Get<GroupWorkflow>().ChangeRoleAsync(TestSoga.Alice, group, "bob", new ChangeRoleRequest("admin"), default);

            Assert.Equal(SogaErrorCodes.ParticipantNotFound, result.Error?.Code);
        }
    }

    [Fact]
    public async Task EveryGroupChange_WhenGroupsAreDisabled_IsRefused()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            using var disabled = TestSoga.Create(o => o.Groups.Enabled = false);
            var workflow = disabled.Get<GroupWorkflow>();

            Assert.Equal(SogaErrorCodes.GroupsDisabled, (await workflow.CreateAsync(TestSoga.Alice, new CreateGroupRequest("T", ["bob"]), default)).Error?.Code);
            Assert.Equal(SogaErrorCodes.GroupsDisabled, (await workflow.UpdateAsync(TestSoga.Alice, group, new UpdateConversationRequest("x"), default)).Error?.Code);
            Assert.Equal(SogaErrorCodes.GroupsDisabled, (await workflow.ArchiveAsync(TestSoga.Alice, group, true, default)).Error?.Code);
            Assert.Equal(SogaErrorCodes.GroupsDisabled, (await workflow.AddParticipantAsync(TestSoga.Alice, group, new AddParticipantRequest("x"), default)).Error?.Code);
            Assert.Equal(SogaErrorCodes.GroupsDisabled, (await workflow.RemoveParticipantAsync(TestSoga.Alice, group, "bob", default)).Error?.Code);
            Assert.Equal(SogaErrorCodes.GroupsDisabled, (await workflow.ChangeRoleAsync(TestSoga.Alice, group, "bob", new ChangeRoleRequest("admin"), default)).Error?.Code);
        }
    }

    [Fact]
    public async Task GroupChanges_OnADirectConversation_AreRefusedAsNotAGroup()
    {
        using var soga = TestSoga.Create();
        var direct = (await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest("bob"), default)).Value.Value.Id;
        var workflow = soga.Get<GroupWorkflow>();

        Assert.Equal(SogaErrorCodes.ConversationNotGroup, (await workflow.AddParticipantAsync(TestSoga.Alice, direct, new AddParticipantRequest("carol"), default)).Error?.Code);
        Assert.Equal(SogaErrorCodes.ConversationNotGroup, (await workflow.RemoveParticipantAsync(TestSoga.Alice, direct, "bob", default)).Error?.Code);
        Assert.Equal(SogaErrorCodes.ConversationNotGroup, (await workflow.UpdateAsync(TestSoga.Alice, direct, new UpdateConversationRequest("x"), default)).Error?.Code);
        Assert.Equal(SogaErrorCodes.ConversationNotGroup, (await workflow.ArchiveAsync(TestSoga.Alice, direct, true, default)).Error?.Code);
        Assert.Equal(SogaErrorCodes.ConversationNotGroup, (await workflow.ChangeRoleAsync(TestSoga.Alice, direct, "bob", new ChangeRoleRequest("admin"), default)).Error?.Code);
    }

    // ---- Direct conversations and listing ----

    [Fact]
    public async Task CreateDirect_WhenMetadataIsInvalid_IsRejectedAsInvalidMetadata()
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest("bob", null, NestedMetadata), default);

        Assert.Equal(SogaErrorCodes.MetadataInvalid, result.Error?.Code);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData(" alice ")]
    public async Task CreateDirect_WithYourself_IsInvalid(string other)
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest(other), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task CreateDirect_WithTheSameNameInADifferentCase_IsADifferentUser()
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest("Alice"), default);

        Assert.Equal(["Alice", "alice"], result.Value.Value.ParticipantUserIds.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0, null, null, null)]
    [InlineData(null, "channel", null, null)]
    [InlineData(null, null, "deleted", null)]
    [InlineData(null, null, null, "long")]
    public async Task List_WithAnInvalidFilterOrLimit_IsInvalid(int? limit, string? kind, string? state, string? reference)
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<ConversationWorkflow>().ListAsync(
            TestSoga.Alice,
            new ConversationListRequest(limit, null, kind, state, reference == "long" ? TooLongReference : reference),
            default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Theory]
    [InlineData("DIRECT", null)]
    [InlineData(" group ", null)]
    [InlineData(null, "ALL")]
    [InlineData(null, " archived ")]
    [InlineData(null, "")]
    public async Task List_FiltersIgnoreCaseAndSurroundingSpaces(string? kind, string? state)
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<ConversationWorkflow>().ListAsync(TestSoga.Alice, new ConversationListRequest(null, null, kind, state), default);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task List_WhenTheCursorWasIssuedForDifferentFilters_IsInvalidCursor()
    {
        using var soga = TestSoga.Create();
        for (var i = 0; i < 3; i++)
        {
            await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest($"user{i}"), default);
            soga.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var workflow = soga.Get<ConversationWorkflow>();
        var page = (await workflow.ListAsync(TestSoga.Alice, new ConversationListRequest(1), default)).Value;

        var differentFilter = await workflow.ListAsync(TestSoga.Alice, new ConversationListRequest(1, page.NextCursor, "group"), default);
        var differentUser = await workflow.ListAsync(TestSoga.Bob, new ConversationListRequest(1, page.NextCursor), default);

        Assert.Equal(SogaErrorCodes.CursorInvalid, differentFilter.Error?.Code);
        Assert.Equal(SogaErrorCodes.CursorInvalid, differentUser.Error?.Code);
    }

    [Fact]
    public async Task List_WhenTheLimitExceedsTheMaximum_IsClampedRatherThanRejected()
    {
        using var soga = TestSoga.Create(o => { o.Conversations.DefaultPageSize = 1; o.Conversations.MaximumPageSize = 2; });
        for (var i = 0; i < 3; i++)
        {
            await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest($"user{i}"), default);
        }

        var page = (await soga.Get<ConversationWorkflow>().ListAsync(TestSoga.Alice, new ConversationListRequest(1000), default)).Value;

        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    // ---- Messages, read state, and sync ----

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task History_WithALimitBelowOne_IsInvalid(int limit)
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            Assert.Equal(SogaErrorCodes.InvalidRequest, (await soga.Get<MessageWorkflow>().GetHistoryAsync(TestSoga.Alice, group, null, limit, default)).Error?.Code);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Sync_WithALimitBelowOne_IsInvalid(int limit)
    {
        using var soga = TestSoga.Create();

        Assert.Equal(SogaErrorCodes.InvalidRequest, (await soga.Get<SyncWorkflow>().SyncAsync(TestSoga.Alice, null, limit, default)).Error?.Code);
    }

    [Fact]
    public async Task Sync_WithATamperedCursor_IsInvalidCursor()
    {
        using var soga = TestSoga.Create();
        var cursor = (await soga.Get<SyncWorkflow>().SyncAsync(TestSoga.Alice, null, null, default)).Value.Cursor;

        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? "BB" : "AA");

        Assert.Equal(SogaErrorCodes.CursorInvalid, (await soga.Get<SyncWorkflow>().SyncAsync(TestSoga.Alice, tampered, null, default)).Error?.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Edit_WithBlankText_IsInvalid(string text)
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            var message = await soga.SendAsync(TestSoga.Alice, group, "m1", "hi");

            var result = await soga.Get<MessageWorkflow>().EditAsync(TestSoga.Alice, group, message.Id, new EditMessageRequest(text), default);

            Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
        }
    }

    [Fact]
    public async Task Edit_WithTextOverTheLimit_IsTooLong()
    {
        var (soga, group) = await ArrangeGroupAsync(o => o.Messages.MaximumTextLength = 5);
        using (soga)
        {
            var message = await soga.SendAsync(TestSoga.Alice, group, "m1", "hi");

            var result = await soga.Get<MessageWorkflow>().EditAsync(TestSoga.Alice, group, message.Id, new EditMessageRequest("123456"), default);

            Assert.Equal(SogaErrorCodes.MessageTooLong, result.Error?.Code);
        }
    }

    [Fact]
    public async Task Delete_OfAMessageThatDoesNotExist_IsMessageNotFound()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            Assert.Equal(SogaErrorCodes.MessageNotFound, (await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Alice, group, Guid.NewGuid(), default)).Error?.Code);
        }
    }

    [Fact]
    public async Task Delete_WhenDeletionIsDisabled_IsForbiddenForTheSender()
    {
        var (soga, group) = await ArrangeGroupAsync(o => { o.Messages.AllowDeletion = false; o.Messages.AllowAdminDeletion = false; });
        using (soga)
        {
            var message = await soga.SendAsync(TestSoga.Bob, group, "m1", "hi");

            Assert.Equal(SogaErrorCodes.AuthorizationDenied, (await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Bob, group, message.Id, default)).Error?.Code);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MarkRead_WithoutAMessageId_IsInvalid(bool nullRequest)
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            var request = nullRequest ? null : new MarkReadRequest(Guid.Empty);

            Assert.Equal(SogaErrorCodes.InvalidRequest, (await soga.Get<ReadStateWorkflow>().AdvanceAsync(TestSoga.Alice, group, request, default)).Error?.Code);
        }
    }

    [Fact]
    public async Task MarkRead_OfAMessageFromAnotherConversation_IsMessageNotFound()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            var other = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
            var foreign = await soga.SendAsync(TestSoga.Bob, other.Id, "m1", "elsewhere");

            var result = await soga.Get<ReadStateWorkflow>().AdvanceAsync(TestSoga.Alice, group, new MarkReadRequest(foreign.Id), default);

            Assert.Equal(SogaErrorCodes.MessageNotFound, result.Error?.Code);
        }
    }

    // ---- Trusted system messages ----

    [Theory]
    [InlineData("", "text")]
    [InlineData(" ", "text")]
    [InlineData("tenant-1", "")]
    [InlineData("tenant-1", "  ")]
    public async Task SystemMessage_WithBlankTenantOrText_Throws(string tenantId, string text)
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => soga.Get<ISogaSystemMessages>().SendAsync(tenantId, group, text));
        }
    }

    [Fact]
    public async Task SystemMessage_OverTheLengthLimit_Throws()
    {
        var (soga, group) = await ArrangeGroupAsync(o => o.Messages.MaximumTextLength = 5);
        using (soga)
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => soga.Get<ISogaSystemMessages>().SendAsync(TestSoga.Tenant, group, "123456"));
            Assert.Equal("text", exception.ParamName);
        }
    }

    [Fact]
    public async Task SystemMessage_ToAConversationInAnotherTenant_ReturnsNull()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            Assert.Null(await soga.Get<ISogaSystemMessages>().SendAsync("other-tenant", group, "order shipped"));
        }
    }

    [Fact]
    public async Task SystemMessage_ToAnArchivedGroup_IsStillDelivered()
    {
        var (soga, group) = await ArrangeGroupAsync();
        using (soga)
        {
            await soga.Get<GroupWorkflow>().ArchiveAsync(TestSoga.Alice, group, archived: true, default);

            var message = await soga.Get<ISogaSystemMessages>().SendAsync(TestSoga.Tenant, group, "order closed");

            Assert.Equal(SogaMessageKinds.System, message!.Kind);
        }
    }

    private static async Task<(TestSoga Soga, Guid Group)> ArrangeGroupAsync(Action<SogaOptions>? configure = null)
    {
        var soga = TestSoga.Create(configure);
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob", "carol");
        return (soga, group.Id);
    }
}
