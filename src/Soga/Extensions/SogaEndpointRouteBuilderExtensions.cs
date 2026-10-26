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
using Soga.Identity;
using Soga.Messages.Endpoints;
using Soga.ReadState.Endpoints;
using Soga.Realtime;
using Soga.Synchronization.Endpoints;

namespace Soga.Extensions;

/// <summary>Maps the Soga HTTP protocol and SignalR hub into an ASP.NET Core host.</summary>
public static class SogaEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps authenticated, versioned Soga endpoints under <see cref="SogaOptions.RoutePrefix"/>, plus the hub at
    /// <c>{prefix}/hub</c>. The host must call <c>UseAuthentication()</c> and <c>UseAuthorization()</c> first.
    /// </summary>
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
        MessageEndpoints.Map(group);
        ReadStateEndpoints.Map(group);
        SyncEndpoints.Map(group);

        group.MapGet("/context", GetContextAsync)
            .WithName("SogaGetContext")
            .WithSummary("Returns the trusted Soga execution context.");

        group.MapHub<SogaHub>("/hub");
        return group;
    }

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

    private static async Task<IResult> GetContextAsync(HttpContext http, SogaCallerResolver resolver)
    {
        var caller = await resolver.ResolveAsync(principal: null, http.RequestAborted);
        return caller.IsSuccess
            ? Results.Ok(new SogaContextResponse(caller.Value.UserId, caller.Value.TenantId))
            : SogaHttp.Problem(http, caller.Error!);
    }

    private sealed record SogaContextResponse(string UserId, string TenantId);
}
