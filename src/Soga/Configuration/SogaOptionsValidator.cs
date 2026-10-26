using Microsoft.Extensions.Options;

namespace Soga.Configuration;

/// <summary>
/// Validates <see cref="SogaOptions"/> when the application starts.
/// </summary>
/// <remarks>
/// Every rule is checked and every failure is reported together, so a misconfigured host fails fast with one complete
/// list of problems instead of one error per restart. Messages name the configuration key, for example
/// <c>Soga:Messages:MaximumTextLength</c>, so they can be fixed directly in <c>appsettings.json</c>.
/// </remarks>
internal sealed class SogaOptionsValidator : IValidateOptions<SogaOptions>
{
    /// <summary>
    /// Validates a <see cref="SogaOptions"/> instance.
    /// </summary>
    /// <param name="name">The name of the options instance being validated. Soga uses unnamed options.</param>
    /// <param name="options">The options to validate.</param>
    /// <returns>
    /// <see cref="ValidateOptionsResult.Success"/> when every rule passes; otherwise, a failed result listing every
    /// broken rule.
    /// </returns>
    public ValidateOptionsResult Validate(string? name, SogaOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.RoutePrefix) ||
            !options.RoutePrefix.StartsWith('/') ||
            options.RoutePrefix.EndsWith('/'))
        {
            failures.Add("Soga:RoutePrefix must start with '/' and must not end with '/'.");
        }

        if (string.IsNullOrWhiteSpace(options.DefaultTenantId) ||
            options.DefaultTenantId.Trim().Length > SogaOptions.MaximumTenantIdLength)
        {
            failures.Add($"Soga:DefaultTenantId is required and cannot exceed {SogaOptions.MaximumTenantIdLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(options.Identity.UserIdClaimType))
        {
            failures.Add("Soga:Identity:UserIdClaimType is required.");
        }

        ValidatePaging("Soga:Conversations", options.Conversations.DefaultPageSize, options.Conversations.MaximumPageSize, failures);
        ValidatePaging("Soga:Messages", options.Messages.DefaultPageSize, options.Messages.MaximumPageSize, failures);
        ValidatePaging("Soga:Sync", options.Sync.DefaultBatchSize, options.Sync.MaximumBatchSize, failures, "DefaultBatchSize", "MaximumBatchSize");

        if (options.Messages.MaximumTextLength is < 1 or > SogaOptions.AbsoluteMaximumTextLength)
        {
            failures.Add($"Soga:Messages:MaximumTextLength must be between 1 and {SogaOptions.AbsoluteMaximumTextLength}.");
        }

        if (options.Messages.EditWindow is { } window && window <= TimeSpan.Zero)
        {
            failures.Add("Soga:Messages:EditWindow must be greater than zero, or unset for no limit.");
        }

        if (options.Conversations.MaximumMetadataBytes is < 2 or > SogaOptions.AbsoluteMaximumMetadataBytes)
        {
            failures.Add($"Soga:Conversations:MaximumMetadataBytes must be between 2 and {SogaOptions.AbsoluteMaximumMetadataBytes}.");
        }

        if (options.Conversations.MaximumMetadataProperties is < 1 or > 100)
        {
            failures.Add("Soga:Conversations:MaximumMetadataProperties must be between 1 and 100.");
        }

        if (options.Groups.MaximumParticipants is < 2 or > 10_000)
        {
            failures.Add("Soga:Groups:MaximumParticipants must be between 2 and 10000.");
        }

        if (options.Groups.MaximumTitleLength is < 1 or > SogaOptions.AbsoluteMaximumTitleLength)
        {
            failures.Add($"Soga:Groups:MaximumTitleLength must be between 1 and {SogaOptions.AbsoluteMaximumTitleLength}.");
        }

        if (options.Typing.Timeout <= TimeSpan.Zero || options.Typing.MinimumBroadcastInterval < TimeSpan.Zero ||
            options.Typing.MinimumBroadcastInterval >= options.Typing.Timeout)
        {
            failures.Add("Soga:Typing:Timeout must be positive and greater than MinimumBroadcastInterval.");
        }

        if (options.Presence.OfflineGracePeriod < TimeSpan.Zero)
        {
            failures.Add("Soga:Presence:OfflineGracePeriod cannot be negative.");
        }

        var outbox = options.Outbox;
        if (outbox.PollInterval <= TimeSpan.Zero)
        {
            failures.Add("Soga:Outbox:PollInterval must be greater than zero.");
        }

        if (outbox.BatchSize is < 1 or > 500)
        {
            failures.Add("Soga:Outbox:BatchSize must be between 1 and 500.");
        }

        if (outbox.LeaseDuration <= TimeSpan.Zero)
        {
            failures.Add("Soga:Outbox:LeaseDuration must be greater than zero.");
        }

        if (outbox.MaximumAttempts < 1)
        {
            failures.Add("Soga:Outbox:MaximumAttempts must be at least 1.");
        }

        if (outbox.BaseRetryDelay <= TimeSpan.Zero || outbox.MaximumRetryDelay < outbox.BaseRetryDelay)
        {
            failures.Add("Soga:Outbox:BaseRetryDelay must be positive and not exceed MaximumRetryDelay.");
        }

        if (outbox.Retention <= TimeSpan.Zero)
        {
            failures.Add("Soga:Outbox:Retention must be greater than zero.");
        }

        if (options.Security.MaximumRequestBodyBytes is < 1024 or > 1_048_576)
        {
            failures.Add("Soga:Security:MaximumRequestBodyBytes must be between 1024 and 1048576.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Checks that a default page size is at least 1, does not exceed its maximum, and that the maximum is at most 500.
    /// </summary>
    /// <param name="section">The configuration section being validated, such as <c>Soga:Messages</c>.</param>
    /// <param name="defaultSize">The configured default page size.</param>
    /// <param name="maximumSize">The configured maximum page size.</param>
    /// <param name="failures">The list that receives a failure message when the rule is broken.</param>
    /// <param name="defaultName">The name of the default-size setting, used in the failure message.</param>
    /// <param name="maximumName">The name of the maximum-size setting, used in the failure message.</param>
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
