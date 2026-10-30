using System.Threading.Channels;
using GameServer.Analytics;
using GameServer.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Analytics;

/// <summary>
/// One bounded queue and one dispatcher for every analytics record (R5, FR-001, FR-004, FR-028). Producers never wait:
/// best-effort events (<see cref="TryEmit"/>) are dropped and counted when the queue is full; outbox deliveries
/// (<see cref="DeliverAsync"/>) complete only after every sink wrote the record, within <c>DeliveryTimeoutMs</c>. The
/// dispatcher formats each record once and hands the same bytes to every sink in registration order. Registered before
/// the outbox sweeper, so it stops after it and after the web server; queued records are drained until the stop token.
/// </summary>
public sealed class AnalyticsPipeline : BackgroundService, IAnalyticsEmitter
{
    private const int MaxBatch = 512;

    private readonly AnalyticsOptions _options;
    private readonly IReadOnlyList<IAnalyticsSink> _sinks;
    private readonly AnalyticsRecordFormatter _formatter;
    private readonly AnalyticsTimeZone _zone;
    private readonly AnalyticsMetrics _metrics;
    private readonly IClock _clock;
    private readonly ILogger<AnalyticsPipeline> _logger;
    private readonly Channel<Pending> _queue;
    private readonly CancellationTokenSource _abort = new();
    private readonly Lock _statusLock = new();
    private volatile bool _stopping;
    private long _sinkErrors;
    private long _dropped;
    private DateTimeOffset? _lastErrorAtUtc;
    private string? _lastError;

    public AnalyticsPipeline(
        IOptions<AnalyticsOptions> options,
        AnalyticsActivation activation,
        IEnumerable<IAnalyticsSink> sinks,
        AnalyticsRecordFormatter formatter,
        AnalyticsTimeZone zone,
        AnalyticsMetrics metrics,
        IClock clock,
        ILogger<AnalyticsPipeline> logger)
    {
        _options = options.Value;
        IsActive = activation.IsActive(_options);

        // Inactive: no sink is ever called (a disabled file sink does no I/O at all, US5-2).
        _sinks = IsActive ? [.. sinks] : [];
        _formatter = formatter;
        _zone = zone;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        metrics.ObserveQueue(() => _queue.Reader.Count);
    }

    public bool IsActive { get; }

