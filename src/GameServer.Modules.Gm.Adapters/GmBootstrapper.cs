using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>
/// Before traffic: seeds <c>Gm:Bootstrap:Staff</c> (Development/Test only, FR-004 of slice 004) and, when the
/// deployment has no staff manager, creates <c>Gm:Oidc:BootstrapManagers</c> as Administrators (research R11).
/// Every write goes through <see cref="StaffAdminService"/> (one transaction with its audit entry); both steps are
/// idempotent, so several instances starting together are safe.
/// </summary>
public sealed class GmBootstrapper(
    StaffAdminService staff,
    GameServerOptions gameServer,
    IOptions<GmOptions> options,
    IServiceProvider services,
    ILogger<GmBootstrapper> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IStoreInitializer>() is { } initializer)
        {
            await initializer.EnsureInitializedAsync(cancellationToken);
        }

        if (gameServer.AllowsDevelopmentTools)
        {
            foreach (var seed in options.Value.Bootstrap.Staff)
            {
                var result = await staff.SeedAsync(
                    new NewStaff(
                        seed.StaffId,
                        seed.DisplayName ?? seed.StaffId,
                        Email: null,
                        Oidc: null,
                        seed.Roles,
                        seed.Permissions,
                        DevPasswordHasher.Hash(seed.Password),
                        seed.Disabled) { Scope = (seed.GameId, seed.EnvironmentId) },
                    StaffAdminService.Hash("seed-password:" + seed.Password),
                    cancellationToken);
                logger.LogInformation("Seeded GM staff {StaffId}{Replayed}.", seed.StaffId, result.Replayed ? " (unchanged)" : string.Empty);
            }
        }

        var managers = options.Value.Oidc.BootstrapManagers;
        if (managers.Count == 0 || await staff.CountManagersAsync(cancellationToken) > 0)
        {
            return;
        }

        foreach (var manager in managers)
        {
            var oidc = manager.Issuer is not null && manager.Subject is not null
                ? new StaffOidcIdentity { Issuer = manager.Issuer, Subject = manager.Subject }
                : null;
            try
            {
                await staff.CreateAsync(
                    new StaffChange("system:bootstrap", null, StaffAdminService.Hash("bootstrap-manager:" + manager.StaffId), "Gm:Oidc:BootstrapManagers", "bootstrap"),
                    new NewStaff(manager.StaffId!, manager.StaffId!, manager.Email, oidc, [GmRoles.Administrator], []),
                    cancellationToken);
                logger.LogInformation("Created bootstrap GM staff manager {StaffId}.", manager.StaffId);
            }
            catch (GameServerException ex) when (ex.Code == GmErrorCodes.StaffExists)
            {
                // Created earlier or by another instance starting at the same time; checked below.
            }
        }

        // A configured manager that exists but is not a usable manager (wrong staffId reused, disabled, no
        // staff.manage) would leave the deployment without anyone able to manage staff (review M2).
        foreach (var manager in managers)
        {
            var record = await staff.GetAsync(manager.StaffId!, cancellationToken);
            var usable = record is not null && !record.Disabled
                && record.EffectivePermissions(gameServer.GameId, gameServer.EnvironmentId).Contains(GmPermissions.StaffManage);
            if (!usable)
            {
                var message = $"Gm:Oidc:BootstrapManagers staffId '{manager.StaffId}' exists but is not an enabled staff manager; the deployment has no staff manager.";
                if (!gameServer.AllowsDevelopmentTools)
                {
                    throw new InvalidOperationException(message);
                }

                logger.LogError("{Message}", message);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Every permission declared by the registered modules (SEC-07 catalog).</summary>
    public static IReadOnlySet<string> PermissionCatalog(ModuleRegistry registry) =>
        registry.Modules.SelectMany(m => m.Descriptor.Permissions).ToHashSet(StringComparer.Ordinal);
}
