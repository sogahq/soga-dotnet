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

    /// <summary>Gets or sets the versioned route prefix.</summary>
    public string RoutePrefix { get; set; } = "/soga/v1";

    /// <summary>Gets or sets the tenant used by a single-tenant host.</summary>
    public string DefaultTenantId { get; set; } = "default";
}
