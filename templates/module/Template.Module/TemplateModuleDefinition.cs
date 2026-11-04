using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Config;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Routing;

namespace Template.Module;

// Copy this project, rename "Template"/"template" and adjust ids, ranges and rules.

/// <summary>State of the module's player aggregate.</summary>
public sealed record TemplateState
{
    public long Points { get; init; }
}

public sealed record EarnCommand(long Amount);

public sealed record EarnResult(long Points);

/// <summary>Replaceable rule (EXT-02).</summary>
public interface ITemplatePolicy
{
    bool Allows(EarnCommand command);
}

public sealed class DefaultTemplatePolicy : ITemplatePolicy
{
    public bool Allows(EarnCommand command) => command.Amount is > 0 and <= 100;
}

public static class TemplateErrors
{
    public static readonly ErrorCode NotAllowed = new(90001, "TEMPLATE_NOT_ALLOWED", 200, false, "The action is not allowed.");

    public static ErrorCodeSet All { get; } = new(90000, 90099, NotAllowed);
}

public sealed class EarnHandler(ITemplatePolicy policy) : ICommandHandler<EarnCommand, EarnResult>
{
    public const string Operation = "template.earn";

    public static CommandDescriptor Descriptor { get; } = new(Operation, touches: [TemplateModuleDefinition.AggregateId]);

    public ValueTask<CommandOutcome<EarnResult>> HandleAsync(EarnCommand request, ICommandContext context)
    {
        if (!policy.Allows(request))
        {
            return ValueTask.FromResult(CommandOutcome<EarnResult>.Reject(TemplateErrors.NotAllowed));
        }

        var state = context.Get<TemplateState>(TemplateModuleDefinition.AggregateId);
        var next = state with { Points = state.Points + request.Amount };
        context.Set(TemplateModuleDefinition.AggregateId, next);
        return ValueTask.FromResult(CommandOutcome<EarnResult>.Ok(new EarnResult(next.Points)));
    }
}

/// <summary>HTTP routes of the module (FG-026), declared next to its commands; the adapter maps from them.</summary>
public static class TemplateRoutes
{
    public static RouteDeclaration Earn { get; } = RouteDeclaration.Command(EarnHandler.Descriptor, "/api/v1/template/earn");
}

public static class TemplateModuleDefinition
{
    public const string ModuleName = "template.module";
    public const string AggregateId = "template";

    /// <summary>Config file of the module (CFG-01): optional here, so a host without it still starts.</summary>
    public const string ConfigFile = "template.json";

    public static PlayerAggregateDescriptor<TemplateState> Aggregate { get; } =
        new(AggregateId, schemaVersion: 1, clientVisible: true, createDefault: () => new TemplateState());

    public static ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        DependsOn = ["identity"],
        Aggregates = [Aggregate],
        Commands = [Command.Define<EarnCommand, EarnResult>(EarnHandler.Descriptor).HandledBy<EarnHandler>()],
        ErrorCodes = TemplateErrors.All,
        Routes = [TemplateRoutes.Earn],

        // Every config file the module reads, with its content rules (checked at every start, FG-018). Use Required(...) for a
        // file the module cannot work without.
        ConfigFiles = [ConfigFileDeclaration.Optional(ConfigFile, TemplateConfigRules.Points)],

        // A typed catalog built once from the files before traffic; handlers take it from DI. Declare it here, never register
        // it in ConfigureServices (an undeclared catalog stays lazy and its faults surface on the first request). The file is
        // optional, so the build runs without it too and must return an empty catalog then, here with MyCatalog (your catalog type):
        // ConfigCatalogs = [ConfigCatalogDeclaration.Of(c => c.Config.HasFile(ConfigFile) ? MyCatalog.Load(c.Config) : MyCatalog.Empty, ConfigFile)],

        // The starting state of a new account (FG-015): an IAccountInitializer runs once per account, in the transaction that
        // creates it, here with MyStartingState (your initializer type, `using GameServer.Accounts;`):
        //   public sealed class MyStartingState : IAccountInitializer
        //   {
        //       public ValueTask<ErrorCode?> InitializeAsync(ICommandContext context)
        //       {
        //           context.Set(TemplateModuleDefinition.AggregateId, new TemplateState { Points = 10 });
        //           return ValueTask.FromResult<ErrorCode?>(null);
        //       }
        //   }
        // - Touches: the aggregates it may read and write, never "profile". profile + every initializer's touches is at most 4
        //   aggregates, and every initializer's claim markers together at most 2 (checked at startup, one line per fault).
        // - Pure over the context, like a handler: the transaction may run it several times; no I/O, clock or random of its own.
        // - A returned code or a throw fails the creation: nothing is written, the login answers INTERNAL with one log line naming
        //   the initializer, and the next login retries. Accounts created before the registration are never seeded.
        // - Test it host-free: CommandContextDouble.ForAccountInitializer("template.starting_state", modules) then
        //   CheckAccountCreation() (consumer guide §13.2).
        // AccountInitializers = [AccountInitializerRegistration.Of<MyStartingState>("template.starting_state", [AggregateId])],
    };
}

/// <summary>Content rules of <c>template.json</c> (FG-018): one line per fault, written with <see cref="ConfigJson"/>.</summary>
public static class TemplateConfigRules
{
    /// <summary>Rule <c>template.points</c>: <c>maxPoints</c> is an integer of at least 1.</summary>
    public static ConfigRule Points { get; } = new("template.points", CheckPoints);

    private static void CheckPoints(ConfigContext context) =>
        ConfigJson.RequiredInteger(
            context.Json(TemplateModuleDefinition.ConfigFile),
            "maxPoints",
            ConfigLocation.WholeFile,
            context.Problems,
            min: 1,
            fix: "set the most points a player may hold, at least 1");
}
