using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Soga.Contracts;
using Soga.Errors;

namespace Soga.IntegrationTests;

/// <summary>
/// Checks the same contract on every endpoint: authentication, body handling, outsider privacy, and the shape of every
/// error. A new endpoint that forgets one of these rules shows up here.
/// </summary>
public sealed class EndpointContractMatrixTests
{
    private const string P = SogaTestServer.Prefix;

    /// <summary>Method, route template, and a valid JSON body (null when the endpoint takes none).</summary>
    public static TheoryData<string, string, string?> AllEndpoints => new()
    {
        { "POST", "/conversations/direct", """{"otherUserId":"erin"}""" },
        { "GET", "/conversations", null },
        { "GET", "/conversations/{id}", null },
        { "PATCH", "/conversations/{id}", """{"title":"Renamed"}""" },
        { "POST", "/conversations/{id}/archive", null },
        { "POST", "/conversations/{id}/unarchive", null },
        { "POST", "/conversations/groups", """{"title":"Team","participantUserIds":["erin"]}""" },
        { "POST", "/conversations/{id}/participants", """{"userId":"erin"}""" },
        { "DELETE", "/conversations/{id}/participants/carol", null },
        { "PUT", "/conversations/{id}/participants/bob/role", """{"role":"admin"}""" },
        { "GET", "/conversations/{id}/messages", null },
        { "POST", "/conversations/{id}/messages", """{"clientMessageId":"x1","text":"hi"}""" },
        { "PATCH", "/conversations/{id}/messages/{msg}", """{"text":"edited"}""" },
        { "DELETE", "/conversations/{id}/messages/{msg}", null },
        { "POST", "/conversations/{id}/read", """{"messageId":"{msg}"}""" },
        { "GET", "/unread", null },
        { "GET", "/sync", null },
        { "GET", "/context", null },
    };

    public static TheoryData<string, string, string?> BodyEndpoints => Filter(e => e.Body is not null);

    public static TheoryData<string, string, string?> ConversationEndpoints => Filter(e => e.Route.Contains("{id}", StringComparison.Ordinal));

