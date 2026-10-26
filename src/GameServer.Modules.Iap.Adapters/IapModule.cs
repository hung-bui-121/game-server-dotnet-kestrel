using GameServer.Config;
using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Wire = GameServer.Modules.Iap.Protocol.V1;

namespace GameServer.Modules.Iap;

/// <summary>
/// Framework IAP module (IAP-01, IAP-02, SMP-04): the endpoint verifies the receipt with the store <b>before</b> running
/// the command (no outbound call inside the transaction), then <c>iap.purchase</c> grants once per external reference.
/// </summary>
public sealed class IapModule : IGameModule, IPlayerApiModule
{
    public const string PurchaseRoute = "/api/v1/iap/purchase";

    public ModuleDescriptor Describe() => IapModuleDefinition.Describe();

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.AddOptions<IapOptions>().BindConfiguration(IapOptions.SectionName);
        services.TryAddSingleton(sp => IapCatalog.Load(sp.GetRequiredService<IGameConfig>()));
        services.AddHttpClient(GooglePlayReceiptVerifier.HttpClientName, (sp, client) =>
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<IapOptions>>().Value.StoreTimeoutSeconds));
        services.AddSingleton<StoreVerifiers>();
    }

    public void MapPlayerApi(IEndpointRouteBuilder routes) =>
        routes.MapPlayerCommand<Wire.IapPurchaseRequest, IapPurchaseCommand, IapPurchaseResult>(
            PurchaseRoute,
            IapPurchaseHandler.Operation,
            VerifyAsync,
            result =>
            {
                var wire = new Wire.IapPurchaseResult();
                wire.Granted.AddRange(result.Granted.Select(g => new Wire.GrantedResource { Resource = g.Resource, Quantity = g.Quantity }));
                return wire;
            });

    private static async Task<IapPurchaseCommand> VerifyAsync(Wire.IapPurchaseRequest request, HttpContext http)
    {
        if (string.IsNullOrEmpty(request.ProductId) || request.ProductId.Length > 128 || string.IsNullOrEmpty(request.ReceiptPayload))
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "product_id and receipt_payload are required.");
        }

        var verifier = http.RequestServices.GetRequiredService<StoreVerifiers>().Find(request.Store)
            ?? throw new GameServerException(CoreErrorCodes.BadRequest, $"Unknown store '{request.Store}'.");
        try
        {
            return new IapPurchaseCommand(await verifier.VerifyAsync(request.ProductId, request.ReceiptPayload, http.RequestAborted));
        }
        catch (StoreUnavailableException ex)
        {
            throw new GameServerException(IapErrors.StoreUnavailable, ex.Message, ex) { RetryAfterMs = 2000 };
        }
    }
}

/// <summary>Resolves the verifier per store: fakes in Development/Test (default there), real adapters elsewhere.</summary>
internal sealed class StoreVerifiers : IDisposable
{
    private readonly Dictionary<string, IStoreReceiptVerifier> _verifiers = new(StringComparer.Ordinal);

    public StoreVerifiers(IServiceProvider services, IOptions<IapOptions> options, GameServerOptions gameServer)
    {
        var useFake = options.Value.UseFakeVerifier ?? gameServer.AllowsDevelopmentTools;
        if (useFake && !gameServer.AllowsDevelopmentTools)
        {
            throw new GameServerStartupException(
                $"[module iap] Iap:UseFakeVerifier is true in '{gameServer.DeploymentEnvironment}'. Fix: configure the Apple/Google verifiers (IAP-01).");
        }

        IStoreReceiptVerifier[] verifiers = useFake
            ? [new FakeStoreReceiptVerifier(GooglePlayReceiptVerifier.StoreName), new FakeStoreReceiptVerifier(AppleJwsReceiptVerifier.StoreName)]
            : [ActivatorUtilities.CreateInstance<GooglePlayReceiptVerifier>(services), ActivatorUtilities.CreateInstance<AppleJwsReceiptVerifier>(services)];
        foreach (var verifier in verifiers)
        {
            _verifiers[verifier.Store] = verifier;
        }
    }

    public IStoreReceiptVerifier? Find(string store) => _verifiers.GetValueOrDefault(store);

    public void Dispose()
    {
        foreach (var verifier in _verifiers.Values.OfType<IDisposable>())
        {
            verifier.Dispose();
        }
    }
}
