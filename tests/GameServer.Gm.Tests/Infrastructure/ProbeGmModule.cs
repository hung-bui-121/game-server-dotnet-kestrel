using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Modules;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Gm.Tests.Infrastructure;

/// <summary>
/// Nested record of <see cref="ProbeGmBody"/> (also the item of its list). <c>Inner</c> is required by the presence rule (a
/// non-nullable value type), <c>Count</c> by <c>[Required]</c> (review r1 m3, m6).
/// </summary>
public sealed record ProbeNested([property: Range(1, 9)] int Inner, [StringLength(4)] string? Tag, [property: Required] int Count);

/// <summary>
/// Slice 017 SC-002 probe body: every supported attribute on the property target (<c>P…</c>) and on the positional parameter
/// (<c>A…</c>), a required number checked for presence, a nested record and a list of records; a defaulted parameter and a computed
/// member that stay optional.
/// </summary>
public sealed record ProbeGmBody(
    [property: Range(1, 100)] int? PRange,
    [Range(1, 100)] int? ARange,
    [property: StringLength(8, MinimumLength = 2)] string? PStringLength,
    [StringLength(8, MinimumLength = 2)] string? AStringLength,
    [property: MinLength(3)] string? PMinLength,
    [MinLength(3)] string? AMinLength,
    [property: MaxLength(5)] string? PMaxLength,
    [MaxLength(5)] string? AMaxLength,
    [property: Length(1, 3)] List<string>? PLength,
    [Length(1, 3)] List<string>? ALength,
    [property: RegularExpression("^[a-z]+$")] string? PPattern,
    [RegularExpression("^[a-z]+$")] string? APattern,
    [property: RegularExpression("[a-z]+")] string? PLoosePattern,
    [property: Description("property description")] string? PDescription,
    [Description("parameter description")] string? ADescription,
    [property: Required] string? PRequired,
    [Required] string? ARequired,
    [property: Required] long PRequiredNumber,
    ProbeNested? Nested,
    [Length(1, 2)] List<ProbeNested>? Items,
    string? Reason,
    int Weight = 3) : IGmCommandBody
{
    /// <summary>Computed, get-only: never read from the JSON, so never required (review r2 M1).</summary>
    public bool HasRange => PRange is not null;
}

public sealed record ProbeState(int Calls);

public sealed record ProbeSetCommand(string Reason);

public sealed record ProbeSetResult(int Calls);

/// <summary>Counts accepted calls on the probe aggregate, with the one audit a GM command writes.</summary>
public sealed class ProbeSetHandler : ICommandHandler<ProbeSetCommand, ProbeSetResult>
{
    public const string Operation = "probe.set";

    public static CommandDescriptor Descriptor { get; } =
        new(Operation, touches: [ProbeGmModule.Aggregate], profile: SecurityProfile.Gm, permission: ProbeGmModule.Permission);

    public ValueTask<CommandOutcome<ProbeSetResult>> HandleAsync(ProbeSetCommand request, ICommandContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var before = context.Get<ProbeState>(ProbeGmModule.Aggregate);
        var after = before with { Calls = before.Calls + 1 };
        context.Set(ProbeGmModule.Aggregate, after);
        context.AddAudit(ProbeGmModule.Permission, Operation, request.Reason, before, after);
        return ValueTask.FromResult(CommandOutcome<ProbeSetResult>.Ok(new ProbeSetResult(after.Calls)));
    }
}

/// <summary>
/// Test-only GM module (slice 017 T022): one GM command over <see cref="ProbeGmBody"/>. Added through
/// <c>GmTestHost.StartAsync(extra: …)</c> by the tests that need it, so the committed framework document never contains it.
/// </summary>
public sealed class ProbeGmModule : IGameModule, IGmApiModule
{
    public const string Permission = "probe.edit";
    public const string Aggregate = "probe_state";
    public const string Template = "/gm/v1/players/{playerId}/probe";

    public static RouteDeclaration Route { get; } = RouteDeclaration.Command(ProbeSetHandler.Descriptor, Template);

    public ModuleDescriptor Describe() => new()
    {
        Name = "probe",
        Permissions = [Permission],
        Aggregates = [new PlayerAggregateDescriptor<ProbeState>(Aggregate, 1, clientVisible: false, () => new ProbeState(0))],
        Commands = [Command.Define<ProbeSetCommand, ProbeSetResult>(ProbeSetHandler.Descriptor).HandledBy<ProbeSetHandler>()],
        Routes = [Route],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
    }

    public void MapGmApi(IEndpointRouteBuilder routes) =>
        routes.MapGmCommand<ProbeGmBody, ProbeSetCommand, ProbeSetResult>(Route, body => new ProbeSetCommand(body.Reason!));
}