    public static TheoryData<string> PagedEndpoints => ["/conversations", "/conversations/{id}/messages", "/sync"];

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task Endpoint_WithoutAuthentication_Returns401(string method, string route, string? body)
    {
        await using var server = await SogaTestServer.StartAsync();

        var response = await SendAsync(server.Client(user: null), method, Resolve(route, Guid.NewGuid(), Guid.NewGuid()), body, "application/json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task Endpoint_ForAParticipantWithValidInput_Succeeds(string method, string route, string? body)
    {
        await using var server = await SogaTestServer.StartAsync();
        var (group, message) = await ArrangeAsync(server);

        var response = await SendAsync(server.Client("alice"), method, Resolve(route, group, message), ResolveBody(body, group, message), "application/json");

        Assert.True(response.IsSuccessStatusCode, $"{method} {route} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [MemberData(nameof(BodyEndpoints))]
    public async Task Endpoint_WithMalformedJson_Returns400InvalidRequest(string method, string route, string? body)
    {
        _ = body;
        await using var server = await SogaTestServer.StartAsync();
        var (group, message) = await ArrangeAsync(server);

        var response = await SendAsync(server.Client("alice"), method, Resolve(route, group, message), "{\"broken\":", "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SogaErrorCodes.InvalidRequest, await response.ErrorCodeAsync());
    }

    [Theory]
    [MemberData(nameof(BodyEndpoints))]
    public async Task Endpoint_WithJsonOfTheWrongShape_Returns400InvalidRequest(string method, string route, string? body)
    {
        _ = body;
        await using var server = await SogaTestServer.StartAsync();
        var (group, message) = await ArrangeAsync(server);

        var response = await SendAsync(server.Client("alice"), method, Resolve(route, group, message), "[1,2,3]", "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SogaErrorCodes.InvalidRequest, await response.ErrorCodeAsync());
    }

    [Theory]
    [MemberData(nameof(BodyEndpoints))]
    public async Task Endpoint_WithAnEmptyJsonObject_IsRejectedWithoutAServerError(string method, string route, string? body)
    {
        _ = body;
        await using var server = await SogaTestServer.StartAsync();
        var (group, message) = await ArrangeAsync(server);

        var response = await SendAsync(server.Client("alice"), method, Resolve(route, group, message), "{}", "application/json");

        Assert.True((int)response.StatusCode is >= 400 and < 500, $"{method} {route} returned {(int)response.StatusCode}");
        Assert.StartsWith("soga.", await response.ErrorCodeAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BodyEndpoints))]
    public async Task Endpoint_WithJsonNull_IsRejectedWithoutAServerError(string method, string route, string? body)
    {
        _ = body;
        await using var server = await SogaTestServer.StartAsync();
        var (group, message) = await ArrangeAsync(server);

        var response = await SendAsync(server.Client("alice"), method, Resolve(route, group, message), "null", "application/json");

        Assert.True((int)response.StatusCode is >= 400 and < 500, $"{method} {route} returned {(int)response.StatusCode}");
    }

    [Theory]
    [MemberData(nameof(BodyEndpoints))]
    public async Task Endpoint_WithANonJsonContentType_IsRejected(string method, string route, string? body)
    {
        await using var server = await SogaTestServer.StartAsync();
        var (group, message) = await ArrangeAsync(server);

        var response = await SendAsync(server.Client("alice"), method, Resolve(route, group, message), ResolveBody(body, group, message), "text/plain");

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.UnsupportedMediaType, HttpStatusCode.BadRequest });
    }

    [Theory]
    [MemberData(nameof(BodyEndpoints))]
    public async Task Endpoint_WithAnOversizedBody_Returns413(string method, string route, string? body)
    {
        _ = body;
        await using var server = await SogaTestServer.StartAsync(o => o.Security.MaximumRequestBodyBytes = 1024);
        var (group, message) = await ArrangeAsync(server);
        var oversized = "{\"padding\":\"" + new string('a', 4096) + "\"}";

        var response = await SendAsync(server.Client("alice"), method, Resolve(route, group, message), oversized, "application/json");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ConversationEndpoints))]
    public async Task Endpoint_ForAnOutsider_LooksExactlyLikeAConversationThatDoesNotExist(string method, string route, string? body)
    {
        await using var server = await SogaTestServer.StartAsync();
        var (group, message) = await ArrangeAsync(server);
        var outsider = server.Client("dave");

        var real = await SendAsync(outsider, method, Resolve(route, group, message), ResolveBody(body, group, message), "application/json");
        var unknown = await SendAsync(outsider, method, Resolve(route, Guid.NewGuid(), message), ResolveBody(body, Guid.NewGuid(), message), "application/json");

        Assert.Equal(HttpStatusCode.NotFound, real.StatusCode);
        Assert.Equal(unknown.StatusCode, real.StatusCode);
        Assert.Equal(await WithoutTraceIdAsync(unknown), await WithoutTraceIdAsync(real));
    }

    [Theory]
    [MemberData(nameof(ConversationEndpoints))]
    public async Task Endpoint_WithAnIdThatIsNotAGuid_IsNotRouted(string method, string route, string? body)
    {
        await using var server = await SogaTestServer.StartAsync();

        var response = await SendAsync(server.Client("alice"), method, route.Replace("{id}", "not-a-guid", StringComparison.Ordinal).Replace("{msg}", Guid.NewGuid().ToString(), StringComparison.Ordinal), body, "application/json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(PagedEndpoints))]
    public async Task PagedEndpoint_WithANonNumericOrNegativeLimit_Returns400(string route)
    {
        await using var server = await SogaTestServer.StartAsync();
        var (group, message) = await ArrangeAsync(server);
        var alice = server.Client("alice");

        foreach (var limit in new[] { "abc", "-1", "1.5", "0" })
        {
            var response = await alice.GetAsync($"{P}{Resolve(route, group, message)}?limit={limit}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(SogaErrorCodes.InvalidRequest, await response.ErrorCodeAsync());
        }
    }

    [Theory]
    [MemberData(nameof(PagedEndpoints))]
    public async Task PagedEndpoint_WithAGarbageCursor_Returns400InvalidCursor(string route)
    {
        await using var server = await SogaTestServer.StartAsync();
        var (group, message) = await ArrangeAsync(server);

        var response = await server.Client("alice").GetAsync($"{P}{Resolve(route, group, message)}?cursor=not-a-real-cursor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SogaErrorCodes.CursorInvalid, await response.ErrorCodeAsync());
    }

    [Theory]
    [MemberData(nameof(PagedEndpoints))]
    public async Task PagedEndpoint_WithAnOversizedCursor_Returns400InvalidCursor(string route)
    {
        await using var server = await SogaTestServer.StartAsync();
        var (group, message) = await ArrangeAsync(server);

        var response = await server.Client("alice").GetAsync($"{P}{Resolve(route, group, message)}?cursor={new string('A', 600)}");

        Assert.Equal(SogaErrorCodes.CursorInvalid, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task EveryErrorResponse_IsWellFormedProblemDetailsWithoutInternals()
    {
        await using var server = await SogaTestServer.StartAsync(o => o.Messages.MaximumTextLength = 5);
        var (group, _) = await ArrangeAsync(server);
        var alice = server.Client("alice");

        var errors = new[]
        {
            await alice.GetAsync($"{P}/conversations/{Guid.NewGuid()}"),
            await alice.GetAsync($"{P}/sync?cursor=bad"),
            await alice.PostAsJsonAsync($"{P}/conversations/{group}/messages", new SendMessageRequest("c1", "far too long")),
            await alice.PostAsJsonAsync($"{P}/conversations/{group}/messages", new SendMessageRequest("", "hi")),
            await server.Client("bob").PostAsync($"{P}/conversations/{group}/archive", null),
        };

        foreach (var response in errors)
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.ReadAsync<JsonElement>();
            var code = problem.GetProperty("code").GetString()!;
            Assert.Equal($"urn:soga:error:{code}", problem.GetProperty("type").GetString());
            Assert.Equal((int)response.StatusCode, problem.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
            var raw = problem.GetRawText();
            Assert.DoesNotContain("   at ", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ErrorCodes_ReturnedOverHttp_AreAllDocumentedConstants()
    {
        var documented = typeof(SogaErrorCodes).GetFields().Select(f => (string)f.GetValue(null)!).ToHashSet();
        await using var server = await SogaTestServer.StartAsync();
        var (group, _) = await ArrangeAsync(server);
        var alice = server.Client("alice");

        var responses = new[]
        {
            await alice.GetAsync($"{P}/conversations/{Guid.NewGuid()}"),
            await alice.GetAsync($"{P}/conversations?kind=channel"),
            await alice.PutAsJsonAsync($"{P}/conversations/{group}/participants/alice/role", new ChangeRoleRequest("member")),
            await server.Client("bob").DeleteAsync($"{P}/conversations/{group}/participants/carol"),
            await alice.DeleteAsync($"{P}/conversations/{group}/participants/nobody"),
        };

        foreach (var response in responses)
        {
            Assert.Contains(await response.ErrorCodeAsync(), documented);
        }
    }

    private static TheoryData<string, string, string?> Filter(Func<(string Method, string Route, string? Body), bool> predicate)
    {
        var data = new TheoryData<string, string, string?>();
        foreach (var row in AllEndpoints)
        {
            var entry = ((string)row[0], (string)row[1], (string?)row[2]);
            if (predicate(entry))
            {
                data.Add(entry.Item1, entry.Item2, entry.Item3);
            }
        }

        return data;
    }

    private static async Task<(Guid Group, Guid Message)> ArrangeAsync(SogaTestServer server)
    {
        var alice = server.Client("alice");
        var group = await alice.CreateGroupAsync("Team", "bob", "carol");
        var message = await alice.SendAsync(group.Id, "m1", "hello");
        return (group.Id, message.Id);
    }

    private static string Resolve(string route, Guid group, Guid message) =>
        route.Replace("{id}", group.ToString(), StringComparison.Ordinal).Replace("{msg}", message.ToString(), StringComparison.Ordinal);

    private static string? ResolveBody(string? body, Guid group, Guid message) => body is null ? null : Resolve(body, group, message);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string route, string? body, string contentType)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), $"{P}{route}");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        return client.SendAsync(request);
    }

    private static async Task<string> WithoutTraceIdAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        if (json.Length == 0)
        {
            return json;
        }

        using var document = JsonDocument.Parse(json);
        return string.Join("|", document.RootElement.EnumerateObject().Where(p => p.Name != "traceId").Select(p => $"{p.Name}={p.Value}"));
    }
}
