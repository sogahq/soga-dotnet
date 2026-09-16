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
        using var provider = new ServiceCollection().AddSoga().BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<SogaOptions>>().Value;

        Assert.Equal("/soga/v1", options.RoutePrefix);
        Assert.Equal("default", options.DefaultTenantId);
    }

    [Fact]
    public void AddSoga_WhenRoutePrefixHasTrailingSlash_RejectsConfiguration()
    {
        using var provider = new ServiceCollection()
            .AddSoga(options => options.RoutePrefix = "/soga/v1/")
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SogaOptions>>().Value);
    }
}
