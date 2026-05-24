using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace AccountService.Diagnostics;

/// <summary>
/// Enriches every Serilog log event with the active OpenTelemetry <c>traceId</c> and <c>spanId</c>
/// so that structured logs can be correlated with distributed traces without a separate join.
/// </summary>
internal sealed class ActivityEnricher : ILogEventEnricher
{
    /// <inheritdoc/>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("traceId", activity.TraceId.ToString()));

        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("spanId", activity.SpanId.ToString()));
    }
}
