using GameServer.Platform;
using GameServer.Platform.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>Seeds <c>Gm:Bootstrap:Staff</c> into <c>gm_staff</c> at startup (Development/Test only, FR-004).</summary>
public sealed class GmBootstrapper(
    StaffDirectory directory,
    ModuleRegistry registry,
    GameServerOptions gameServer,
    IOptions<GmOptions> options,
    ILogger<GmBootstrapper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!gameServer.AllowsDevelopmentTools)
        {
            return;
        }

        var catalog = PermissionCatalog(registry);
        foreach (var seed in options.Value.Bootstrap.Staff)
        {
            var unknown = seed.Permissions.Where(p => !catalog.Contains(p)).ToArray();
            if (unknown.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Gm:Bootstrap:Staff '{seed.StaffId}' grants permissions not in the catalog: {string.Join(", ", unknown)}.");
            }

            var permissions = seed.Roles.SelectMany(r => GmRoles.Expand(r, catalog)).Concat(seed.Permissions)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            await directory.SaveAsync(
                new StaffRecord
                {
                    StaffId = seed.StaffId,
                    DisplayName = seed.DisplayName ?? seed.StaffId,
                    Disabled = seed.Disabled,
                    PasswordHash = DevPasswordHasher.Hash(seed.Password),
                    Grants =
                    [
                        new StaffGrant
                        {
                            GameId = seed.GameId ?? gameServer.GameId,
                            EnvironmentId = seed.EnvironmentId ?? gameServer.EnvironmentId,
                            Permissions = permissions,
                        },
                    ],
                },
                cancellationToken);
            logger.LogInformation("Seeded GM staff {StaffId} with {PermissionCount} permissions.", seed.StaffId, permissions.Length);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Every permission declared by the registered modules (SEC-07 catalog).</summary>
    public static IReadOnlySet<string> PermissionCatalog(ModuleRegistry registry) =>
        registry.Modules.SelectMany(m => m.Descriptor.Permissions).ToHashSet(StringComparer.Ordinal);
}
