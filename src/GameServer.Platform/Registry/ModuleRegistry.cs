using System.Text.RegularExpressions;
using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Config;
using GameServer.Errors;
using GameServer.Modules;

namespace GameServer.Platform.Registry;

/// <summary>A module added to the host and how it was registered.</summary>
public sealed record RegisteredModule(IGameModule Module, ModuleDescriptor Descriptor, bool ServiceOnly);

/// <summary>
/// Everything modules declared: aggregates, commands, error codes, outbox projections (ARC-03, ARC-06, ARC-07).
/// Built while the host registers modules; validated once before serving traffic.
/// </summary>
public sealed partial class ModuleRegistry
{
    /// <summary>Aggregate every game must register (DAT-09).</summary>
    public const string RequiredProfileAggregate = "profile";

    private readonly List<RegisteredModule> _modules = [];
    private readonly Dictionary<string, CommandRegistration> _commandOverrides = new(StringComparer.Ordinal);
    private readonly List<string> _registrationErrors = [];
    private Dictionary<string, PlayerAggregateDescriptor>? _aggregates;
    private Dictionary<string, CommandRegistration>? _commands;
    private ErrorCodeCatalog? _errors;

    public IReadOnlyList<RegisteredModule> Modules => _modules;

    public IReadOnlyDictionary<string, PlayerAggregateDescriptor> Aggregates => _aggregates ??= BuildAggregates();

    public IReadOnlyDictionary<string, CommandRegistration> Commands => _commands ??= BuildCommands();

    public ErrorCodeCatalog Errors => _errors ??= BuildErrors();

    public IEnumerable<OutboxProjection> OutboxProjections => _modules.SelectMany(m => m.Descriptor.OutboxProjections);

    public void Add(IGameModule module, bool serviceOnly)
    {
        var descriptor = module.Describe();
        _modules.Add(new RegisteredModule(module, descriptor, serviceOnly));
        Invalidate();
    }

    /// <summary>Replaces the handler of a registered command (EXT-02). Applied after framework modules.</summary>
    public void ReplaceHandler(string operation, Type handlerType)
    {
        var current = _commandOverrides.GetValueOrDefault(operation)
            ?? _modules.SelectMany(m => m.Descriptor.Commands).FirstOrDefault(c => c.Descriptor.Operation == operation);
        if (current is null)
        {
            _registrationErrors.Add(
                $"[extensions] ReplaceHandler('{operation}'): no module registers this command. " +
                "Fix: register the module that owns the command before calling ConfigureExtensions.");
            return;
        }

        try
        {
            _commandOverrides[operation] = current.WithHandler(handlerType);
        }
        catch (ArgumentException ex)
        {
            _registrationErrors.Add($"[extensions] ReplaceHandler('{operation}'): {ex.Message}");
        }

        Invalidate();
    }

    public CommandRegistration GetCommand(string operation) =>
        Commands.TryGetValue(operation, out var registration)
            ? registration
            : throw new GameServerException(CoreErrorCodes.NotFound, $"Unknown command '{operation}'.");

    public PlayerAggregateDescriptor GetAggregate(string aggregateId) =>
        Aggregates.TryGetValue(aggregateId, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException($"Unknown aggregate '{aggregateId}'.");

    /// <summary>Returns every violation of the registration rules; empty when valid.</summary>
    public IReadOnlyList<string> Validate(GameServerOptions options, IGameConfig? config)
    {
        var errors = new List<string>(_registrationErrors);
        ValidateOptions(options, errors);
        ValidateModules(errors);
        ValidateAggregates(options, errors);
        ValidateCommands(errors);
        ValidateErrorCodes(errors);
        ValidateOutbox(errors);
        if (config is not null)
        {
            ValidateConfig(config, errors);
        }

        return errors;
    }

    private static void ValidateOptions(GameServerOptions options, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(options.GameId) || !ScopeIdPattern().IsMatch(options.GameId))
        {
            errors.Add(
                $"[deployment] GameServer:GameId '{options.GameId}' is missing or invalid. " +
                "Fix: set GameServer:GameId (lowercase letters, digits, '_', max 24) in deployment config (ARC-10).");
        }

        if (string.IsNullOrWhiteSpace(options.EnvironmentId) || !ScopeIdPattern().IsMatch(options.EnvironmentId))
        {
            errors.Add(
                $"[deployment] GameServer:EnvironmentId '{options.EnvironmentId}' is missing or invalid. " +
                "Fix: set GameServer:EnvironmentId in deployment config (ARC-10).");
        }
    }

    private void ValidateModules(List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in _modules)
        {
            if (!names.Add(module.Descriptor.Name))
            {
                errors.Add($"[module {module.Descriptor.Name}] registered twice. Fix: call AddModule once per module.");
            }
        }

