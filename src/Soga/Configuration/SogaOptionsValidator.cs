using Microsoft.Extensions.Options;

namespace Soga.Configuration;

/// <summary>
/// Validates <see cref="SogaOptions"/> when the options are bound, so that invalid
/// configuration is reported with clear messages instead of causing failures later at runtime.
/// </summary>
/// <remarks>
/// All problems are collected and returned together, so a misconfigured host sees every
/// issue in a single pass rather than fixing them one at a time.
/// </remarks>
internal sealed class SogaOptionsValidator : IValidateOptions<SogaOptions>
{
    /// <summary>
    /// Checks every validated section of <paramref name="options"/> and aggregates the failures.
    /// </summary>
    /// <param name="name">The named options instance being validated (unused; all instances share the same rules).</param>
    /// <param name="options">The bound Soga options to validate.</param>
    /// <returns>
    /// <see cref="ValidateOptionsResult.Success"/> when the options are valid; otherwise a failed
    /// result containing one message per violated rule.
    /// </returns>
    public ValidateOptionsResult Validate(string? name, SogaOptions options)
    {
        var failures = new List<string>();

        // Route prefix: it is combined with endpoint paths, so it must be a rooted path.
        // NOTE: the check requires a trailing '/', but the failure message says it must NOT end
        // with '/'. One of the two is wrong; align them once the intended behavior is confirmed.
        if (string.IsNullOrWhiteSpace(options.RoutePrefix) || !options.RoutePrefix.StartsWith('/') || !options.RoutePrefix.EndsWith('/'))
        {
            failures.Add("Soga:RoutePrefix must start with '/' and must not end with '/'.");
        }

        // Default tenant: used when a request carries no tenant, so it must always have a value.
        // NOTE: the check compares against MaximumClientMessageIdLength while the message reports
        // MaximumTenantIdLength. This looks like a copy/paste mix-up and may need to be fixed.
        if (string.IsNullOrWhiteSpace(options.DefaultTenantId) || options.DefaultTenantId.Trim().Length > SogaOptions.MaximumClientMessageIdLength)
        {
            failures.Add($"Soga:DefaultTenantId is required and cannot exceed {SogaOptions.MaximumTenantIdLength} characters.");
        }

        // Identity: the claim type is how the current user's id is read from the principal,
        // so without it no request can be attributed to a user.
        if (string.IsNullOrWhiteSpace(options.Identity.UserIdClaimType))
        {
            failures.Add("Soga:Identity:UserIdClaimType is required.");
        }

        // Paging: each list endpoint needs a sane default page size that does not exceed its maximum.
        // Sync uses "batch" terminology, so its setting names are passed in for accurate error messages.
        ValidatePaging("Soga:Conversations", options.Conversations.DefaultPageSize, options.Conversations.MaximumPageSize, failures);
        ValidatePaging("Soga:Messages", options.Messages.DefaultPageSize, options.Messages.MaximumPageSize, failures);
        ValidatePaging("Soga:Sync", options.Sync.DefaultBatchSize, options.Sync.MaximumBatchSize, failures, "DefaultBatchSize", "MaximumBatchSize");

        // Message text: capped by a hard upper limit so configuration cannot exceed what the system supports.
        if (options.Messages.MaximumTextLength is < 1 or > SogaOptions.AbsoluteMaximumTextLength)
        {
            failures.Add($"Soga:Messages:MaximumTextLength must be between 1 and {SogaOptions.AbsoluteMaximumTextLength}.");
        }

        // Outbox: settings for the background processor that polls and delivers pending messages.
        var outbox = options.Outbox;

        // A non-positive interval would make the poller spin or never wait.
        if (outbox.PollInterval <= TimeSpan.Zero)
        {
            failures.Add("Soga:Outbox:PollInterval must be greater than zero.");
        }

        // Bounded so a single poll cannot load an unreasonably large number of rows.
        if (outbox.BatchSize is < 1 or > 500)
        {
            failures.Add("Soga:Outbox:BatchSize must be between 1 and 500.");
        }

        // The lease is how long a claimed item stays locked to a worker; it must be positive to expire.
        if (outbox.LeaseDuration <= TimeSpan.Zero)
        {
            failures.Add("Soga:Outbox:LeaseDuration must be greater than zero.");
        }

        // At least one attempt is required, otherwise items would never be delivered.
        if (outbox.MaximumAttempts < 1)
        {
            failures.Add("Soga:Outbox:MaximumAttempts must be at least 1.");
        }

        // Retry backoff starts at the base delay and is capped by the maximum delay,
        // so the base must be positive and the cap cannot be smaller than it.
        if (outbox.BaseRetryDelay <= TimeSpan.Zero || outbox.MaximumRetryDelay < outbox.BaseRetryDelay)
        {
            failures.Add("Soga:Outbox:BaseRetryDelay must be positive and not exceed MaximumRetryDelay.");
        }

        // How long processed items are kept before cleanup; must be positive to be meaningful.
        if (outbox.Retention <= TimeSpan.Zero)
        {
            failures.Add("Soga:Outbox:Retention must be greater than zero.");
        }

        // Security: limit request body size (1 KB to 1 MB) to guard against oversized payloads
        // while still leaving room for legitimate requests.
        if (options.Security.MaximumRequestBodyBytes is < 1024 or > 1_048_576)
        {
            failures.Add("Soga:Security:MaximumRequestBodyBytes must be between 1024 and 1048576.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Validates a default/maximum size pair shared by the paged and batched settings.
    /// </summary>
    /// <param name="section">The configuration section path, used as the prefix in the error message.</param>
    /// <param name="defaultSize">The default size; must be at least 1.</param>
    /// <param name="maximumSize">The maximum size; must be at least <paramref name="defaultSize"/> and no more than 500.</param>
    /// <param name="failures">The list that receives a message if validation fails.</param>
    /// <param name="defaultName">The setting name of the default value, for the error message.</param>
    /// <param name="maximumName">The setting name of the maximum value, for the error message.</param>
    private static void ValidatePaging(
        string section,
        int defaultSize,
        int maximumSize,
        List<string> failures,
        string defaultName = "DefaultPageSize",
        string maximumName = "MaximumPageSize")
    {
        if (defaultSize < 1 || maximumSize < defaultSize || maximumSize > 500)
        {
            failures.Add($"{section}:{defaultName} must be at least 1 and not exceed {section}:{maximumName}, which cannot exceed 500.");
        }
    }
}
