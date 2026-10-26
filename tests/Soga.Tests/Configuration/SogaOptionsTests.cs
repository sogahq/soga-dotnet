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
