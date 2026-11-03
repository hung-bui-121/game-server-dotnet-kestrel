using GameServer.Aggregates;
using GameServer.Modules;
using GameServer.Platform.Web.Aggregates;
using Microsoft.Extensions.DependencyInjection;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// A module still on the obsolete untyped wire mapper (BC-3): one client-visible aggregate <c>probe_legacy</c> whose section has no
/// message in the catalog and gets the startup Warning (FG-028).
/// </summary>
public sealed class UntypedMapperProbeModule : IGameModule
{
    public const string Name = "probe.legacy";
    public const string Legacy = "probe_legacy";

    public static PlayerAggregateDescriptor<ProbeCard> LegacyDescriptor { get; } = new(Legacy, 1, clientVisible: true, () => new ProbeCard());

    public ModuleDescriptor Describe() => new() { Name = Name, Aggregates = [LegacyDescriptor] };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        // The obsolete overload is the subject of these tests (BC-3, kept through v0.7.x).
#pragma warning disable CS0618
        services.AddAggregateWireMapper<ProbeCard>(Legacy, state => new Wire.CounterState { Value = state.Value });
#pragma warning restore CS0618
    }
}
