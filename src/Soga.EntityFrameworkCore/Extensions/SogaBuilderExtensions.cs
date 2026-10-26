using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Soga.EntityFrameworkCore.Configuration;
using Soga.EntityFrameworkCore.Repositories;
using Soga.Extensions;
using Soga.Persistence;

namespace Soga.EntityFrameworkCore.Extensions;

/// <summary>
/// Provides extension methods for storing Soga data with Entity Framework Core.
/// </summary>
public static class SogaBuilderExtensions
{
    /// <summary>
    /// Stores Soga data through the host's <typeparamref name="TContext"/>, replacing the in-memory store.
    /// </summary>
    /// <typeparam name="TContext">The host database context.</typeparam>
    /// <param name="builder">The Soga builder returned by <c>AddSoga</c>.</param>
    /// <returns>The same <paramref name="builder"/>, so further calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The host must register the context, for example with <c>AddDbContext</c>, and call
    /// <see cref="SogaModelBuilderExtensions.AddSoga"/> from <c>OnModelCreating</c>.
    /// </para>
    /// <para>
    /// The application fails at startup if the Soga model is missing or unsafe, for example on SQL Server without a
    /// case-sensitive identity collation.
    /// </para>
    /// </remarks>
    public static SogaBuilder UseEntityFrameworkCore<TContext>(this SogaBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Replace(ServiceDescriptor.Scoped<ISogaStore, EfSogaStore<TContext>>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SogaEfStartupCheck<TContext>>());
        return builder;
    }
}
