using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Identity;
using Soga.Tenancy;

namespace Soga.Extensions;

/// <summary>Maps the Soga HTTP protocol into an ASP.NET Core host.</summary>
public static class SogaEndpointRouteBuilderExtensions
{
    /// <summary>Maps authenticated, versioned Soga endpoints.</summary>
    public static RouteGroupBuilder MapSoga(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<SogaOptions>>().Value;
        var group = endpoints.MapGroup(options.RoutePrefix).RequireAuthorization().WithTags("Soga");

        group.MapGet("/context", GetContextAsync)
            .WithName("SogaGetContext")
            .WithSummary("Returns the trusted Soga execution context.");
        return group;
    }

    private static async Task<IResult> GetContextAsync(
        ISogaUserContext users,
        ISogaTenantContext tenants,
        CancellationToken cancellationToken)
    {
        var userId = await users.GetUserIdAsync(cancellationToken);
        if (userId is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Authentication required",
                extensions: new Dictionary<string, object?> { ["code"] = "soga.authentication.required" });
        }

        return TypedResults.Ok(new SogaContextResponse(
            userId,
            await tenants.GetTenantIdAsync(cancellationToken)));
    }

    private sealed record SogaContextResponse(string UserId, string TenantId);
}
