using System.Diagnostics;
using System.Diagnostics.Metrics;
using Soga.Errors;

namespace Soga.Diagnostics;

/// <summary>
/// Provides the names of the tracing and metrics sources that Soga emits, and the internal helpers that record them.
/// </summary>
/// <remarks>
/// Subscribe to <see cref="SourceName"/> and <see cref="MeterName"/> with OpenTelemetry or <c>dotnet-counters</c>.
/// Metric tags are always bounded values, such as a command name or an error code. Message text, user IDs, tenant IDs,
/// and tokens are never used as tags.
/// </remarks>
public static class SogaDiagnostics
{
    /// <summary>The <see cref="ActivitySource"/> name.</summary>
    public const string SourceName = "Soga";

    /// <summary>The <see cref="Meter"/> name.</summary>
    public const string MeterName = "Soga";

    /// <summary>
    /// The source of the <see cref="Activity"/> spans that trace Soga workflows and outbox dispatch.
    /// </summary>
    internal static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>
    /// The meter that owns every Soga instrument.
    /// </summary>
    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Counts workflow executions, tagged by <c>command</c> and <c>outcome</c>.
    /// </summary>
    private static readonly Counter<long> Commands =
        Meter.CreateCounter<long>("soga.commands", description: "Workflow executions by command and bounded outcome.");

    /// <summary>
    /// Counts outbox events successfully published to realtime clients.
    /// </summary>
    private static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("soga.outbox.published", description: "Outbox events published to realtime.");

    /// <summary>
    /// Counts outbox publication failures, tagged by whether the failure was <c>terminal</c>.
    /// </summary>
    private static readonly Counter<long> OutboxFailed =
        Meter.CreateCounter<long>("soga.outbox.failed", description: "Outbox publication failures by terminal flag.");

    /// <summary>
    /// Records how long one outbox dispatch pass takes, in milliseconds.
    /// </summary>
    private static readonly Histogram<double> OutboxDispatchDuration =
        Meter.CreateHistogram<double>("soga.outbox.dispatch.duration", unit: "ms", description: "Time spent dispatching one outbox pass.");

    /// <summary>
    /// Tracks the number of active authenticated SignalR connections.
    /// </summary>
    private static readonly UpDownCounter<long> Connections =
        Meter.CreateUpDownCounter<long>("soga.realtime.connections", description: "Active authenticated realtime connections.");

    /// <summary>
    /// Starts a tracing span for a Soga operation.
    /// </summary>
    /// <param name="name">The span name, such as <c>soga.message.send</c>.</param>
    /// <returns>The started <see cref="Activity"/>, or <see langword="null"/> when nothing is listening.</returns>
    internal static Activity? StartActivity(string name) => ActivitySource.StartActivity(name);

    /// <summary>
    /// Records the outcome of a workflow command and returns the result unchanged.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="command">The bounded command name, such as <c>message.send</c>.</param>
    /// <param name="result">The workflow result to record.</param>
    /// <returns>The same <paramref name="result"/>, so the call can wrap a <see langword="return"/> statement.</returns>
    /// <remarks>
    /// The <c>outcome</c> tag is either <c>ok</c> or a stable error code from <see cref="SogaErrorCodes"/>, which keeps
    /// metric cardinality bounded.
    /// </remarks>
    internal static SogaResult<T> Track<T>(string command, SogaResult<T> result)
    {
        Commands.Add(
            1,
            new KeyValuePair<string, object?>("command", command),
            new KeyValuePair<string, object?>("outcome", result.Error?.Code ?? "ok"));
        return result;
    }

    /// <summary>
    /// Records that an outbox event was published.
    /// </summary>
    internal static void RecordOutboxPublished() => OutboxPublished.Add(1);

    /// <summary>
    /// Records that publishing an outbox event failed.
    /// </summary>
    /// <param name="terminal">
    /// <see langword="true"/> if the event used its last attempt and will not be retried; otherwise,
    /// <see langword="false"/>.
    /// </param>
    internal static void RecordOutboxFailed(bool terminal) =>
        OutboxFailed.Add(1, new KeyValuePair<string, object?>("terminal", terminal));

    /// <summary>
    /// Records the duration of one outbox dispatch pass.
    /// </summary>
    /// <param name="elapsed">The time the pass took.</param>
    internal static void RecordDispatchDuration(TimeSpan elapsed) =>
        OutboxDispatchDuration.Record(elapsed.TotalMilliseconds);

    /// <summary>
    /// Records that an authenticated realtime connection opened.
    /// </summary>
    internal static void ConnectionOpened() => Connections.Add(1);

    /// <summary>
    /// Records that an authenticated realtime connection closed.
    /// </summary>
    internal static void ConnectionClosed() => Connections.Add(-1);
}
