using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Soga.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddAuthentication("Sample")
    .AddScheme<AuthenticationSchemeOptions, SampleAuthenticationHandler>("Sample", _ => { });
builder.Services.AddSoga();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapSoga();
app.MapGet("/", () => Results.Ok(new { name = "Soga minimal sample" }));
app.Run();

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
