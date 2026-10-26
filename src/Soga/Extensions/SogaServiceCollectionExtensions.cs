using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Soga.Abstractions;
using Soga.Configuration;
using Soga.Conversations;
using Soga.Diagnostics;
using Soga.Identity;
using Soga.Messages;
using Soga.Persistence;
using Soga.Persistence.InMemory;
using Soga.ReadState;
using Soga.Realtime;
using Soga.Synchronization;
using Soga.Tenancy;

namespace Soga.Extensions;

/// <summary>Registers Soga services in an ASP.NET Core host.</summary>
public static class SogaServiceCollectionExtensions
{
    /// <summary>
    /// Adds Soga with the in-memory development provider. Call <c>UseEntityFrameworkCore</c> on the returned
    /// builder (from Soga.EntityFrameworkCore) for durable storage.
    /// </summary>
    public static SogaBuilder AddSoga(
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
        services.AddAuthorization();
        services.AddDataProtection();
        services.AddSignalR();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISogaIdGenerator, SogaIdGenerator>();
        services.TryAddSingleton<ISogaStore, InMemorySogaStore>();
        services.TryAddSingleton<SogaCursorProtector>();
        services.TryAddSingleton<SogaOutboxSignal>();
        services.TryAddSingleton<SogaRealtimePublisher>();

        services.TryAddScoped<ISogaUserContext, HttpSogaUserContext>();
        services.TryAddScoped<ISogaTenantContext, SingleTenantContext>();
        services.TryAddScoped<SogaCallerResolver>();
        services.TryAddScoped<ConversationWorkflow>();
        services.TryAddScoped<MessageWorkflow>();
        services.TryAddScoped<ReadStateWorkflow>();
        services.TryAddScoped<SyncWorkflow>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SogaOutboxDispatcher>());
        services.AddHealthChecks().AddCheck<SogaHealthCheck>("soga", HealthStatus.Unhealthy, ["soga"]);

        return new SogaBuilder(services);
    }
}

/// <summary>Fluent builder returned by <see cref="SogaServiceCollectionExtensions.AddSoga"/>.</summary>
public sealed class SogaBuilder
{
    internal SogaBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>Gets the service collection Soga registers into.</summary>
    public IServiceCollection Services { get; }
}
