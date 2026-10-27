using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Web.Security;

/// <summary>
/// One Data Protection key ring per deployment (game + environment), shared by every instance through the store
/// (OPS-04, ACC-49). Protects GM session cookies, antiforgery tokens and OIDC correlation cookies.
/// </summary>
public static class SharedDataProtection
{
    public static void Add(IServiceCollection services, IConfiguration configuration, GameServerOptions gameServer)
    {
        var settings = configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new DataProtectionSettings();
        services.AddSingleton(settings);
        services.AddSingleton<KeyRingRepository>();
        services.AddSingleton<KeyRingService>();
        services.AddHostedService(sp => sp.GetRequiredService<KeyRingService>());

        services.AddDataProtection()
            .SetApplicationName($"gameserver:{gameServer.GameId}:{gameServer.EnvironmentId}")
            .SetDefaultKeyLifetime(TimeSpan.FromDays(Math.Clamp(settings.KeyLifetimeDays, 7, 365)))
            .DisableAutomaticKeyGeneration();

        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp => new ConfigureOptions<KeyManagementOptions>(options =>
        {
            options.XmlRepository = sp.GetRequiredService<KeyRingRepository>();
            if (settings.MasterKeyBytes() is { Length: 32 } masterKey)
            {
                options.XmlEncryptor = new MasterKeyXmlEncryptor(settings.MasterKeyId, masterKey);
            }
        }));

        // The default manager only invalidates its cache for keys it created; this one also reacts to keys loaded
        // from the shared ring (written by other instances).
        services.Replace(ServiceDescriptor.Singleton<IKeyManager>(sp =>
            new RefreshingKeyManager(ActivatorUtilities.CreateInstance<XmlKeyManager>(sp), sp.GetRequiredService<KeyRingRepository>())));
    }
}
