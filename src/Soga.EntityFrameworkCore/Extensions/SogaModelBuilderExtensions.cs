using Microsoft.EntityFrameworkCore;
using Soga.Configuration;
using Soga.EntityFrameworkCore.Entities;

namespace Soga.EntityFrameworkCore.Extensions;

/// <summary>Adds Soga persistence mappings to a host-owned EF Core model.</summary>
public static class SogaModelBuilderExtensions
{
    /// <summary>Registers the initial Soga tables and constraints.</summary>
    public static ModelBuilder AddSoga(this ModelBuilder modelBuilder, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ConversationRow>(entity =>
        {
            entity.ToTable("SogaConversations", schema);
            entity.HasKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.TenantId).HasMaxLength(SogaOptions.MaximumTenantIdLength);
            entity.Property(x => x.NextSequence).IsConcurrencyToken();
        });

        modelBuilder.Entity<ParticipantRow>(entity =>
        {
            entity.ToTable("SogaParticipants", schema);
            entity.HasKey(x => new { x.TenantId, x.ConversationId, x.UserId });
            entity.Property(x => x.TenantId).HasMaxLength(SogaOptions.MaximumTenantIdLength);
            entity.Property(x => x.UserId).HasMaxLength(SogaOptions.MaximumUserIdLength);
            entity.HasIndex(x => new { x.TenantId, x.UserId, x.ConversationId });
        });

        modelBuilder.Entity<MessageRow>(entity =>
        {
            entity.ToTable("SogaMessages", schema);
            entity.HasKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.TenantId).HasMaxLength(SogaOptions.MaximumTenantIdLength);
            entity.Property(x => x.SenderId).HasMaxLength(SogaOptions.MaximumUserIdLength);
            entity.Property(x => x.ClientMessageId).HasMaxLength(100);
            entity.Property(x => x.Content).HasMaxLength(10_000);
            entity.HasIndex(x => new { x.TenantId, x.ConversationId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ConversationId, x.SenderId, x.ClientMessageId }).IsUnique();
        });

        modelBuilder.Entity<OutboxRow>(entity =>
        {
            entity.ToTable("SogaOutbox", schema);
            entity.HasKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.TenantId).HasMaxLength(SogaOptions.MaximumTenantIdLength);
            entity.Property(x => x.EventType).HasMaxLength(200);
            entity.HasIndex(x => new { x.PublishedAt, x.OccurredAt });
        });

        return modelBuilder;
    }
}
