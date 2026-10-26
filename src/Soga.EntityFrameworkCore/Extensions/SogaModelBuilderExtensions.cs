using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Soga.Configuration;
using Soga.EntityFrameworkCore.Entities;

namespace Soga.EntityFrameworkCore.Extensions;

/// <summary>Adds Soga persistence mappings to a host-owned EF Core model.</summary>
public static class SogaModelBuilderExtensions
{
    /// <summary>
    /// Registers the Soga tables and constraints. Call it from the host <c>DbContext.OnModelCreating</c>; the host
    /// owns migrations and Soga never migrates a database on its own (ADR 0004, ADR 0005).
    /// </summary>
    /// <param name="modelBuilder">The host model builder.</param>
    /// <param name="schema">An optional database schema.</param>
    /// <param name="tablePrefix">The prefix of every Soga table. Defaults to <c>Soga</c>.</param>
    public static ModelBuilder AddSoga(this ModelBuilder modelBuilder, string? schema = null, string tablePrefix = "Soga")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(tablePrefix);

        modelBuilder.Entity<ConversationRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}Conversations", schema);
            entity.HasKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.TenantId).HasMaxLength(SogaOptions.MaximumTenantIdLength);
            entity.Property(x => x.DirectKey).HasMaxLength(64);
            entity.Property(x => x.ExternalReference).HasMaxLength(SogaOptions.MaximumExternalReferenceLength);
            entity.Property(x => x.CreatedAt).HasConversion<UtcDateTimeConverter>();
            entity.Property(x => x.LastActivityAt).HasConversion<UtcDateTimeConverter>();
            entity.HasIndex(x => new { x.TenantId, x.DirectKey }).IsUnique();
        });

        modelBuilder.Entity<ParticipantRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}Participants", schema);
            entity.HasKey(x => new { x.TenantId, x.ConversationId, x.UserId });
            entity.Property(x => x.TenantId).HasMaxLength(SogaOptions.MaximumTenantIdLength);
            entity.Property(x => x.UserId).HasMaxLength(SogaOptions.MaximumUserIdLength);
            entity.Property(x => x.JoinedAt).HasConversion<UtcDateTimeConverter>();
            entity.Property(x => x.LeftAt).HasConversion<NullableUtcDateTimeConverter>();
            entity.Property(x => x.LastReadAt).HasConversion<NullableUtcDateTimeConverter>();
            entity.HasIndex(x => new { x.TenantId, x.UserId, x.ConversationId });
            ReferenceConversation(entity);
        });

        modelBuilder.Entity<MessageRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}Messages", schema);
            entity.HasKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.TenantId).HasMaxLength(SogaOptions.MaximumTenantIdLength);
            entity.Property(x => x.SenderId).HasMaxLength(SogaOptions.MaximumUserIdLength);
            entity.Property(x => x.ClientMessageId).HasMaxLength(SogaOptions.MaximumClientMessageIdLength);
            entity.Property(x => x.Kind).HasMaxLength(20);
            entity.Property(x => x.Content).HasMaxLength(SogaOptions.AbsoluteMaximumTextLength);
            entity.Property(x => x.CreatedAt).HasConversion<UtcDateTimeConverter>();
            entity.HasIndex(x => new { x.TenantId, x.ConversationId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ConversationId, x.SenderId, x.ClientMessageId }).IsUnique();
            ReferenceConversation(entity);
        });

        modelBuilder.Entity<ChangeRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}Changes", schema);
            entity.HasKey(x => new { x.TenantId, x.Sequence });
            entity.Property(x => x.TenantId).HasMaxLength(SogaOptions.MaximumTenantIdLength);
            entity.Property(x => x.Sequence).ValueGeneratedNever();
            entity.Property(x => x.ChangeType).HasMaxLength(100);
            entity.Property(x => x.OccurredAt).HasConversion<UtcDateTimeConverter>();
            entity.HasIndex(x => new { x.TenantId, x.ConversationId, x.Sequence });
            entity.HasIndex(x => x.EventId).IsUnique();
            ReferenceConversation(entity);
        });

        modelBuilder.Entity<TenantStateRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}TenantState", schema);
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.TenantId).HasMaxLength(SogaOptions.MaximumTenantIdLength);
        });

        modelBuilder.Entity<OutboxRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}Outbox", schema);
            entity.HasKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.TenantId).HasMaxLength(SogaOptions.MaximumTenantIdLength);
            entity.Property(x => x.EventType).HasMaxLength(200);
            entity.Property(x => x.LastFailure).HasMaxLength(200);
            entity.Property(x => x.OccurredAt).HasConversion<UtcDateTimeConverter>();
            entity.Property(x => x.AvailableAt).HasConversion<UtcDateTimeConverter>();
            entity.Property(x => x.LeaseUntil).HasConversion<NullableUtcDateTimeConverter>();
            entity.Property(x => x.PublishedAt).HasConversion<NullableUtcDateTimeConverter>();
            entity.Property(x => x.FailedAt).HasConversion<NullableUtcDateTimeConverter>();
            entity.HasIndex(x => new { x.PublishedAt, x.FailedAt, x.AvailableAt });
        });

        return modelBuilder;
    }

    // The relationship has no navigation properties: rows stay flat and tenant-scoped.
    private static void ReferenceConversation<TRow>(EntityTypeBuilder<TRow> entity)
        where TRow : class
    {
        entity.HasOne<ConversationRow>()
            .WithMany()
            .HasForeignKey("TenantId", "ConversationId")
            .HasPrincipalKey(nameof(ConversationRow.TenantId), nameof(ConversationRow.Id))
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Stores <see cref="DateTimeOffset"/> as a UTC <see cref="DateTime"/> so every provider can sort and compare it.</summary>
internal sealed class UtcDateTimeConverter : ValueConverter<DateTimeOffset, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
    {
    }
}

internal sealed class NullableUtcDateTimeConverter : ValueConverter<DateTimeOffset?, DateTime?>
{
    public NullableUtcDateTimeConverter()
        : base(
            value => value.HasValue ? value.Value.UtcDateTime : null,
            value => value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null)
    {
    }
}
