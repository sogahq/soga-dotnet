using Microsoft.Extensions.Options;
using Soga.Configuration;

namespace Soga.Tenancy;

internal sealed class SingleTenantContext(IOptions<SogaOptions> options) : ISogaTenantContext
{
    public ValueTask<string> GetTenantIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(options.Value.DefaultTenantId.Trim());
    }
}
