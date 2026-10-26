using Soga.Configuration;

namespace Soga.Tests.Configuration;

/// <summary>Every validation rule, its failure message, and the boundary values that must still be accepted.</summary>
public sealed class SogaOptionsValidatorTests
{
    public static TheoryData<string, Action<SogaOptions>> InvalidSettings => new()
    {
        { "Soga:RoutePrefix", o => o.RoutePrefix = "" },
        { "Soga:RoutePrefix", o => o.RoutePrefix = "soga" },
        { "Soga:RoutePrefix", o => o.RoutePrefix = "/soga/" },
        { "Soga:DefaultTenantId", o => o.DefaultTenantId = " " },
        { "Soga:DefaultTenantId", o => o.DefaultTenantId = new string('t', SogaOptions.MaximumTenantIdLength + 1) },
        { "Soga:Identity:UserIdClaimType", o => o.Identity.UserIdClaimType = "" },
        { "Soga:Conversations:DefaultPageSize", o => o.Conversations.DefaultPageSize = 0 },
        { "Soga:Conversations:DefaultPageSize", o => o.Conversations.MaximumPageSize = 501 },
        { "Soga:Messages:DefaultPageSize", o => o.Messages.DefaultPageSize = 0 },
        { "Soga:Sync:DefaultBatchSize", o => o.Sync.DefaultBatchSize = 0 },
        { "Soga:Sync:DefaultBatchSize", o => { o.Sync.DefaultBatchSize = 100; o.Sync.MaximumBatchSize = 50; } },
        { "Soga:Messages:MaximumTextLength", o => o.Messages.MaximumTextLength = 0 },
        { "Soga:Messages:EditWindow", o => o.Messages.EditWindow = TimeSpan.FromSeconds(-1) },
        { "Soga:Conversations:MaximumMetadataBytes", o => o.Conversations.MaximumMetadataBytes = 1 },
        { "Soga:Conversations:MaximumMetadataProperties", o => o.Conversations.MaximumMetadataProperties = 0 },
        { "Soga:Conversations:MaximumMetadataProperties", o => o.Conversations.MaximumMetadataProperties = 101 },
        { "Soga:Groups:MaximumParticipants", o => o.Groups.MaximumParticipants = 1 },
        { "Soga:Groups:MaximumTitleLength", o => o.Groups.MaximumTitleLength = 0 },
        { "Soga:Groups:MaximumTitleLength", o => o.Groups.MaximumTitleLength = SogaOptions.AbsoluteMaximumTitleLength + 1 },
        { "Soga:Typing:Timeout", o => o.Typing.Timeout = TimeSpan.Zero },
        { "Soga:Typing:Timeout", o => o.Typing.MinimumBroadcastInterval = TimeSpan.FromSeconds(-1) },
        { "Soga:Presence:OfflineGracePeriod", o => o.Presence.OfflineGracePeriod = TimeSpan.FromSeconds(-1) },
        { "Soga:Outbox:PollInterval", o => o.Outbox.PollInterval = TimeSpan.Zero },
        { "Soga:Outbox:BatchSize", o => o.Outbox.BatchSize = 0 },
        { "Soga:Outbox:BatchSize", o => o.Outbox.BatchSize = 501 },
        { "Soga:Outbox:LeaseDuration", o => o.Outbox.LeaseDuration = TimeSpan.Zero },
        { "Soga:Outbox:MaximumAttempts", o => o.Outbox.MaximumAttempts = 0 },
        { "Soga:Outbox:BaseRetryDelay", o => o.Outbox.BaseRetryDelay = TimeSpan.Zero },
        { "Soga:Outbox:Retention", o => o.Outbox.Retention = TimeSpan.Zero },
        { "Soga:Security:MaximumRequestBodyBytes", o => o.Security.MaximumRequestBodyBytes = 1023 },
        { "Soga:Security:MaximumRequestBodyBytes", o => o.Security.MaximumRequestBodyBytes = 1_048_577 },
    };

    public static TheoryData<Action<SogaOptions>> BoundarySettings => new()
    {
        o => o.Messages.MaximumTextLength = 1,
        o => o.Messages.MaximumTextLength = SogaOptions.AbsoluteMaximumTextLength,
        o => { o.Messages.DefaultPageSize = 500; o.Messages.MaximumPageSize = 500; },
        o => o.Messages.EditWindow = null,
        o => o.Conversations.MaximumMetadataBytes = 2,
        o => o.Conversations.MaximumMetadataProperties = 100,
        o => o.Groups.MaximumParticipants = 2,
        o => o.Groups.MaximumParticipants = 10_000,
        o => o.Presence.OfflineGracePeriod = TimeSpan.Zero,
        o => o.Typing.MinimumBroadcastInterval = TimeSpan.Zero,
        o => o.Outbox.MaximumAttempts = 1,
        o => { o.Outbox.BaseRetryDelay = TimeSpan.FromSeconds(5); o.Outbox.MaximumRetryDelay = TimeSpan.FromSeconds(5); },
        o => o.Security.MaximumRequestBodyBytes = 1024,
        o => o.Security.MaximumRequestBodyBytes = 1_048_576,
        o => o.DefaultTenantId = new string('t', SogaOptions.MaximumTenantIdLength),
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Validate_WhenSettingIsInvalid_FailsAndNamesTheConfigurationKey(string key, Action<SogaOptions> configure)
    {
        var options = new SogaOptions();
        configure(options);

        var result = new SogaOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.StartsWith(key, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(BoundarySettings))]
    public void Validate_WhenSettingIsAtItsLimit_Succeeds(Action<SogaOptions> configure)
    {
        var options = new SogaOptions();
        configure(options);

        var result = new SogaOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [Fact]
    public void Validate_WhenSeveralSettingsAreInvalid_ReportsEveryFailureAtOnce()
    {
        var options = new SogaOptions { RoutePrefix = "bad" };
        options.Outbox.BatchSize = 0;
        options.Groups.MaximumParticipants = 0;

        var result = new SogaOptionsValidator().Validate(null, options);

        Assert.Equal(3, result.Failures!.Count());
    }

    [Fact]
    public void Validate_WhenDefaultsAreUsed_Succeeds()
    {
        Assert.True(new SogaOptionsValidator().Validate(null, new SogaOptions()).Succeeded);
    }
}
