using Microsoft.Extensions.DependencyInjection;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Extensions;
using Soga.Groups;
using Soga.Identity;
using Soga.Messages;

namespace Soga.Tests;

/// <summary>A clock tests can move, so edit windows and typing expiry are deterministic.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Builds a real Soga service provider with the in-memory store, without a web host.</summary>
internal sealed class TestSoga : IDisposable
{
    public const string Tenant = "tenant-1";

    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    private TestSoga(Action<SogaOptions>? configure, Action<IServiceCollection>? services, TestClock clock)
    {
        Clock = clock;
        var collection = new ServiceCollection().AddLogging();
        collection.AddSingleton<TimeProvider>(clock);
        collection
            .AddSoga(options =>
            {
                options.DefaultTenantId = Tenant;
                configure?.Invoke(options);
            });
        services?.Invoke(collection);
        _provider = collection.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public static SogaCaller Alice => new(Tenant, "alice");

    public static SogaCaller Bob => new(Tenant, "bob");

    public static SogaCaller Carol => new(Tenant, "carol");

    public static SogaCaller Dave => new(Tenant, "dave");

    public TestClock Clock { get; }

    public static TestSoga Create(Action<SogaOptions>? configure = null, Action<IServiceCollection>? services = null) =>
        new(configure, services, new TestClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero)));

    public T Get<T>()
        where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>Creates a group owned by <paramref name="owner"/> with the given members and returns it.</summary>
    public async Task<ConversationResponse> CreateGroupAsync(SogaCaller owner, params string[] members)
    {
        var result = await Get<GroupWorkflow>().CreateAsync(owner, new CreateGroupRequest("Team", members), default);
        return result.Value;
    }

    public async Task<MessageResponse> SendAsync(SogaCaller caller, Guid conversationId, string clientMessageId, string text, Guid? replyTo = null)
    {
        var result = await Get<MessageWorkflow>().SendAsync(caller, conversationId, new SendMessageRequest(clientMessageId, text, replyTo), default);
        return result.Value.Value;
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
