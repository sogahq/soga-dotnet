using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Extensions;

namespace Soga.Tests.Configuration;

public sealed class SogaOptionsTests
{
    [Fact]
    public void AddSoga_WhenDefaultsAreUsed_ProvidesValidOptions()
    {
        using var provider = new ServiceCollection().AddLogging().AddSoga().Services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<SogaOptions>>().Value;

        Assert.Equal("/soga/v1", options.RoutePrefix);
        Assert.Equal("default", options.DefaultTenantId);
        Assert.Equal(8_000, options.Messages.MaximumTextLength);
    }

    [Fact]
    public void AddSoga_WhenRoutePrefixHasTrailingSlash_RejectsConfiguration()
    {
        AssertRejected(options => options.RoutePrefix = "/soga/v1/");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SogaOptions.AbsoluteMaximumTextLength + 1)]
    public void AddSoga_WhenMaximumTextLengthIsOutOfRange_RejectsConfiguration(int length)
    {
        AssertRejected(options => options.Messages.MaximumTextLength = length);
    }

    [Fact]
    public void AddSoga_WhenDefaultPageSizeExceedsMaximum_RejectsConfiguration()
    {
        AssertRejected(options =>
        {
            options.Messages.DefaultPageSize = 200;
            options.Messages.MaximumPageSize = 100;
        });
    }

    [Fact]
    public void AddSoga_WhenRetryDelayExceedsMaximum_RejectsConfiguration()
    {
        AssertRejected(options =>
        {
            options.Outbox.BaseRetryDelay = TimeSpan.FromMinutes(10);
            options.Outbox.MaximumRetryDelay = TimeSpan.FromMinutes(1);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10_001)]
    public void AddSoga_WhenGroupSizeIsOutOfRange_RejectsConfiguration(int size)
    {
        AssertRejected(options => options.Groups.MaximumParticipants = size);
    }

    [Fact]
    public void AddSoga_WhenTypingIntervalIsNotShorterThanTimeout_RejectsConfiguration()
    {
        AssertRejected(options =>
        {
            options.Typing.Timeout = TimeSpan.FromSeconds(2);
            options.Typing.MinimumBroadcastInterval = TimeSpan.FromSeconds(2);
        });
    }

    [Fact]
    public void AddSoga_WhenEditWindowIsNotPositive_RejectsConfigurationButNullMeansUnlimited()
    {
        AssertRejected(options => options.Messages.EditWindow = TimeSpan.Zero);

        using var provider = new ServiceCollection().AddLogging().AddSoga(options => options.Messages.EditWindow = null).Services.BuildServiceProvider();
        Assert.Null(provider.GetRequiredService<IOptions<SogaOptions>>().Value.Messages.EditWindow);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20_000)]
    public void AddSoga_WhenMetadataLimitIsOutOfRange_RejectsConfiguration(int bytes)
    {
        AssertRejected(options => options.Conversations.MaximumMetadataBytes = bytes);
    }

    [Fact]
    public void AddSoga_WhenTheGroupTitleLimitIsTooLarge_RejectsConfiguration()
    {
        AssertRejected(options => options.Groups.MaximumTitleLength = 201);
    }

    [Fact]
    public void AddSoga_WhenPresenceGraceIsNegative_RejectsConfiguration()
    {
        AssertRejected(options => options.Presence.OfflineGracePeriod = TimeSpan.FromSeconds(-1));
    }

    [Fact]
    public void AddSoga_DefaultsMatchThePublishedV2Configuration()
    {
        using var provider = new ServiceCollection().AddLogging().AddSoga().Services.BuildServiceProvider();

        var o = provider.GetRequiredService<IOptions<SogaOptions>>().Value;

        Assert.Equal((256, 100, false), (o.Groups.MaximumParticipants, o.Groups.MaximumTitleLength, o.Groups.PostSystemMessages));
        Assert.Equal((true, TimeSpan.FromMinutes(15), false, true, true), (o.Messages.AllowEditing, o.Messages.EditWindow, o.Messages.RetainEditHistory, o.Messages.AllowDeletion, o.Messages.AllowAdminDeletion));
        Assert.Equal((true, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(2)), (o.Typing.Enabled, o.Typing.Timeout, o.Typing.MinimumBroadcastInterval));
        Assert.Equal((true, TimeSpan.FromSeconds(10), false), (o.Presence.Enabled, o.Presence.OfflineGracePeriod, o.Presence.ExposeLastSeen));
    }

    [Fact]
    public void AddSoga_WhenSeveralOptionsAreInvalid_ReportsEveryFailure()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSoga(options =>
            {
                options.RoutePrefix = "soga";
                options.Messages.MaximumTextLength = 0;
            })
            .Services
            .BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SogaOptions>>().Value);

        Assert.Equal(2, exception.Failures.Count());
    }

    private static void AssertRejected(Action<SogaOptions> configure)
    {
        using var provider = new ServiceCollection().AddLogging().AddSoga(configure).Services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SogaOptions>>().Value);
    }
}
