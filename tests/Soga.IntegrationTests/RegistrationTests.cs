using Microsoft.Extensions.DependencyInjection;
using Soga.Extensions;
using Soga.Identity;
using Xunit;

namespace Soga.IntegrationTests;

public sealed class RegistrationTests
{
    [Fact]
    public void AddSoga_RegistersTrustedUserContext()
    {
        using var provider = new ServiceCollection().AddSoga().BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<ISogaUserContext>());
    }
}
