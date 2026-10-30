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
    private readonly Counter<long> _deadLetters;
    private readonly Histogram<double> _outboxPendingAge;
    private readonly Counter<long> _commandResults;
    private readonly Counter<long> _httpErrors;
    private readonly Counter<long> _jobRuns;
    private readonly Histogram<double> _jobRunDuration;
    private readonly Counter<long> _jobLeaseAcquisitions;
    private readonly Counter<long> _jobLeaseLosses;
    private readonly Counter<long> _accountPurges;
    private readonly Counter<long> _accountPurgeStepFailures;
    private readonly Counter<long> _accountPurgeDocuments;
    private string _configVersion = string.Empty;
    private OverdueSample? _purgeOverdue;

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
        _deadLetters = _meter.CreateCounter<long>("outbox_dead_letter_total");
        _outboxPendingAge = _meter.CreateHistogram<double>("outbox_pending_age_seconds", "s");
        _commandResults = _meter.CreateCounter<long>("command_results_total", description: "Command outcomes by operation and result code");
        _httpErrors = _meter.CreateCounter<long>("http_errors_total", description: "Error responses by catalog code");
        _jobRuns = _meter.CreateCounter<long>("job_runs_total", description: "Lease job runs by job and result");
        _jobRunDuration = _meter.CreateHistogram<double>("job_run_duration_seconds", "s", "Duration of lease job runs");
        _jobLeaseAcquisitions = _meter.CreateCounter<long>("job_lease_acquisitions_total", description: "Job leases acquired by this instance");
        _jobLeaseLosses = _meter.CreateCounter<long>("job_lease_losses_total", description: "Job leases lost while a run was in progress");
        _accountPurges = _meter.CreateCounter<long>("account_purges_total", description: "Per-player purge attempts by result");
        _accountPurgeStepFailures = _meter.CreateCounter<long>("account_purge_step_failures_total", description: "Failed purge steps by step id");
        _accountPurgeDocuments = _meter.CreateCounter<long>("account_purge_documents_total", description: "Documents anonymized or removed by account purges");
        _meter.CreateObservableGauge(
            "account_purge_overdue_seconds",
            ObserveAccountPurgeOverdue,
            "s",
            "Age past purgeAfter of the oldest due player seen by the latest purge run on this instance; reported only until the " +
            "next run is due (aggregate with max; alert > 86400, SC-006; absent = the job stopped running)");
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

    public void OutboxDeadLettered() => _deadLetters.Add(1);

    public void OutboxPendingAge(TimeSpan age) => _outboxPendingAge.Record(age.TotalSeconds);

    public void SetConfigVersion(string version) => _configVersion = version;

    /// <summary>One command outcome (OBS-01 errors/rejections by code). Never tagged with player or request ids (OBS-02).</summary>
    public void CommandCompleted(string operation, string code, bool replayed) =>
        _commandResults.Add(
            1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("code", code),
            new KeyValuePair<string, object?>("replayed", replayed));

    public void HttpError(string code) => _httpErrors.Add(1, new KeyValuePair<string, object?>("code", code));

    /// <summary>One lease job run (JOB-01, research R12). <paramref name="result"/> is a <c>JobRunResult</c> name.</summary>
    public void JobRun(string job, string result, TimeSpan duration)
    {
        _jobRuns.Add(1, new KeyValuePair<string, object?>("job", job), new KeyValuePair<string, object?>("result", result));
        _jobRunDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("job", job));
    }

    public void JobLeaseAcquired(string job) => _jobLeaseAcquisitions.Add(1, new KeyValuePair<string, object?>("job", job));

    public void JobLeaseLost(string job) => _jobLeaseLosses.Add(1, new KeyValuePair<string, object?>("job", job));

    /// <summary>One per-player purge attempt (research R12): <c>completed</c>, <c>incomplete</c>, <c>failed</c> or <c>fenced_out</c>.</summary>
    public void AccountPurge(string result) => _accountPurges.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <summary>A purge step failed; <paramref name="step"/> is a step id of research R7 (bounded by the registered collections and steps).</summary>
    public void AccountPurgeStepFailed(string step) => _accountPurgeStepFailures.Add(1, new KeyValuePair<string, object?>("step", step));

    /// <summary>Committed purge writes; <paramref name="action"/> is <c>anonymize</c> or <c>remove</c>.</summary>
    public void AccountPurgeDocuments(string action, int count)
    {
        if (count > 0)
        {
            _accountPurgeDocuments.Add(count, new KeyValuePair<string, object?>("action", action));
        }
    }

    /// <summary>
    /// Sets <c>account_purge_overdue_seconds</c>: how long the oldest due player waits past its purge date (0 when none). Called
    /// by the lease holder's run; the value is reported for <paramref name="validFor"/> (real time) only, so an instance that
    /// stopped holding the lease drops out instead of reporting a stale value forever (review minor 5).
    /// </summary>
    public void SetAccountPurgeOverdue(TimeSpan overdue, TimeSpan validFor) =>
        Volatile.Write(ref _purgeOverdue, new OverdueSample(Math.Max(0, overdue.TotalSeconds), Environment.TickCount64 + (long)validFor.TotalMilliseconds));

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<double>> ObserveAccountPurgeOverdue()
    {
        var sample = Volatile.Read(ref _purgeOverdue);
        return sample is not null && Environment.TickCount64 < sample.ExpiresAtTicks ? [new Measurement<double>(sample.Seconds)] : [];
    }

    private sealed record OverdueSample(double Seconds, long ExpiresAtTicks);
}
