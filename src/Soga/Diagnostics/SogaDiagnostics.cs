using System.Diagnostics;
using System.Diagnostics.Metrics;
using Soga.Errors;

namespace Soga.Diagnostics;

/// <summary>Names of the telemetry sources Soga emits. Subscribe to them with OpenTelemetry or dotnet-counters.</summary>
public static class SogaDiagnostics
{
    /// <summary>The <see cref="ActivitySource"/> name.</summary>
    public const string SourceName = "Soga";

    /// <summary>The <see cref="Meter"/> name.</summary>
    public const string MeterName = "Soga";

    internal static readonly ActivitySource ActivitySource = new(SourceName);

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Commands =
        Meter.CreateCounter<long>("soga.commands", description: "Workflow executions by command and bounded outcome.");

    private static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("soga.outbox.published", description: "Outbox events published to realtime.");

    private static readonly Counter<long> OutboxFailed =
        Meter.CreateCounter<long>("soga.outbox.failed", description: "Outbox publication failures by terminal flag.");

    private static readonly Histogram<double> OutboxDispatchDuration =
        Meter.CreateHistogram<double>("soga.outbox.dispatch.duration", unit: "ms", description: "Time spent dispatching one outbox pass.");

    private static readonly UpDownCounter<long> Connections =
        Meter.CreateUpDownCounter<long>("soga.realtime.connections", description: "Active authenticated realtime connections.");

    internal static Activity? StartActivity(string name) => ActivitySource.StartActivity(name);

    /// <summary>Records a command outcome. The outcome is a stable error code or <c>ok</c>, so cardinality stays bounded.</summary>
    internal static SogaResult<T> Track<T>(string command, SogaResult<T> result)
    {
        Commands.Add(
            1,
            new KeyValuePair<string, object?>("command", command),
            new KeyValuePair<string, object?>("outcome", result.Error?.Code ?? "ok"));
        return result;
    }

    internal static void RecordOutboxPublished() => OutboxPublished.Add(1);

    internal static void RecordOutboxFailed(bool terminal) =>
        OutboxFailed.Add(1, new KeyValuePair<string, object?>("terminal", terminal));

    internal static void RecordDispatchDuration(TimeSpan elapsed) =>
        OutboxDispatchDuration.Record(elapsed.TotalMilliseconds);

    internal static void ConnectionOpened() => Connections.Add(1);

    internal static void ConnectionClosed() => Connections.Add(-1);
}
