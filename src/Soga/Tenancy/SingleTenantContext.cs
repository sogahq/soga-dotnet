using Microsoft.Extensions.Options;
using Soga.Configuration;

namespace Soga.Tenancy;

/// <summary>
/// The default <see cref="ISogaTenantContext"/>, which places every operation in the single tenant named by
/// <see cref="SogaOptions.DefaultTenantId"/>.
/// </summary>
/// <param name="options">The Soga options that hold the default tenant ID.</param>
internal sealed class SingleTenantContext(IOptions<SogaOptions> options) : ISogaTenantContext
{
    /// <inheritdoc />
    public ValueTask<string> GetTenantIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(options.Value.DefaultTenantId.Trim());
    }
}
