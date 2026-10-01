using System.Security.Claims;

namespace Soga.Configuration;

/// <summary>Configures the public Soga protocol surface.</summary>
public sealed class SogaOptions
{
    /// <summary>The configuration section consumed by Soga.</summary>
    public const string SectionName = "Soga";

    /// <summary>The maximum supported normalized host user identifier length.</summary>
    public const int MaximumUserIdLength = 200;

    /// <summary>The maximum supported normalized tenant identifier length.</summary>
    public const int MaximumTenantIdLength = 100;

    /// <summary>The maximum supported client message identifier length.</summary>
    public const int MaximumClientMessageIdLength = 100;

    /// <summary>The maximum supported external reference length.</summary>
    public const int MaximumExternalReferenceLength = 200;

    /// <summary>The largest message text length the persistence model can store.</summary>
    public const int AbsoluteMaximumTextLength = 10_000;

    /// <summary>Gets or sets the versioned route prefix.</summary>
    public string RoutePrefix { get; set; } = "/soga/v1";

    /// <summary>Gets or sets the tenant used by a single-tenant host.</summary>
    public string DefaultTenantId { get; set; } = "default";

    /// <summary>Gets the identity resolution options.</summary>
    public SogaIdentityOptions Identity { get; } = new();

    /// <summary>Gets the conversation options.</summary>
    public SogaConversationOptions Conversations { get; } = new();

    /// <summary>Gets the message options.</summary>
    public SogaMessageOptions Messages { get; } = new();

    /// <summary>Gets the synchronization options.</summary>
    public SogaSyncOptions Sync { get; } = new();

    /// <summary>Gets the transactional outbox options.</summary>
    public SogaOutboxOptions Outbox { get; } = new();

    /// <summary>Gets the security options.</summary>
    public SogaSecurityOptions Security { get; } = new();
}

/// <summary>Configures how the acting user is identified.</summary>
public sealed class SogaIdentityOptions
{
    /// <summary>Gets or sets the claim type that carries the host user ID.</summary>
    public string UserIdClaimType { get; set; } = ClaimTypes.NameIdentifier;
}

/// <summary>Configures conversation listing.</summary>
public sealed class SogaConversationOptions
{
    /// <summary>Gets or sets the page size used when a client does not request one.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>Gets or sets the largest page size a client may request.</summary>
    public int MaximumPageSize { get; set; } = 100;
}

/// <summary>Configures messages and message history.</summary>
public sealed class SogaMessageOptions
{
    /// <summary>Gets or sets the maximum message text length in UTF-16 code units.</summary>
    public int MaximumTextLength { get; set; } = 8_000;

    /// <summary>Gets or sets the history page size used when a client does not request one.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>Gets or sets the largest history page size a client may request.</summary>
    public int MaximumPageSize { get; set; } = 100;
}

/// <summary>Configures durable synchronization.</summary>
public sealed class SogaSyncOptions
{
    /// <summary>Gets or sets the batch size used when a client does not request one.</summary>
    public int DefaultBatchSize { get; set; } = 100;

    /// <summary>Gets or sets the largest batch a client may request.</summary>
    public int MaximumBatchSize { get; set; } = 500;
}

/// <summary>Configures the transactional outbox dispatcher.</summary>
public sealed class SogaOutboxOptions
{
    /// <summary>Gets or sets whether this process dispatches outbox events. Disable it on instances that must not publish.</summary>
    public bool DispatcherEnabled { get; set; } = true;

    /// <summary>Gets or sets how long the dispatcher sleeps when no events are pending.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets how many events one dispatch pass claims.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Gets or sets how long a claimed event is leased before another worker may retry it.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets how many delivery attempts an event receives before it is marked failed.</summary>
    public int MaximumAttempts { get; set; } = 10;

    /// <summary>Gets or sets the first retry delay. Later delays grow exponentially.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets or sets the largest retry delay.</summary>
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets how long published events are kept before cleanup.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Gets or sets the oldest pending event age at which health checks report degraded.</summary>
    public TimeSpan UnhealthyBacklogAge { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Configures request protection.</summary>
public sealed class SogaSecurityOptions
{
    /// <summary>Gets or sets the ASP.NET Core rate limiting policy applied to Soga endpoints, or <see langword="null"/> for none.</summary>
    public string? RateLimitPolicyName { get; set; }

    /// <summary>Gets or sets the largest accepted HTTP request body in bytes.</summary>
    public long MaximumRequestBodyBytes { get; set; } = 64 * 1024;
}
