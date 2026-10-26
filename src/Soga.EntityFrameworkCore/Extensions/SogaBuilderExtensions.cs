using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soga.EntityFrameworkCore.Repositories;
using Soga.Extensions;
using Soga.Persistence;

namespace Soga.EntityFrameworkCore.Extensions;

/// <summary>Selects EF Core persistence for Soga.</summary>
public static class SogaBuilderExtensions
{
    /// <summary>
    /// Stores Soga data through the host <typeparamref name="TContext"/>. The context must be registered by the host
    /// (for example with <c>AddDbContext</c>) and must call <c>modelBuilder.AddSoga()</c> in <c>OnModelCreating</c>.
    /// </summary>
    /// <typeparam name="TContext">The host database context.</typeparam>
    public static SogaBuilder UseEntityFrameworkCore<TContext>(this SogaBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Replace(ServiceDescriptor.Scoped<ISogaStore, EfSogaStore<TContext>>());
        return builder;
    }
}
