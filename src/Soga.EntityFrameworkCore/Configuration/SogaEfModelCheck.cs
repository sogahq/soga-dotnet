using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Soga.EntityFrameworkCore.Entities;

namespace Soga.EntityFrameworkCore.Configuration;

/// <summary>Verifies the host model is safe for Soga before any data is touched.</summary>
internal static class SogaEfModelCheck
{
    // The runtime model is built once per context configuration, so one check per model instance is enough.
    private static readonly ConditionalWeakTable<IModel, object> Verified = [];

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

/// <summary>Runs <see cref="SogaEfModelCheck"/> when the application starts, so a misconfigured model fails fast instead of on the first request.</summary>
internal sealed class SogaEfStartupCheck<TContext>(IServiceScopeFactory scopes) : IHostedService
    where TContext : DbContext
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        SogaEfModelCheck.Validate(scope.ServiceProvider.GetRequiredService<TContext>());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
