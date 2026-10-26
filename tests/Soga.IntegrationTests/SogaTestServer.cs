using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Extensions;
using Soga.Tenancy;

namespace Soga.IntegrationTests;

/// <summary>Hosts Soga in a real ASP.NET Core pipeline with a real authentication handler.</summary>
internal sealed class SogaTestServer : IAsyncDisposable
{
    public const string UserHeader = "X-Test-User";
    public const string TenantHeader = "X-Test-Tenant";
    public const string Prefix = "/soga/v1";

    private readonly WebApplication _app;

    private SogaTestServer(WebApplication app) => _app = app;

    public TestServer Server => _app.GetTestServer();

    public IServiceProvider Services => _app.Services;

    public static async Task<SogaTestServer> StartAsync(
        Action<SogaOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? beforeMap = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddAuthentication(HeaderAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderAuthHandler.SchemeName, _ => { });

        builder.Services.AddSoga(options =>
        {
            options.Outbox.PollInterval = TimeSpan.FromMilliseconds(50);
            configure?.Invoke(options);
        });
        services?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        beforeMap?.Invoke(app);
        app.MapSoga();

        await app.StartAsync();
        return new SogaTestServer(app);
    }

    public HttpClient Client(string? user, string? tenant = null)
    {
        var client = Server.CreateClient();
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(UserHeader, user);
        }

        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add(TenantHeader, tenant);
        }

        return client;
    }

    public HubConnection Hub(string? user, string? tenant = null) => new HubConnectionBuilder()
        .WithUrl(
            new Uri(Server.BaseAddress, $"{Prefix}/hub"),
            options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                if (user is not null)
                {
                    options.Headers[UserHeader] = user;
                }

                if (tenant is not null)
                {
                    options.Headers[TenantHeader] = tenant;
                }
            })
        .Build();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class HeaderAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestHeader";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SogaTestServer.UserHeader, out var user) || string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new System.Security.Claims.ClaimsIdentity(
            [new(System.Security.Claims.ClaimTypes.NameIdentifier, user.ToString())],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>A host-supplied tenant resolver that trusts a header. Real hosts would derive it from a token or subdomain.</summary>
internal sealed class HeaderTenantContext(IHttpContextAccessor accessor) : ISogaTenantContext
{
    public ValueTask<string> GetTenantIdAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(accessor.HttpContext?.Request.Headers[SogaTestServer.TenantHeader].ToString() ?? string.Empty);
}

internal static class HttpExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<T>();
        return body!;
    }

    public static async Task<string> ErrorCodeAsync(this HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("code").GetString()!;
    }

    public static async Task<ConversationResponse> OpenConversationAsync(
        this HttpClient client,
        string otherUser,
        string? externalReference = null)
    {
        var response = await client.PostAsJsonAsync(
            $"{SogaTestServer.Prefix}/conversations/direct",
            new CreateDirectConversationRequest(otherUser, externalReference));
        response.EnsureSuccessStatusCode();
        return await response.ReadAsync<ConversationResponse>();
    }

    public static async Task<MessageResponse> SendAsync(
        this HttpClient client,
        Guid conversationId,
        string clientMessageId,
        string text)
    {
        var response = await client.PostAsJsonAsync(
            $"{SogaTestServer.Prefix}/conversations/{conversationId}/messages",
            new SendMessageRequest(clientMessageId, text));
        response.EnsureSuccessStatusCode();
        return await response.ReadAsync<MessageResponse>();
    }
}

/// <summary>Collects realtime events from a hub connection so tests can await them.</summary>
internal sealed class EventInbox
{
    private static readonly string[] EventNames =
    [
        SogaEventTypes.MessageCreated,
        SogaEventTypes.ReadPositionChanged,
        SogaEventTypes.ConversationCreated,
        SogaEventTypes.ConversationAccessChanged,
    ];

    private readonly Channel<SogaEvent> _events = Channel.CreateUnbounded<SogaEvent>();

    public EventInbox(HubConnection connection)
    {
        foreach (var name in EventNames)
        {
            connection.On<SogaEvent>(name, evt => _events.Writer.TryWrite(evt));
        }
    }

    public async Task<SogaEvent> NextAsync(string? expectedType = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var evt = await _events.Reader.ReadAsync(timeout.Token);
            if (expectedType is null || evt.Type == expectedType)
            {
                return evt;
            }
        }
    }

    /// <summary>Returns true when no event arrives within the quiet period.</summary>
    public async Task<bool> StaysQuietAsync(TimeSpan quiet)
    {
        using var timeout = new CancellationTokenSource(quiet);
        try
        {
            await _events.Reader.WaitToReadAsync(timeout.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}
