namespace Soga.Tenancy;

/// <summary>
/// Resolves the active tenant from trusted host context.
/// </summary>
/// <remarks>
/// Every Soga row and every query is scoped to a tenant. Single-tenant applications can rely on the default
/// implementation, which returns <see cref="Configuration.SogaOptions.DefaultTenantId"/>. Multi-tenant applications
/// implement this interface to select the tenant from a trusted source such as a claim, subdomain, or route value,
/// never from a request body.
/// </remarks>
public interface ISogaTenantContext
{
    /// <summary>
    /// Gets the ID of the tenant the current operation runs in.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the tenant ID. An empty value
    /// causes the request to fail with <c>soga.tenant.unavailable</c>.
    /// </returns>
    ValueTask<string> GetTenantIdAsync(CancellationToken cancellationToken = default);
}
