using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Errors;
using Soga.Security;
using Soga.Tenancy;
using System.Threading.RateLimiting;

namespace Soga.IntegrationTests;

public sealed class HttpApiTests
{
    private const string P = SogaTestServer.Prefix;

    [Fact]
    public async Task Request_WithoutAuthentication_IsRejected()
    {
        await using var server = await SogaTestServer.StartAsync();

        var response = await server.Client(user: null).GetAsync($"{P}/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Context_ReturnsTrustedUserAndTenant()
    {
        await using var server = await SogaTestServer.StartAsync(o => o.DefaultTenantId = "acme");

        var context = await (await server.Client("alice").GetAsync($"{P}/context")).ReadAsync<JsonElement>();

        Assert.Equal("alice", context.GetProperty("userId").GetString());
        Assert.Equal("acme", context.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task DirectConversation_IsCreatedOnceThenReturned()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");

        var first = await alice.PostAsJsonAsync($"{P}/conversations/direct", new CreateDirectConversationRequest("bob"));
        var second = await server.Client("bob").PostAsJsonAsync($"{P}/conversations/direct", new CreateDirectConversationRequest("alice"));

        var created = await first.ReadAsync<ConversationResponse>();
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal($"{P}/conversations/{created.Id}", first.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(created.Id, (await second.ReadAsync<ConversationResponse>()).Id);
        Assert.Equal(["alice", "bob"], created.ParticipantUserIds);
    }

    [Fact]
    public async Task ConversationExchange_CoversSendRetryHistoryUnreadAndRead()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var bob = server.Client("bob");
        var conversation = await alice.OpenConversationAsync("bob");

        var sent = await alice.PostAsJsonAsync($"{P}/conversations/{conversation.Id}/messages", new SendMessageRequest("c1", "hello bob"));
        var retried = await alice.PostAsJsonAsync($"{P}/conversations/{conversation.Id}/messages", new SendMessageRequest("c1", "hello bob"));
        var conflict = await alice.PostAsJsonAsync($"{P}/conversations/{conversation.Id}/messages", new SendMessageRequest("c1", "different"));
        var message = await sent.ReadAsync<MessageResponse>();

        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(message.Id, (await retried.ReadAsync<MessageResponse>()).Id);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(SogaErrorCodes.IdempotencyConflict, await conflict.ErrorCodeAsync());

        var list = await (await bob.GetAsync($"{P}/conversations")).ReadAsync<ConversationResponse[]>();
        Assert.Equal(1, list.Single().UnreadCount);
        Assert.Equal(1, (await (await bob.GetAsync($"{P}/unread")).ReadAsync<UnreadResponse>()).TotalUnread);

        var history = await (await bob.GetAsync($"{P}/conversations/{conversation.Id}/messages")).ReadAsync<MessagePageResponse>();
        Assert.Equal("hello bob", history.Items.Single().Content);
        Assert.Null(history.NextCursor);

        var read = await bob.PostAsJsonAsync($"{P}/conversations/{conversation.Id}/read", new MarkReadRequest(message.Id));
        var state = await read.ReadAsync<ReadStateResponse>();
        Assert.Equal((1L, 0L), (state.LastReadSequence, state.UnreadCount));
        Assert.Equal(0, (await (await bob.GetAsync($"{P}/unread")).ReadAsync<UnreadResponse>()).TotalUnread);
    }

    [Fact]
    public async Task Send_IgnoresAnySenderSuppliedByTheClient()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");

        using var body = new StringContent(
            "{\"clientMessageId\":\"c1\",\"text\":\"spoof\",\"senderId\":\"bob\",\"tenantId\":\"other\"}",
            Encoding.UTF8,
            "application/json");
        var response = await alice.PostAsync($"{P}/conversations/{conversation.Id}/messages", body);

        Assert.Equal("alice", (await response.ReadAsync<MessageResponse>()).SenderId);
    }

    [Fact]
    public async Task Outsider_SeesIdenticalNotFoundForRealAndUnknownConversations()
    {
        await using var server = await SogaTestServer.StartAsync();
        var conversation = await server.Client("alice").OpenConversationAsync("bob");
        var carol = server.Client("carol");
        var real = conversation.Id;
        var unknown = Guid.NewGuid();

        async Task<(HttpStatusCode, string)> Probe(Func<Guid, Task<HttpResponseMessage>> call, Guid id)
        {
            var response = await call(id);
            return (response.StatusCode, await response.ErrorCodeAsync());
        }

        var probes = new Func<Guid, Task<HttpResponseMessage>>[]
        {
            id => carol.GetAsync($"{P}/conversations/{id}"),
            id => carol.GetAsync($"{P}/conversations/{id}/messages"),
            id => carol.PostAsJsonAsync($"{P}/conversations/{id}/messages", new SendMessageRequest("c1", "hi")),
            id => carol.PostAsJsonAsync($"{P}/conversations/{id}/read", new MarkReadRequest(Guid.NewGuid())),
        };

        foreach (var probe in probes)
        {
            var forReal = await Probe(probe, real);
            var forUnknown = await Probe(probe, unknown);

            Assert.Equal(forUnknown, forReal);
            Assert.Equal((HttpStatusCode.NotFound, SogaErrorCodes.ConversationNotFound), forReal);
        }
    }

    [Fact]
    public async Task Tenants_AreIsolatedEvenForTheSameUserIds()
    {
        await using var server = await SogaTestServer.StartAsync(
            services: s => s.AddScoped<ISogaTenantContext, HeaderTenantContext>());
        var inAcme = await server.Client("alice", "acme").OpenConversationAsync("bob");
        await server.Client("alice", "acme").SendAsync(inAcme.Id, "c1", "acme secret");

        var crossTenant = server.Client("alice", "globex");
        var get = await crossTenant.GetAsync($"{P}/conversations/{inAcme.Id}");
        var history = await crossTenant.GetAsync($"{P}/conversations/{inAcme.Id}/messages");
        var sameUsersElsewhere = await crossTenant.OpenConversationAsync("bob");
        var list = await (await crossTenant.GetAsync($"{P}/conversations")).ReadAsync<ConversationResponse[]>();

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
        Assert.Equal(sameUsersElsewhere.Id, list.Single().Id);
        Assert.NotEqual(inAcme.Id, sameUsersElsewhere.Id);
    }

    [Fact]
    public async Task UnresolvedTenant_IsRejectedWithStableCode()
    {
        await using var server = await SogaTestServer.StartAsync(
            services: s => s.AddScoped<ISogaTenantContext, HeaderTenantContext>());

        var response = await server.Client("alice").GetAsync($"{P}/conversations");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SogaErrorCodes.TenantUnavailable, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task UnexpectedFailure_ReturnsGenericProblemWithoutDetails()
    {
        await using var server = await SogaTestServer.StartAsync(
            services: s => s.AddScoped<ISogaTenantContext, ThrowingTenantContext>());

        var response = await server.Client("alice").GetAsync($"{P}/conversations");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(SogaErrorCodes.InternalError, text, StringComparison.Ordinal);
        Assert.DoesNotContain("database-password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowingTenantContext", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("null", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{\"clientMessageId\":\"bad id!\",\"text\":\"hi\"}", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{\"clientMessageId\":\"c1\",\"text\":\"   \"}", "application/json", HttpStatusCode.BadRequest)]
    public async Task Send_WithInvalidRequest_ReturnsInvalidRequestCode(string json, string contentType, HttpStatusCode expected)
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");

        using var body = new StringContent(json, Encoding.UTF8, contentType);
        var response = await alice.PostAsync($"{P}/conversations/{conversation.Id}/messages", body);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(SogaErrorCodes.InvalidRequest, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Send_WithNonJsonContentType_Returns415()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");

        using var body = new StringContent("{\"clientMessageId\":\"c1\",\"text\":\"hi\"}", Encoding.UTF8, "text/plain");
        var response = await alice.PostAsync($"{P}/conversations/{conversation.Id}/messages", body);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Send_WhenTextIsTooLong_Returns422WithStableCode()
    {
        await using var server = await SogaTestServer.StartAsync(o => o.Messages.MaximumTextLength = 5);
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");

        var response = await alice.PostAsJsonAsync(
            $"{P}/conversations/{conversation.Id}/messages",
            new SendMessageRequest("c1", "123456"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(SogaErrorCodes.MessageTooLong, await response.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Send_WhenBodyIsOversized_Returns413(bool declareContentLength)
    {
        await using var server = await SogaTestServer.StartAsync(o => o.Security.MaximumRequestBodyBytes = 1024);
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");
        var payload = "{\"clientMessageId\":\"c1\",\"text\":\"" + new string('a', 4096) + "\"}";

        // A non-seekable stream makes HttpClient send chunked with no Content-Length, so only the bounded reader can stop it.
        using HttpContent body = declareContentLength
            ? new StringContent(payload, Encoding.UTF8, "application/json")
            : new StreamContent(new ForwardOnlyStream(Encoding.UTF8.GetBytes(payload)));
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await alice.PostAsync($"{P}/conversations/{conversation.Id}/messages", body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task History_WithTamperedOrForeignCursor_ReturnsInvalidCursor()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");
        for (var i = 1; i <= 3; i++)
        {
            await alice.SendAsync(conversation.Id, $"c{i}", "x");
        }

        var page = await (await alice.GetAsync($"{P}/conversations/{conversation.Id}/messages?limit=1")).ReadAsync<MessagePageResponse>();
        var other = await alice.OpenConversationAsync("carol");

        var tampered = await alice.GetAsync($"{P}/conversations/{conversation.Id}/messages?cursor={page.NextCursor}x");
        var foreign = await alice.GetAsync($"{P}/conversations/{other.Id}/messages?cursor={page.NextCursor}");

        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        Assert.Equal(SogaErrorCodes.CursorInvalid, await tampered.ErrorCodeAsync());
        Assert.Equal(SogaErrorCodes.CursorInvalid, await foreign.ErrorCodeAsync());
    }

    [Theory]
    [InlineData("limit=abc")]
    [InlineData("limit=0")]
    [InlineData("limit=-5")]
    public async Task History_WithInvalidLimit_ReturnsInvalidRequest(string query)
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");

        var response = await alice.GetAsync($"{P}/conversations/{conversation.Id}/messages?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SogaErrorCodes.InvalidRequest, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Sync_RecoversMessagesAcceptedWhileTheClientWasAway()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var bob = server.Client("bob");
        var start = await (await bob.GetAsync($"{P}/sync")).ReadAsync<SyncResponse>();
        var conversation = await alice.OpenConversationAsync("bob");
        await alice.SendAsync(conversation.Id, "c1", "while you were away");

        var caughtUp = await (await bob.GetAsync($"{P}/sync?cursor={Uri.EscapeDataString(start.Cursor)}")).ReadAsync<SyncResponse>();
        var again = await (await bob.GetAsync($"{P}/sync?cursor={Uri.EscapeDataString(caughtUp.Cursor)}")).ReadAsync<SyncResponse>();

        Assert.Equal(
            [SogaEventTypes.ConversationCreated, SogaEventTypes.MessageCreated],
            caughtUp.Events.Select(e => e.Type));
        Assert.All(caughtUp.Events, e => Assert.Equal(SogaProtocol.Version, e.ProtocolVersion));
        Assert.Empty(again.Events);
    }

    [Fact]
    public async Task Sync_WithInvalidCursor_ReturnsInvalidCursor()
    {
        await using var server = await SogaTestServer.StartAsync();

        var response = await server.Client("alice").GetAsync($"{P}/sync?cursor=garbage");

        Assert.Equal(SogaErrorCodes.CursorInvalid, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Health_WhenStoreIsReachable_IsHealthy()
    {
        await using var server = await SogaTestServer.StartAsync();

        var report = await server.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Contains("soga", report.Entries.Keys);
    }

    [Fact]
    public async Task RateLimit_ReturnsStableCodeAndRetryAfter()
    {
        await using var server = await SogaTestServer.StartAsync(
            configure: o => o.Security.RateLimitPolicyName = "soga",
            services: s => s.AddRateLimiter(limiter =>
            {
                limiter.UseSogaRejectionResponse();
                limiter.AddFixedWindowLimiter("soga", window =>
                {
                    window.PermitLimit = 2;
                    window.Window = TimeSpan.FromMinutes(1);
                    window.QueueLimit = 0;
                });
            }),
            beforeMap: app => app.UseRateLimiter());
        var alice = server.Client("alice");

        await alice.GetAsync($"{P}/conversations");
        await alice.GetAsync($"{P}/conversations");
        var limited = await alice.GetAsync($"{P}/conversations");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(SogaErrorCodes.RateLimited, await limited.ErrorCodeAsync());
    }

    [Fact]
    public async Task Startup_WithInvalidConfiguration_Fails()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(
            () => SogaTestServer.StartAsync(o => o.RoutePrefix = "no-leading-slash"));
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingTenantContext : ISogaTenantContext
    {
        public ValueTask<string> GetTenantIdAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("database-password=hunter2");
    }
}
