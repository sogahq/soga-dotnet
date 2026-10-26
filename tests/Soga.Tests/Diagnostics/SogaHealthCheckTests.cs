using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Diagnostics;
using Soga.Persistence;
using Soga.Tests.Persistence;

namespace Soga.Tests.Diagnostics;

public sealed class SogaHealthCheckTests
{
    [Fact]
    public async Task Check_WhenStorageIsReachableAndOutboxIsEmpty_IsHealthy()
    {
        using var soga = TestSoga.Create();

        var result = await CheckAsync(soga);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(0L, result.Data["outboxPending"]);
        Assert.Equal(0L, result.Data["outboxFailed"]);
    }

    [Fact]
    public async Task Check_WhenEventsArePendingButRecent_IsHealthyAndReportsTheBacklog()
    {
        using var soga = TestSoga.Create();
        await CreateDirectAsync(soga);

        var result = await CheckAsync(soga);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(1L, result.Data["outboxPending"]);
    }

    [Fact]
    public async Task Check_WhenTheOldestPendingEventIsOlderThanTheLimit_IsDegraded()
    {
        using var soga = TestSoga.Create(options => options.Outbox.UnhealthyBacklogAge = TimeSpan.FromMinutes(5));
        await CreateDirectAsync(soga);
        soga.Clock.Advance(TimeSpan.FromMinutes(6));

        var result = await CheckAsync(soga);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(360L, result.Data["outboxOldestPendingSeconds"]);
    }

    [Fact]
    public async Task Check_WhenTheBacklogIsExactlyAtTheLimit_IsStillHealthy()
    {
        using var soga = TestSoga.Create(options => options.Outbox.UnhealthyBacklogAge = TimeSpan.FromMinutes(5));
        await CreateDirectAsync(soga);
        soga.Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(soga)).Status);
    }

    [Fact]
    public async Task Check_WhenStorageReportsItIsUnavailable_IsUnhealthy()
    {
        var store = new FaultyStore { HealthResult = false };
        using var soga = TestSoga.Create(services: s => s.Replace(ServiceDescriptor.Singleton<ISogaStore>(store)));

        var result = await CheckAsync(soga);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Exception);
    }

    [Fact]
    public async Task Check_WhenStorageThrows_IsUnhealthyAndKeepsTheException()
    {
        var failure = new InvalidOperationException("connection refused");
        var store = new FaultyStore { HealthFailure = failure };
        using var soga = TestSoga.Create(services: s => s.Replace(ServiceDescriptor.Singleton<ISogaStore>(store)));

        var result = await CheckAsync(soga);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Same(failure, result.Exception);
    }

    [Fact]
    public async Task Check_WhenCanceled_LetsTheCancellationPropagate()
    {
        var store = new FaultyStore { HealthFailure = new OperationCanceledException() };
        using var soga = TestSoga.Create(services: s => s.Replace(ServiceDescriptor.Singleton<ISogaStore>(store)));

        await Assert.ThrowsAsync<OperationCanceledException>(() => CheckAsync(soga));
    }

    [Fact]
    public async Task HealthChecks_RegisterSogaUnderItsOwnNameAndTag()
    {
        using var soga = TestSoga.Create();

        var report = await soga.Get<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains("soga"));

        Assert.Equal(HealthStatus.Healthy, report.Entries["soga"].Status);
    }

    private static Task<HealthCheckResult> CheckAsync(TestSoga soga) =>
        ActivatorUtilities.CreateInstance<SogaHealthCheck>(soga.Get<IServiceProvider>())
            .CheckHealthAsync(new HealthCheckContext(), default);

    private static Task<Soga.Errors.SogaResult<Soga.Errors.CreatedResult<ConversationResponse>>> CreateDirectAsync(TestSoga soga) =>
        soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest("bob"), default);
}
