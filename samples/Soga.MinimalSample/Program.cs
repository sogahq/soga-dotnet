using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Soga.Extensions;

// The smallest possible host: header-based sample authentication and Soga's in-memory development provider.
// Data is lost when the process stops. See Soga.MarketplaceSample for durable EF Core storage.
var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddAuthentication("Sample")
    .AddScheme<AuthenticationSchemeOptions, SampleAuthenticationHandler>("Sample", _ => { });
builder.Services.AddSoga(options =>
{
    options.RoutePrefix = "/soga/v1";
    options.Messages.MaximumTextLength = 8_000;
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapSoga();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new
{
    name = "Soga minimal sample",
    try_it = "curl -H 'X-Soga-User: alice' http://localhost:5272/soga/v1/context",
}));
app.Run();

/// <summary>Trusts an X-Soga-User header. Never do this in production: use your real authentication scheme.</summary>
internal sealed class SampleAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Soga-User", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new System.Security.Claims.ClaimsIdentity(
            [new(System.Security.Claims.ClaimTypes.NameIdentifier, value.ToString())],
            Scheme.Name);
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

public partial class Program;