    public AnalyticsStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return new AnalyticsStatus(Interlocked.Read(ref _sinkErrors), Interlocked.Read(ref _dropped), _queue.Reader.Count, _lastErrorAtUtc, _lastError);
            }
        }
    }

    public bool TryEmit(AnalyticsEventType type, string userId, DateTimeOffset occurredAtUtc, object fields)
    {
        if (!IsActive)
        {
            return false;
        }

        if (type is null || fields is null || string.IsNullOrEmpty(userId) || !AnalyticsEventType.IsValidName(type.Name)
            || type.SchemaVersion < 1 || !AnalyticsEventType.IsValidTimeField(type.TimeField))
        {
            _logger.LogError("Invalid analytics emission ignored (event {Event}).", type?.Name);
            return false;
        }

        var pending = new Pending(new AnalyticsEnvelope(type, AnalyticsEventIds.NewRandom(), userId, occurredAtUtc, null, fields), null);
        if (_stopping || !_queue.Writer.TryWrite(pending))
        {
            Drop(type.Name, _stopping ? AnalyticsDropReason.Shutdown : AnalyticsDropReason.QueueFull);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Guaranteed path (outbox consumer): completes once every sink wrote and flushed the record. Throws
    /// <see cref="AnalyticsDeliveryException"/> when the queue is full, no ack arrives within <c>DeliveryTimeoutMs</c>, a
    /// sink failed or the pipeline is stopping; each failure is counted. A timed-out delivery not yet taken by the
    /// dispatcher is skipped; one already taken may still be written (duplicate with identical identifying fields).
    /// Inactive: returns at once (the consumer acknowledges without delivering).
    /// </summary>
    public async Task DeliverAsync(AnalyticsEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsActive)
        {
            return;
        }

        var pending = new Pending(envelope, new TaskCompletionSource<AnalyticsDeliveryException?>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_stopping)
        {
            throw Failed(AnalyticsDeliveryFailure.Stopped);
        }

        if (!_queue.Writer.TryWrite(pending))
        {
            throw Failed(_stopping ? AnalyticsDeliveryFailure.Stopped : AnalyticsDeliveryFailure.QueueFull);
        }

        AnalyticsDeliveryException? failure;
        try
        {
            // The ack carries the failure as a value: a delivery abandoned on timeout never leaves an unobserved exception.
            failure = await pending.Ack!.Task.WaitAsync(TimeSpan.FromMilliseconds(_options.DeliveryTimeoutMs), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            pending.Abandon();
            throw Failed(AnalyticsDeliveryFailure.Timeout);
        }
        catch (OperationCanceledException)
        {
            pending.Abandon();
            _metrics.DeliveryFailed(AnalyticsDeliveryFailure.Stopped);
            throw;
        }

        if (failure is not null)
        {
            _metrics.DeliveryFailed(failure.Reason);
            throw failure;
        }
    }

    /// <summary>Waits until everything queued before the call went through the sinks (tests, diagnostics).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!IsActive || _stopping)
        {
            return;
        }

        var marker = new Pending(null, new TaskCompletionSource<AnalyticsDeliveryException?>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await _queue.Writer.WriteAsync(marker, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return;
        }

        await marker.Ack!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled && _sinks.Count == 0)
        {
            // Reached in Development/Test only: Staging/Production fail startup on this (AnalyticsOptionsValidator).
            _logger.LogWarning("Analytics enabled but no sink registered (AddAnalyticsFiles); analytics is inactive and nothing is written.");
        }
        else if (IsActive && _zone.IsDefaulted)
        {
            _logger.LogWarning("{Key} is not set; analytics records use UTC (Development/Test default).", AnalyticsOptions.TimeZoneKey);
        }

        return base.StartAsync(cancellationToken);
    }

    /// <summary>Stops accepting, drains the queue until <paramref name="cancellationToken"/> fires, then counts the rest.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _queue.Writer.TryComplete();
        if (ExecuteTask is { } dispatcher)
        {
            try
            {
                await dispatcher.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Drain budget used up: abort the sinks and give up on the rest.
                await _abort.CancelAsync().ConfigureAwait(false);
                await dispatcher.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        while (_queue.Reader.TryRead(out var left))
        {
            Discard(left);
        }

        await base.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        base.Dispose();
        _abort.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsActive)
        {
            return;
        }

        // Not the stopping token: the dispatcher drains until the writer completes (StopAsync) or the drain is aborted.
        var reader = _queue.Reader;
        var batch = new List<Pending>(MaxBatch);
        try
        {
            while (await reader.WaitToReadAsync(_abort.Token).ConfigureAwait(false))
            {
                while (batch.Count < MaxBatch && reader.TryRead(out var pending))
                {
                    if (pending.TryTake())
                    {
                        batch.Add(pending);
                    }
                }

                if (batch.Count > 0)
                {
                    try
                    {
                        await DispatchAsync(batch).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !_abort.IsCancellationRequested)
                    {
                        // A bug outside the sink calls (metrics, logging): fail this batch, keep the dispatcher (and the
                        // host) running. Settled acks ignore the failure.
                        FailBatch(batch, ex);
                    }

                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            foreach (var pending in batch)
            {
                Discard(pending);
            }
        }
    }

    private async Task DispatchAsync(List<Pending> batch)
    {
        var records = new List<AnalyticsRecord>(batch.Count);
        var written = new List<Pending>(batch.Count);
        foreach (var pending in batch)
        {
            if (pending.Envelope is not { } envelope)
            {
                continue;
            }

            try
            {
                var record = _formatter.Format(envelope);
                records.Add(pending.Ack is null ? record : record with { Guaranteed = true });
                written.Add(pending);
            }
            catch (Exception ex)
            {
                // A module bug (fields not serializable): never retried into a sink.
                _logger.LogError(ex, "Analytics record of event {Event} could not be formatted.", envelope.Type.Name);
                Fail(pending, AnalyticsDeliveryFailure.Malformed, ex);
            }
        }

        Exception? failure = null;
        if (records.Count > 0)
        {
            foreach (var sink in _sinks)
            {
                try
                {
                    await sink.WriteAsync(records, _abort.Token).ConfigureAwait(false);
                    CountWritten(records, sink.Name);
                }
                catch (OperationCanceledException) when (_abort.IsCancellationRequested)
                {
                    // Drain budget used up during shutdown: not a sink error.
                    foreach (var pending in written)
                    {
                        Discard(pending);
                    }

                    written.Clear();
                    break;
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                    RecordSinkError(sink.Name, ex);
                }
            }
        }

        foreach (var pending in written)
        {
            if (failure is null)
            {
                pending.Ack?.TrySetResult(null);
            }
            else
            {
                Fail(pending, AnalyticsDeliveryFailure.SinkError, failure);
            }
        }

        // Flush markers complete after every record queued before them.
        foreach (var pending in batch)
        {
            if (pending.Envelope is null)
            {
                pending.Ack!.TrySetResult(null);
            }
        }
    }

    /// <summary>
    /// The dispatch threw outside the per-sink handling: outbox deliveries not yet acknowledged fail (retried by the sweeper),
    /// best-effort records are counted as dropped (also those a sink may have written before the throw), flush markers complete.
    /// </summary>
    private void FailBatch(List<Pending> batch, Exception ex)
    {
        var dropped = new List<string>();
        foreach (var pending in batch)
        {
            if (pending.Envelope is null)
            {
                pending.Ack!.TrySetResult(null);
            }
            else if (pending.Ack is { } ack)
            {
                ack.TrySetResult(new AnalyticsDeliveryException(AnalyticsDeliveryFailure.SinkError, ex));
            }
            else
            {
                dropped.Add(pending.Envelope.Type.Name);
            }
        }

        Interlocked.Add(ref _dropped, dropped.Count);
        try
        {
            foreach (var eventName in dropped)
            {
                _metrics.Dropped(eventName, AnalyticsDropReason.SinkError);
            }

            _logger.LogError(ex, "Analytics dispatch of a batch of {Count} records failed unexpectedly; the batch counts as failed.", batch.Count);
        }
        catch (Exception)
        {
            // Metrics or logging is the failing component: the batch is settled and counted in Status, nothing else can report it.
        }
    }

    private void CountWritten(List<AnalyticsRecord> records, string sink)
    {
        var start = 0;
        for (var i = 1; i <= records.Count; i++)
        {
            if (i == records.Count || records[i].Event != records[start].Event)
            {
                _metrics.Written(records[start].Event, sink, i - start);
                start = i;
            }
        }
    }

    private void RecordSinkError(string sink, Exception ex)
    {
        Interlocked.Increment(ref _sinkErrors);
        _metrics.SinkError(sink);
        var message = $"{ex.GetType().Name}: {ex.Message}";
        lock (_statusLock)
        {
            _lastError = message.Length > 500 ? message[..500] : message;
            _lastErrorAtUtc = _clock.UtcNow;
        }

        _logger.LogError(ex, "Analytics sink {Sink} failed a batch.", sink);
    }

    /// <summary>A record that did not reach every sink: outbox deliveries fail (retried), best-effort records are dropped.</summary>
    private void Fail(Pending pending, AnalyticsDeliveryFailure reason, Exception? inner)
    {
        if (pending.Ack is { } ack)
        {
            ack.TrySetResult(new AnalyticsDeliveryException(reason, inner));
        }
        else
        {
            Drop(pending.Envelope!.Type.Name, AnalyticsDropReason.SinkError);
        }
    }

    /// <summary>Left in the queue at shutdown.</summary>
    private void Discard(Pending pending)
    {
        if (pending.Envelope is null)
        {
            pending.Ack!.TrySetResult(null);
        }
        else if (pending.Ack is { } ack)
        {
            ack.TrySetResult(new AnalyticsDeliveryException(AnalyticsDeliveryFailure.Stopped));
        }
        else
        {
            Drop(pending.Envelope.Type.Name, AnalyticsDropReason.Shutdown);
        }
    }

    private void Drop(string eventName, AnalyticsDropReason reason)
    {
        Interlocked.Increment(ref _dropped);
        _metrics.Dropped(eventName, reason);
    }

    private AnalyticsDeliveryException Failed(AnalyticsDeliveryFailure reason)
    {
        _metrics.DeliveryFailed(reason);
        return new AnalyticsDeliveryException(reason);
    }

    /// <summary>A queued item: a record (with an ack for outbox deliveries) or a flush marker (no envelope).</summary>
    private sealed class Pending(AnalyticsEnvelope? envelope, TaskCompletionSource<AnalyticsDeliveryException?>? ack)
    {
        private const int Queued = 0;
        private const int Taken = 1;
        private const int Abandoned = 2;

        private int _state;

        public AnalyticsEnvelope? Envelope { get; } = envelope;

        public TaskCompletionSource<AnalyticsDeliveryException?>? Ack { get; } = ack;

        /// <summary>Claimed by the dispatcher; false when the delivery already timed out (skipped, not written).</summary>
        public bool TryTake() => Interlocked.CompareExchange(ref _state, Taken, Queued) == Queued;

        public void Abandon() => Interlocked.CompareExchange(ref _state, Abandoned, Queued);
    }
}
