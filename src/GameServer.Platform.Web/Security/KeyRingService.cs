using GameServer.Platform.Persistence;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Web.Security;

/// <summary>
/// Owns the shared key ring lifecycle (OPS-04, research R6): loads it before the server takes traffic, creates the
/// first key if none exists, reloads every <see cref="DataProtectionSettings.RefreshMinutes"/>, and creates the next
/// key <see cref="DataProtectionSettings.ActivationLeadHours"/> before the current one expires. Requests never create
/// keys (automatic generation is disabled). Times are the system clock because Data Protection itself uses it.
/// </summary>
public sealed class KeyRingService(
    KeyRingRepository repository,
    IKeyManager keyManager,
    DataProtectionSettings settings,
    IServiceProvider services,
    ILogger<KeyRingService> logger) : IHostedLifecycleService, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _refreshLoop;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IStoreInitializer>() is { } initializer)
        {
            await initializer.EnsureInitializedAsync(cancellationToken);
        }

        await RefreshNowAsync(cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        _refreshLoop = RefreshLoopAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_refreshLoop is not null)
        {
            await _refreshLoop;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Reloads the shared ring, then makes sure a usable key and (when due) the next key exist.</summary>
    public async Task RefreshNowAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await repository.LoadAsync(cancellationToken);
            EnsureKeys(TimeProvider.System.GetUtcNow());
            await repository.FlushAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// On-demand rotation: a new key that activates after <see cref="DataProtectionSettings.RotationDelaySeconds"/>,
    /// once every instance has loaded it. Old keys stay in the ring, so existing cookies keep working.
    /// </summary>
    public async Task<(Guid KeyId, DateTimeOffset ActivatesAt)> RotateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await repository.LoadAsync(cancellationToken);
            var activation = TimeProvider.System.GetUtcNow().AddSeconds(settings.RotationDelaySeconds);
            var key = keyManager.CreateNewKey(activation, activation.AddDays(settings.KeyLifetimeDays));
            await repository.FlushAsync(cancellationToken);
            logger.LogInformation("Data Protection key {KeyId} created; activates at {ActivatesAt:O}.", key.KeyId, activation);
            return (key.KeyId, activation);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureKeys(DateTimeOffset now)
    {
        var keys = keyManager.GetAllKeys().Where(k => !k.IsRevoked).ToArray();
        var current = keys.Where(k => k.ActivationDate <= now && k.ExpirationDate > now).MaxBy(k => k.ActivationDate);
        if (current is null)
        {
            // First start of the deployment (or every key expired): instances starting together may each create one;
            // all of them end up in the shared ring and any of them can unprotect.
            var key = keyManager.CreateNewKey(now, now.AddDays(settings.KeyLifetimeDays));
            logger.LogInformation("Data Protection key ring had no active key; created {KeyId}.", key.KeyId);
            return;
        }

        var hasNext = keys.Any(k => k.ActivationDate > now);
        if (!hasNext && current.ExpirationDate - now <= TimeSpan.FromHours(settings.ActivationLeadHours))
        {
            var key = keyManager.CreateNewKey(current.ExpirationDate, current.ExpirationDate.AddDays(settings.KeyLifetimeDays));
            logger.LogInformation("Data Protection key {KeyId} created ahead of rotation; activates at {ActivatesAt:O}.", key.KeyId, current.ExpirationDate);
        }
    }

    private async Task RefreshLoopAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.RefreshMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stopping))
            {
                try
                {
                    await RefreshNowAsync(stopping);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep serving with the loaded ring; the next refresh retries (keys are created well ahead).
                    logger.LogWarning(ex, "Data Protection key ring refresh failed; retrying in {Minutes} min.", settings.RefreshMinutes);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Registered as a singleton and as a hosted service (same instance): the container disposes it twice.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stopping.CancelAsync();
        _stopping.Dispose();
        _gate.Dispose();
    }

    private int _disposed;
}