        foreach (var module in _modules)
        {
            foreach (var dependency in module.Descriptor.DependsOn.Where(d => !names.Contains(d)))
            {
                errors.Add(
                    $"[module {module.Descriptor.Name}] depends on module '{dependency}' which is not registered. " +
                    $"Fix: AddModule the '{dependency}' module before '{module.Descriptor.Name}'.");
            }
        }
    }

    private void ValidateAggregates(GameServerOptions options, List<string> errors)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in _modules)
        {
            foreach (var aggregate in module.Descriptor.Aggregates)
            {
                var owner = module.Descriptor.Name;
                if (!PlayerAggregateDescriptor.IsValidId(aggregate.AggregateId))
                {
                    errors.Add($"[module {owner}] aggregate id '{aggregate.AggregateId}' is invalid. Fix: use [a-z][a-z0-9_]{{1,31}}.");
                }

                if (!seen.TryAdd(aggregate.AggregateId, owner))
                {
                    errors.Add(
                        $"[module {owner}] aggregate '{aggregate.AggregateId}' is already registered by module '{seen[aggregate.AggregateId]}'. " +
                        "Fix: aggregate ids must be unique per game.");
                }

                if (aggregate.SchemaVersion < 1)
                {
                    errors.Add($"[module {owner}] aggregate '{aggregate.AggregateId}' has schemaVersion {aggregate.SchemaVersion}. Fix: start at 1.");
                }

                for (var version = 1; version < aggregate.SchemaVersion; version++)
                {
                    var count = aggregate.Upgraders.Count(u => u.FromVersion == version);
                    if (count != 1)
                    {
                        errors.Add(
                            $"[module {owner}] aggregate '{aggregate.AggregateId}' needs exactly one upgrader from v{version} (found {count}). " +
                            "Fix: register an IAggregateUpgrader per version step (DAT-02).");
                    }
                }

                if (aggregate.WarnBytes <= 0 || aggregate.WarnBytes > aggregate.HardBytes || aggregate.HardBytes > PlayerAggregateDescriptor.DefaultHardBytes)
                {
                    errors.Add(
                        $"[module {owner}] aggregate '{aggregate.AggregateId}' size budget {aggregate.WarnBytes}/{aggregate.HardBytes} is invalid. " +
                        "Fix: 0 < warn <= hard <= 65536 (DAT-09).");
                }
            }
        }

        if (seen.Count > options.Commands.MaxAggregatesPerGame)
        {
            errors.Add(
                $"[deployment] {seen.Count} player aggregates registered (max {options.Commands.MaxAggregatesPerGame}). " +
                "Fix: merge aggregates or remove modules (DAT-09).");
        }

        if (!seen.ContainsKey(RequiredProfileAggregate))
        {
            errors.Add(
                $"[deployment] required aggregate '{RequiredProfileAggregate}' is not registered. " +
                "Fix: AddModule<IdentityModule>() (DAT-09).");
        }
    }

    private void ValidateCommands(List<string> errors)
    {
        var aggregates = _modules.SelectMany(m => m.Descriptor.Aggregates).Select(a => a.AggregateId).ToHashSet(StringComparer.Ordinal);
        var permissions = _modules.SelectMany(m => m.Descriptor.Permissions).ToHashSet(StringComparer.Ordinal);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var module in _modules)
        {
            foreach (var command in module.Descriptor.Commands)
            {
                var owner = module.Descriptor.Name;
                var d = command.Descriptor;
                if (!OperationPattern().IsMatch(d.Operation))
                {
                    errors.Add($"[module {owner}] command '{d.Operation}' has an invalid name. Fix: use '<module>.<command>' in lowercase.");
                }

                if (!seen.TryAdd(d.Operation, owner))
                {
                    errors.Add(
                        $"[module {owner}] command '{d.Operation}' is already registered by module '{seen[d.Operation]}'. " +
                        "Fix: use ConfigureExtensions(x => x.ReplaceHandler(...)) to override a handler (ARC-07).");
                }

                if (d.Touches.Count is < 1 or > CommandDescriptor.MaxTouches || d.Touches.Distinct().Count() != d.Touches.Count)
                {
                    errors.Add(
                        $"[module {owner}] command '{d.Operation}' touches {d.Touches.Count} aggregates. " +
                        $"Fix: declare 1..{CommandDescriptor.MaxTouches} distinct aggregates in Touches (DAT-09).");
                }

                foreach (var id in d.Touches.Concat(d.Reads).Where(id => !aggregates.Contains(id)))
                {
                    errors.Add($"[module {owner}] command '{d.Operation}' references unknown aggregate '{id}'. Fix: register the aggregate or fix Touches/Reads.");
                }

                if (d.MaxClaimMarkers is < 0 or > CommandDescriptor.MaxClaimMarkersLimit)
                {
                    errors.Add(
                        $"[module {owner}] command '{d.Operation}' allows {d.MaxClaimMarkers} claim markers. " +
                        $"Fix: use 0..{CommandDescriptor.MaxClaimMarkersLimit} (DAT-04).");
                }

                if (d.Profile is SecurityProfile.Public)
                {
                    errors.Add($"[module {owner}] command '{d.Operation}' uses profile Public. Fix: commands need an authenticated actor.");
                }

                if (d.Profile is SecurityProfile.Gm && (d.Permission is null || !permissions.Contains(d.Permission)))
                {
                    errors.Add(
                        $"[module {owner}] GM command '{d.Operation}' requires permission '{d.Permission}' which is not in any module's permission catalog. " +
                        "Fix: declare the permission in ModuleDescriptor.Permissions (SEC-07).");
                }
            }
        }
    }

    private void ValidateErrorCodes(List<string> errors)
    {
        var ranges = new List<(string Owner, int Start, int End)> { ("core", CoreErrorCodes.RangeStart, CoreErrorCodes.RangeEnd) };
        var names = CoreErrorCodes.All.Codes.ToDictionary(c => c.Name, _ => "core", StringComparer.Ordinal);
        var numbers = CoreErrorCodes.All.Codes.ToDictionary(c => c.Number, _ => "core");

        foreach (var module in _modules)
        {
            var owner = module.Descriptor.Name;
            var set = module.Descriptor.ErrorCodes;
            if (set.IsNone)
            {
                continue;
            }

            foreach (var other in ranges.Where(r => set.RangeStart <= r.End && r.Start <= set.RangeEnd))
            {
                errors.Add(
                    $"[module {owner}] error-code range {set.RangeStart}-{set.RangeEnd} overlaps '{other.Owner}' ({other.Start}-{other.End}). " +
                    "Fix: choose a free range (API-11).");
            }

            ranges.Add((owner, set.RangeStart, set.RangeEnd));

            foreach (var code in set.Codes)
            {
                if (code.Number < set.RangeStart || code.Number > set.RangeEnd)
                {
                    errors.Add($"[module {owner}] error code {code} is outside the module range {set.RangeStart}-{set.RangeEnd}.");
                }

                if (!ErrorNamePattern().IsMatch(code.Name))
                {
                    errors.Add($"[module {owner}] error code name '{code.Name}' is invalid. Fix: use UPPER_SNAKE_CASE.");
                }

                if (!names.TryAdd(code.Name, owner))
                {
                    errors.Add($"[module {owner}] error code name '{code.Name}' duplicates one from '{names[code.Name]}' (ARC-07).");
                }

                if (!numbers.TryAdd(code.Number, owner))
                {
                    errors.Add($"[module {owner}] error code number {code.Number} duplicates one from '{numbers[code.Number]}' (ARC-07).");
                }
            }
        }
    }

    private void ValidateOutbox(List<string> errors)
    {
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in _modules)
        {
            foreach (var projection in module.Descriptor.OutboxProjections)
            {
                if (!kinds.TryAdd(projection.Kind, module.Descriptor.Name))
                {
                    errors.Add($"[module {module.Descriptor.Name}] outbox kind '{projection.Kind}' is already projected by '{kinds[projection.Kind]}'.");
                }
            }
        }
    }

    private void ValidateConfig(IGameConfig config, List<string> errors)
    {
        foreach (var module in _modules)
        {
            foreach (var file in module.Descriptor.RequiredConfigFiles.Where(f => !config.HasFile(f)))
            {
                errors.Add(
                    $"[module {module.Descriptor.Name}] required config file '{file}' is missing. " +
                    "Fix: add it to config/base/manifest.json 'files' and to config/base (CFG-01).");
            }
        }
    }

    private void Invalidate()
    {
        _aggregates = null;
        _commands = null;
        _errors = null;
    }

    private Dictionary<string, PlayerAggregateDescriptor> BuildAggregates()
    {
        var result = new Dictionary<string, PlayerAggregateDescriptor>(StringComparer.Ordinal);
        foreach (var aggregate in _modules.SelectMany(m => m.Descriptor.Aggregates))
        {
            result.TryAdd(aggregate.AggregateId, aggregate);
        }

        return result;
    }

    private Dictionary<string, CommandRegistration> BuildCommands()
    {
        var result = new Dictionary<string, CommandRegistration>(StringComparer.Ordinal);
        foreach (var command in _modules.SelectMany(m => m.Descriptor.Commands))
        {
            result.TryAdd(command.Descriptor.Operation, command);
        }

        foreach (var (operation, registration) in _commandOverrides)
        {
            result[operation] = registration;
        }

        return result;
    }

    private ErrorCodeCatalog BuildErrors() =>
        new(CoreErrorCodes.All.Codes.Concat(_modules.SelectMany(m => m.Descriptor.ErrorCodes.Codes)));

    [GeneratedRegex("^[a-z0-9_]{1,24}$")]
    private static partial Regex ScopeIdPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$")]
    private static partial Regex OperationPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]*$")]
    private static partial Regex ErrorNamePattern();
}
