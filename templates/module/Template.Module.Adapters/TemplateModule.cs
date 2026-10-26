using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wire = Template.Module.Protocol.V1;

namespace Template.Module.Adapters;

/// <summary>Adapter half: registration, endpoints, wire mapping. Host: <c>AddModule&lt;TemplateModule&gt;()</c>.</summary>
public sealed class TemplateModule : IGameModule, IPlayerApiModule
{
    public ModuleDescriptor Describe() => TemplateModuleDefinition.Describe();

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.TryAddSingleton<ITemplatePolicy, DefaultTemplatePolicy>();
        services.AddAggregateWireMapper<TemplateState>(TemplateModuleDefinition.AggregateId, s => new Wire.TemplateState { Points = s.Points });
    }

    public void MapPlayerApi(IEndpointRouteBuilder routes) =>
        routes.MapPlayerCommand<Wire.EarnRequest, EarnCommand, EarnResult>(
            "/api/v1/template/earn",
            EarnHandler.Operation,
            ToDomain,
            result => new Wire.EarnResult { Points = result.Points });

    /// <summary>Wire validation and mapping stay in the adapter (ARC-02).</summary>
    private static EarnCommand ToDomain(Wire.EarnRequest request) =>
        request.Amount < 0
            ? throw new GameServerException(CoreErrorCodes.BadRequest, "amount must not be negative.")
            : new EarnCommand(request.Amount);
}
