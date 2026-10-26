using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Soga.EntityFrameworkCore.Entities;

namespace Soga.EntityFrameworkCore.Configuration;

/// <summary>
/// Verifies that the host's Entity Framework Core model is safe for Soga before any data is read or written.
/// </summary>
internal static class SogaEfModelCheck
{
    /// <summary>
    /// The runtime models that have already passed the check.
    /// </summary>
    /// <remarks>
    /// The runtime model is built once per context configuration, so one check per model instance is enough. A weak
    /// table lets a discarded model be garbage collected.
    /// </remarks>
    private static readonly ConditionalWeakTable<IModel, object> Verified = [];

    /// <summary>
    /// Validates the model of a context.
    /// </summary>
    /// <param name="db">The host context to validate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The model does not include the Soga tables, or the provider is SQL Server and the identity columns have no
    /// case-sensitive collation.
    /// </exception>
    public static void Validate(DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        var runtimeModel = db.Model;
        if (Verified.TryGetValue(runtimeModel, out _))
        {
            return;
        }

        if (runtimeModel.FindEntityType(typeof(ConversationRow)) is null)
        {
            throw new InvalidOperationException(
                $"{db.GetType().Name} does not include the Soga model. Call modelBuilder.AddSoga() from OnModelCreating and add a migration.");
        }

        // Soga treats user and tenant IDs as case-sensitive. SQL Server compares strings case-insensitively unless a
        // collation says otherwise, which would make "Alice" and "alice" one identity: one user could read another's
        // conversations. Refuse to run rather than risk that. Collation lives only in the design-time model.
        if (db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
        {
            var designModel = db.GetService<IDesignTimeModel>().Model;
            var userId = designModel.FindEntityType(typeof(ParticipantRow))!.FindProperty(nameof(ParticipantRow.UserId))!;
            if (string.IsNullOrWhiteSpace(userId.GetCollation()))
            {
                throw new InvalidOperationException(
                    "SQL Server compares strings case-insensitively by default, so user IDs 'Alice' and 'alice' would be treated as the same user. " +
                    "Pass a case-sensitive collation to modelBuilder.AddSoga(identityCollation: \"Latin1_General_100_BIN2\") and add a migration.");
            }
        }

        Verified.TryAdd(runtimeModel, new object());
    }
}

/// <summary>
/// A hosted service that runs <see cref="SogaEfModelCheck"/> when the application starts.
/// </summary>
/// <typeparam name="TContext">The host database context.</typeparam>
/// <param name="scopes">Creates the scope used to resolve the context.</param>
/// <remarks>A misconfigured model stops the application at startup instead of failing on the first request.</remarks>
internal sealed class SogaEfStartupCheck<TContext>(IServiceScopeFactory scopes) : IHostedService
    where TContext : DbContext
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        SogaEfModelCheck.Validate(scope.ServiceProvider.GetRequiredService<TContext>());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
