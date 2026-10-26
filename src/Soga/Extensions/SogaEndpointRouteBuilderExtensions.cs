using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Conversations.Endpoints;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Groups.Endpoints;
using Soga.Identity;
using Soga.Messages.Endpoints;
using Soga.ReadState.Endpoints;
using Soga.Realtime;
using Soga.Synchronization.Endpoints;

namespace Soga.Extensions;

/// <summary>
/// Provides extension methods for mapping the Soga HTTP API and SignalR hub into an ASP.NET Core application.
/// </summary>
public static class SogaEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the Soga HTTP endpoints under <see cref="SogaOptions.RoutePrefix"/>, and the SignalR hub at
    /// <c>{prefix}/hub</c>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to add the Soga endpoints to.</param>
    /// <returns>
    /// The <see cref="RouteGroupBuilder"/> that contains every Soga endpoint, so the host can add its own conventions,
    /// such as CORS or OpenAPI metadata.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">The Soga options are invalid.</exception>
    /// <remarks>
    /// <para>
    /// Every endpoint requires an authenticated user. Call <c>UseAuthentication()</c> and <c>UseAuthorization()</c>
    /// before this method.
    /// </para>
    /// <para>
    /// When <see cref="SogaSecurityOptions.RateLimitPolicyName"/> is set, the named rate-limit policy is applied to the
    /// whole group. Unexpected exceptions are logged and returned as a generic <c>soga.internal_error</c> response that
    /// never contains exception details.
    /// </para>
    /// </remarks>
    public static RouteGroupBuilder MapSoga(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Reading Value validates the options, so invalid configuration fails application startup.
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<SogaOptions>>().Value;
        var group = endpoints.MapGroup(options.RoutePrefix).RequireAuthorization().WithTags("Soga");

        if (options.Security.RateLimitPolicyName is { Length: > 0 } policy)
        {
            group.RequireRateLimiting(policy);
        }

        group.AddEndpointFilter(HandleUnexpectedErrorsAsync);

        ConversationEndpoints.Map(group);
        GroupEndpoints.Map(group);
        MessageEndpoints.Map(group);
        ReadStateEndpoints.Map(group);
        SyncEndpoints.Map(group);

        group.MapGet("/context", GetContextAsync)
            .WithName("SogaGetContext")
            .WithSummary("Returns the trusted Soga execution context.");

        group.MapHub<SogaHub>("/hub");
        return group;
    }

    /// <summary>
    /// An endpoint filter that turns unexpected exceptions into a safe <c>soga.internal_error</c> response.
    /// </summary>
    /// <param name="context">The endpoint invocation context.</param>
    /// <param name="next">The next filter or the endpoint itself.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is the endpoint result, a 499 status when
    /// the client disconnected, or a 500 Problem Details response when the endpoint threw.
    /// </returns>
    private static async ValueTask<object?> HandleUnexpectedErrorsAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        try
        {
            return await next(context);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception exception)
        {
            // Log the failure with correlation data; never return exception details to the client.
            SogaLog.EndpointFailure(
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Soga.Endpoints"),
                exception);
            return SogaHttp.Problem(http, SogaErrors.InternalError);
        }
    }

    /// <summary>
    /// Handles <c>GET {prefix}/context</c>, which returns the user and tenant Soga resolved for the caller.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="resolver">The resolver for the trusted caller.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is a 200 response with the caller's user
    /// and tenant IDs, or a Problem Details response when they cannot be resolved.
    /// </returns>
    /// <remarks>This endpoint helps hosts confirm that their authentication and tenant integration work.</remarks>
    private static async Task<IResult> GetContextAsync(HttpContext http, SogaCallerResolver resolver)
    {
        var caller = await resolver.ResolveAsync(principal: null, http.RequestAborted);
        return caller.IsSuccess
            ? Results.Ok(new SogaContextResponse(caller.Value.UserId, caller.Value.TenantId))
            : SogaHttp.Problem(http, caller.Error!);
    }

    /// <summary>
    /// Represents the response body of the <c>/context</c> endpoint.
    /// </summary>
    /// <param name="UserId">The resolved user ID.</param>
    /// <param name="TenantId">The resolved tenant ID.</param>
    private sealed record SogaContextResponse(string UserId, string TenantId);
}
