using Microsoft.Extensions.DependencyInjection;
using Soga.Configuration;
using Soga.Extensions;
using Soga.Identity;

namespace Soga.Tests;

/// <summary>Builds a real Soga service provider with the in-memory store, without a web host.</summary>
internal sealed class TestSoga : IDisposable
{
    public const string Tenant = "tenant-1";

    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    private TestSoga(Action<SogaOptions>? configure)
    {
        _provider = new ServiceCollection()
            .AddLogging()
            .AddSoga(options =>
            {
                options.DefaultTenantId = Tenant;
                configure?.Invoke(options);
            })
            .Services
            .BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public static SogaCaller Alice => new(Tenant, "alice");

    public static SogaCaller Bob => new(Tenant, "bob");

    public static SogaCaller Carol => new(Tenant, "carol");

    public static TestSoga Create(Action<SogaOptions>? configure = null) => new(configure);

    public T Get<T>()
        where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
