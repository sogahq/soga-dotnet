using Microsoft.Extensions.Options;

namespace Soga.Configuration;

internal sealed class SogaOptionsValidator : IValidateOptions<SogaOptions>
{
    public ValidateOptionsResult Validate(string? name, SogaOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RoutePrefix) ||
            !options.RoutePrefix.StartsWith('/') ||
            options.RoutePrefix.EndsWith('/'))
        {
            return ValidateOptionsResult.Fail("Soga:RoutePrefix must start with '/' and must not end with '/'.");
        }

        if (string.IsNullOrWhiteSpace(options.DefaultTenantId) ||
            options.DefaultTenantId.Length > SogaOptions.MaximumTenantIdLength)
        {
            return ValidateOptionsResult.Fail($"Soga:DefaultTenantId is required and cannot exceed {SogaOptions.MaximumTenantIdLength} characters.");
        }

        return ValidateOptionsResult.Success;
    }
}
