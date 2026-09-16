namespace Soga.Tenancy;

/// <summary>Resolves the active tenant from trusted host context.</summary>
public interface ISogaTenantContext
{
    /// <summary>Returns the normalized tenant ID selected by trusted host logic.</summary>
    ValueTask<string> GetTenantIdAsync(CancellationToken cancellationToken = default);
}
