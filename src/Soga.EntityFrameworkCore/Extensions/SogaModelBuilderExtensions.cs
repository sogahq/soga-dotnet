using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Soga.Configuration;
using Soga.EntityFrameworkCore.Entities;

namespace Soga.EntityFrameworkCore.Extensions;

/// <summary>
/// Provides extension methods for adding the Soga tables to a host-owned Entity Framework Core model.
/// </summary>
public static class SogaModelBuilderExtensions
{
    /// <summary>
    /// Compares identity values ordinally (case-sensitively) inside Entity Framework Core's change tracker.
    /// </summary>
    private static readonly ValueComparer<string> OrdinalIdentityComparer = new(
        (left, right) => string.Equals(left, right, StringComparison.Ordinal),
        value => value.GetHashCode(StringComparison.Ordinal),
        value => value);

    /// <summary>
    /// Adds the Soga tables, keys, indexes, and constraints to the model.
    /// </summary>
    /// <param name="modelBuilder">The host model builder.</param>
    /// <param name="schema">An optional database schema for the Soga tables.</param>
    /// <param name="tablePrefix">The prefix of every Soga table name. Defaults to <c>Soga</c>.</param>
    /// <param name="identityCollation">
    /// A case-sensitive collation applied to every identity column (tenant, user, and client message IDs). Soga compares
    /// identities ordinally, so <c>Alice</c> and <c>alice</c> are different users. PostgreSQL and SQLite already behave this way.
    /// <b>SQL Server is case-insensitive by default and requires this</b>, for example <c>"Latin1_General_100_BIN2"</c>;
    /// Soga refuses to start on SQL Server without it, because otherwise two different users could share one identity.
    /// </param>
    /// <returns>The same <paramref name="modelBuilder"/>, so further calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelBuilder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tablePrefix"/> is empty or whitespace.</exception>
    /// <remarks>
    /// Call this method from the host's <c>DbContext.OnModelCreating</c>. The host owns migrations; Soga never migrates
    /// a database on its own (ADR 0004, ADR 0005). No table references the host's user table.
    /// </remarks>
    public static ModelBuilder AddSoga(
        this ModelBuilder modelBuilder,
        string? schema = null,
        string tablePrefix = "Soga",
        string? identityCollation = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(tablePrefix);

        // Identity columns compare ordinally when a collation is given. Everything that names a person, tenant, or
        // idempotency key goes through here so none can be forgotten.
        PropertyBuilder<T> Ident<T>(PropertyBuilder<T> property, int length)
        {
            property.HasMaxLength(length);
            if (identityCollation is null)
            {
                return property;
            }

            // The collation fixes how the database compares. EF also compares keys in memory when it tracks entities, and its
            // SQL Server provider assumes a case-insensitive default, which would merge "Alice" and "alice" in the identity
            // map. Force ordinal comparison there too.
            property.UseCollation(identityCollation);
            property.Metadata.SetValueComparer(OrdinalIdentityComparer);
            return property;
        }

        modelBuilder.Entity<ConversationRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}Conversations", schema);
            entity.HasKey(x => new { x.TenantId, x.Id });
            Ident(entity.Property(x => x.TenantId), SogaOptions.MaximumTenantIdLength);
            Ident(entity.Property(x => x.DirectKey), 64);
            Ident(entity.Property(x => x.ExternalReference), SogaOptions.MaximumExternalReferenceLength);
            entity.Property(x => x.Kind).HasMaxLength(10);
            entity.Property(x => x.Title).HasMaxLength(SogaOptions.AbsoluteMaximumTitleLength);
            entity.Property(x => x.MetadataJson).HasMaxLength(SogaOptions.AbsoluteMaximumMetadataBytes);
            entity.Property(x => x.CreatedAt).HasConversion<UtcDateTimeConverter>();
            entity.Property(x => x.LastActivityAt).HasConversion<UtcDateTimeConverter>();
            entity.Property(x => x.ArchivedAt).HasConversion<NullableUtcDateTimeConverter>();
            entity.HasIndex(x => new { x.TenantId, x.DirectKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ExternalReference });
        });

        modelBuilder.Entity<ParticipantRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}Participants", schema);
            entity.HasKey(x => new { x.TenantId, x.ConversationId, x.UserId });
            Ident(entity.Property(x => x.TenantId), SogaOptions.MaximumTenantIdLength);
            Ident(entity.Property(x => x.UserId), SogaOptions.MaximumUserIdLength);
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(10);
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
            Ident(entity.Property(x => x.TenantId), SogaOptions.MaximumTenantIdLength);
            Ident(entity.Property(x => x.SenderId), SogaOptions.MaximumUserIdLength);
            Ident(entity.Property(x => x.ClientMessageId), SogaOptions.MaximumClientMessageIdLength);
            Ident(entity.Property(x => x.DeletedBy), SogaOptions.MaximumUserIdLength);
            entity.Property(x => x.Kind).HasMaxLength(20);
            entity.Property(x => x.Content).HasMaxLength(SogaOptions.AbsoluteMaximumTextLength);
            entity.Property(x => x.CreatedAt).HasConversion<UtcDateTimeConverter>();
            entity.Property(x => x.EditedAt).HasConversion<NullableUtcDateTimeConverter>();
            entity.Property(x => x.DeletedAt).HasConversion<NullableUtcDateTimeConverter>();
            entity.Property(x => x.ContentFingerprint).HasMaxLength(64);
            entity.HasIndex(x => new { x.TenantId, x.ConversationId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ConversationId, x.SenderId, x.ClientMessageId }).IsUnique();
            ReferenceConversation(entity);
        });

        modelBuilder.Entity<MessageEditRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}MessageEdits", schema);
            entity.HasKey(x => new { x.TenantId, x.MessageId, x.Version });
            Ident(entity.Property(x => x.TenantId), SogaOptions.MaximumTenantIdLength);
            entity.Property(x => x.PreviousContent).HasMaxLength(SogaOptions.AbsoluteMaximumTextLength);
            entity.Property(x => x.EditedAt).HasConversion<UtcDateTimeConverter>();
            entity.HasOne<MessageRow>()
                .WithMany()
                .HasForeignKey("TenantId", "MessageId")
                .HasPrincipalKey(nameof(MessageRow.TenantId), nameof(MessageRow.Id))
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ChangeRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}Changes", schema);
            entity.HasKey(x => new { x.TenantId, x.Sequence });
            Ident(entity.Property(x => x.TenantId), SogaOptions.MaximumTenantIdLength);
            Ident(entity.Property(x => x.TargetUserId), SogaOptions.MaximumUserIdLength);
            entity.Property(x => x.Sequence).ValueGeneratedNever();
            entity.Property(x => x.ChangeType).HasMaxLength(100);
            entity.Property(x => x.OccurredAt).HasConversion<UtcDateTimeConverter>();
            entity.HasIndex(x => new { x.TenantId, x.ConversationId, x.Sequence });
            entity.HasIndex(x => new { x.TenantId, x.TargetUserId, x.Sequence });
            entity.HasIndex(x => x.EventId).IsUnique();
            ReferenceConversation(entity);
        });

        modelBuilder.Entity<TenantStateRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}TenantState", schema);
            entity.HasKey(x => x.TenantId);
            Ident(entity.Property(x => x.TenantId), SogaOptions.MaximumTenantIdLength);
        });

        modelBuilder.Entity<OutboxRow>(entity =>
        {
            entity.ToTable($"{tablePrefix}Outbox", schema);
            entity.HasKey(x => new { x.TenantId, x.Id });
            Ident(entity.Property(x => x.TenantId), SogaOptions.MaximumTenantIdLength);
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

    /// <summary>
    /// Adds a foreign key from a Soga row to its conversation, on the composite tenant and conversation key.
    /// </summary>
    /// <typeparam name="TRow">The row type that has <c>TenantId</c> and <c>ConversationId</c> columns.</typeparam>
    /// <param name="entity">The entity type builder of the row.</param>
    /// <remarks>
    /// The relationship has no navigation properties, so rows stay flat and tenant-scoped. Deletes are restricted so
    /// history can never be removed by cascade.
    /// </remarks>
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

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as a UTC <see cref="DateTime"/>.
/// </summary>
/// <remarks>Not every provider can sort and compare <see cref="DateTimeOffset"/> values; every provider can sort UTC <see cref="DateTime"/> values.</remarks>
internal sealed class UtcDateTimeConverter : ValueConverter<DateTimeOffset, DateTime>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UtcDateTimeConverter"/> class.
    /// </summary>
    public UtcDateTimeConverter()
        : base(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
    {
    }
}

/// <summary>
/// Stores a nullable <see cref="DateTimeOffset"/> as a nullable UTC <see cref="DateTime"/>.
/// </summary>
internal sealed class NullableUtcDateTimeConverter : ValueConverter<DateTimeOffset?, DateTime?>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NullableUtcDateTimeConverter"/> class.
    /// </summary>
    public NullableUtcDateTimeConverter()
        : base(
            value => value.HasValue ? value.Value.UtcDateTime : null,
            value => value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null)
    {
    }
}
