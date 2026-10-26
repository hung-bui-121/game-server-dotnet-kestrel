using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GameServer.Platform.Diagnostics;

/// <summary>
/// Platform instruments (OBS-01 subset used by slice 001). Names follow the requirements; exporters are
/// configured in slice 005. <c>playerId</c>/<c>requestId</c> are never metric tags (OBS-02).
/// </summary>
public sealed class PlatformMetrics : IDisposable
{
    public const string MeterName = "GameServer.Platform";

    public static readonly ActivitySource ActivitySource = new(MeterName);

    private readonly Meter _meter;
    private readonly Histogram<int> _aggregateSize;
    private readonly Counter<long> _aggregateWarn;
    private readonly Counter<long> _aggregateUpgrades;
    private readonly Counter<long> _txnAttempts;
    private readonly Counter<long> _txnRetries;
    private readonly Counter<long> _txnAmbiguous;
    private readonly Counter<long> _txnExpired;
    private readonly Histogram<double> _txnDuration;
    private readonly Histogram<int> _receiptSize;
    private readonly Counter<long> _relayFailures;
    private readonly Histogram<double> _outboxPendingAge;
    private string _configVersion = string.Empty;

    public PlatformMetrics()
    {
        _meter = new Meter(MeterName);
        _aggregateSize = _meter.CreateHistogram<int>("aggregate_size_bytes", "By", "Serialized size of written player aggregates");
        _aggregateWarn = _meter.CreateCounter<long>("aggregate_size_warnings_total", description: "Aggregates written above the warning size");
        _aggregateUpgrades = _meter.CreateCounter<long>("aggregate_upgrades_total", description: "Aggregates lazily upgraded on read");
        _txnAttempts = _meter.CreateCounter<long>("txn_attempts_total");
        _txnRetries = _meter.CreateCounter<long>("txn_retries_total");
        _txnAmbiguous = _meter.CreateCounter<long>("txn_ambiguous_total");
        _txnExpired = _meter.CreateCounter<long>("txn_expired_total");
        _txnDuration = _meter.CreateHistogram<double>("txn_duration_seconds", "s");
        _receiptSize = _meter.CreateHistogram<int>("receipt_size_bytes", "By");
        _relayFailures = _meter.CreateCounter<long>("outbox_relay_inline_failures_total");
        _outboxPendingAge = _meter.CreateHistogram<double>("outbox_pending_age_seconds", "s");
        _meter.CreateObservableGauge(
            "config_version_info",
            () => new Measurement<int>(1, new KeyValuePair<string, object?>("version", _configVersion)));
    }

    public void AggregateWritten(string aggregateId, int sizeBytes, bool overWarning)
    {
        _aggregateSize.Record(sizeBytes, new KeyValuePair<string, object?>("aggregateId", aggregateId));
        if (overWarning)
        {
            _aggregateWarn.Add(1, new KeyValuePair<string, object?>("aggregateId", aggregateId));
        }
    }

    public void AggregateUpgraded(string aggregateId) =>
        _aggregateUpgrades.Add(1, new KeyValuePair<string, object?>("aggregateId", aggregateId));

    public void TransactionAttempt(string operation, bool retry)
    {
        _txnAttempts.Add(1, new KeyValuePair<string, object?>("operation", operation));
        if (retry)
        {
            _txnRetries.Add(1, new KeyValuePair<string, object?>("operation", operation));
        }
    }

    public void TransactionAmbiguous(string operation) => _txnAmbiguous.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public void TransactionExpired(string operation) => _txnExpired.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public void TransactionDuration(string operation, TimeSpan duration) =>
        _txnDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("operation", operation));

    public void ReceiptWritten(int sizeBytes) => _receiptSize.Record(sizeBytes);

    public void OutboxRelayFailed(string kind) => _relayFailures.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void OutboxPendingAge(TimeSpan age) => _outboxPendingAge.Record(age.TotalSeconds);

    public void SetConfigVersion(string version) => _configVersion = version;

    public void Dispose() => _meter.Dispose();
}
