using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Soga.Authorization;
using Soga.Extensibility;
using Soga.Extensions;
using Soga.Tenancy;

// Team chat: group conversations with owner/admin/member roles, edit and delete, replies, typing, presence, and host
// rules. Data lives in memory, so it disappears when the process stops (see Soga.MarketplaceSample for a database).
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication("Sample")
    .AddScheme<AuthenticationSchemeOptions, SampleAuthenticationHandler>("Sample", _ => { });

builder.Services.AddSoga(options =>
{
    options.Groups.MaximumParticipants = 50;
    options.Groups.PostSystemMessages = true;     // "carol was added" appears in the timeline as a machine-readable message
    options.Messages.EditWindow = TimeSpan.FromMinutes(10);
    options.Messages.RetainEditHistory = true;
    options.Presence.ExposeLastSeen = false;       // privacy: show online/offline, never "last seen at"
});

// Host rules. Soga checks roles first; these can only restrict further.
builder.Services.AddSingleton<ISogaAuthorizationPolicy, OnlyManagersCreateGroups>();
builder.Services.AddSingleton<ISogaMessageValidator, NoShoutingValidator>();
builder.Services.AddSingleton<ISogaAuditSink, ConsoleAuditSink>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapSoga();
app.MapHealthChecks("/health");

// Trusted host code posts a system message that participants cannot forge, edit, or delete.
app.MapPost("/projects/{conversationId:guid}/announce", async (
    Guid conversationId,
    AnnounceRequest request,
    ClaimsPrincipal user,
    ISogaSystemMessages system,
    ISogaTenantContext tenants,
    CancellationToken cancellationToken) =>
{
    if (!user.IsInRole("manager"))
    {
        return Results.Forbid();
    }

    var message = await system.SendAsync(await tenants.GetTenantIdAsync(cancellationToken), conversationId, request.Text, cancellationToken);
    return message is null ? Results.NotFound() : Results.Ok(message);
}).RequireAuthorization();

app.Run();

internal sealed record AnnounceRequest(string Text);

/// <summary>Only users with the manager role may create groups. Everything else is left to Soga.</summary>
internal sealed class OnlyManagersCreateGroups(IHttpContextAccessor http) : ISogaAuthorizationPolicy
{
    public ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(context.Action != SogaAction.CreateGroup || http.HttpContext?.User.IsInRole("manager") == true);
}

/// <summary>Rejects messages written entirely in capitals. The client only ever sees the machine-readable reason.</summary>
internal sealed class NoShoutingValidator : ISogaMessageValidator
{
    public ValueTask<SogaValidationResult> ValidateAsync(SogaMessageValidationContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(context.Text.Length > 8 && context.Text.Where(char.IsLetter).All(char.IsUpper)
            ? SogaValidationResult.Reject("all_caps")
            : SogaValidationResult.Accepted);
}

/// <summary>Writes audit entries to the console. Send them to your real audit store instead.</summary>
internal sealed class ConsoleAuditSink : ISogaAuditSink
{
    public ValueTask WriteAsync(SogaAuditEntry entry, CancellationToken cancellationToken)
    {
        Console.WriteLine($"AUDIT {entry.At:O} {entry.Action} actor={entry.ActorUserId} target={entry.TargetUserId} conversation={entry.ConversationId} {entry.Detail}");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Trusts X-Soga-User and X-Soga-Role headers. Never do this in production: use your real authentication scheme.</summary>
internal sealed class SampleAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Soga-User", out var user) || string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.ToString()) };
        if (Request.Headers.TryGetValue("X-Soga-Role", out var role) && !string.IsNullOrWhiteSpace(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
