using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Identity;
using Soga.Tenancy;

namespace Soga.Extensions;

/// <summary>Registers Soga services in an ASP.NET Core host.</summary>
public static class SogaServiceCollectionExtensions
{
    /// <summary>Adds the Soga core services.</summary>
    public static IServiceCollection AddSoga(
        this IServiceCollection services,
        Action<SogaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());

        var options = services.AddOptions<SogaOptions>()
            .BindConfiguration(SogaOptions.SectionName)
            .ValidateOnStart();

        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SogaOptions>, SogaOptionsValidator>());
        services.AddHttpContextAccessor();
        services.TryAddScoped<ISogaUserContext, HttpSogaUserContext>();
        services.TryAddScoped<ISogaTenantContext, SingleTenantContext>();
        services.AddAuthorization();
        return services;
    }
}
