using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Soga.Abstractions;
using Soga.Authorization;
using Soga.Configuration;
using Soga.Conversations;
using Soga.Diagnostics;
using Soga.Extensibility;
using Soga.Groups;
using Soga.Identity;
using Soga.Messages;
using Soga.Persistence;
using Soga.Persistence.InMemory;
using Soga.ReadState;
using Soga.Realtime;
using Soga.Synchronization;
using Soga.Tenancy;

namespace Soga.Extensions;

/// <summary>
/// Provides extension methods for registering Soga services in an <see cref="IServiceCollection"/>.
/// </summary>
public static class SogaServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Soga messaging services, using the in-memory development store.
    /// </summary>
    /// <param name="services">The service collection to add Soga to.</param>
    /// <param name="configure">An optional delegate that configures <see cref="SogaOptions"/>.</param>
    /// <returns>A <see cref="SogaBuilder"/> for further configuration, such as selecting a durable store.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Options are bound from the <c>Soga</c> configuration section first, then <paramref name="configure"/> is
    /// applied. They are validated when the application starts, so invalid configuration stops the host.
    /// </para>
    /// <para>
    /// The in-memory store loses all data when the process stops. Call <c>UseEntityFrameworkCore</c> from the
    /// <c>Soga.EntityFrameworkCore</c> package on the returned builder for durable storage.
    /// </para>
    /// <para>
    /// Every service is registered with <c>TryAdd</c>, so a host can replace one, such as
    /// <see cref="ISogaTenantContext"/>, by registering its own implementation first.
    /// </para>
    /// </remarks>
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
        services.TryAddSingleton<SogaConnectionRegistry>();
        services.TryAddSingleton<SogaTypingTracker>();
        services.TryAddSingleton<SogaPresenceBroadcaster>();
        services.TryAddSingleton<ISogaPresenceProvider, InMemoryPresenceProvider>();
        services.TryAddSingleton<ISogaAuditSink, LoggingAuditSink>();
        services.TryAddSingleton<SogaAuditor>();

        services.TryAddScoped<ISogaUserContext, HttpSogaUserContext>();
        services.TryAddScoped<ISogaTenantContext, SingleTenantContext>();
        services.TryAddScoped<SogaCallerResolver>();
        services.TryAddScoped<SogaAuthorizer>();
        services.TryAddScoped<ConversationWorkflow>();
        services.TryAddScoped<GroupWorkflow>();
        services.TryAddScoped<MessageWorkflow>();
        services.TryAddScoped<ReadStateWorkflow>();
        services.TryAddScoped<SyncWorkflow>();
        services.TryAddScoped<ISogaSystemMessages, SogaSystemMessages>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SogaOutboxDispatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SogaEphemeralService>());
        services.AddHealthChecks().AddCheck<SogaHealthCheck>("soga", HealthStatus.Unhealthy, ["soga"]);

        return new SogaBuilder(services);
    }
}

/// <summary>
/// Configures Soga after it has been added with
/// <see cref="SogaServiceCollectionExtensions.AddSoga(IServiceCollection, Action{SogaOptions})"/>.
/// </summary>
/// <remarks>
/// Storage providers and other add-on packages add extension methods to this type, for example
/// <c>UseEntityFrameworkCore&lt;TContext&gt;()</c>.
/// </remarks>
public sealed class SogaBuilder
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SogaBuilder"/> class.
    /// </summary>
    /// <param name="services">The service collection Soga was added to.</param>
    internal SogaBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>Gets the service collection Soga registers into.</summary>
    public IServiceCollection Services { get; }
}
