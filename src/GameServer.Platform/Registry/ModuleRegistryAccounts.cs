using System.Text.RegularExpressions;
using GameServer.Accounts;
using GameServer.Commands;
using GameServer.Platform.Commands;

namespace GameServer.Platform.Registry;

/// <summary>An account initializer with the module that registered it (run order: module registration, then list order).</summary>
internal sealed record RegisteredAccountInitializer(string Module, AccountInitializerRegistration Registration);

/// <summary>Account initializers (FG-015): the run order and the startup checks of errors-and-report-lines §4.1 (BC-9).</summary>
public sealed partial class ModuleRegistry
{
    /// <summary>Every registered initializer in run order: module registration order, then the order of the descriptor's list.</summary>
    internal IReadOnlyList<RegisteredAccountInitializer> AccountInitializers =>
        _modules.SelectMany(m => m.Descriptor.AccountInitializers.Select(r => new RegisteredAccountInitializer(m.Descriptor.Name, r))).ToArray();

    /// <summary>One line per fault of the initializer declarations, and the reserved creation operation (research R4).</summary>
    private void ValidateAccountInitializers(List<string> errors)
    {
        var aggregates = _modules.SelectMany(m => m.Descriptor.Aggregates).Select(a => a.AggregateId).ToHashSet(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (module, registration) in AccountInitializers)
        {
            var name = registration.Name;
            var prefix = ModulePrefix(module);
            if (!InitializerNamePattern().IsMatch(name) || !name.StartsWith(prefix, StringComparison.Ordinal))
            {
                errors.Add($"[module {module}] account initializer '{name}' has an invalid name. Fix: use '{prefix}<name>' in lowercase.");
            }

            if (!names.TryAdd(name, module))
            {
                errors.Add(
                    $"[module {module}] account initializer '{name}' is already registered by module '{names[name]}'. " +
                    "Fix: initializer names must be unique per game.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in registration.Touches)
            {
                if (!seen.Add(id))
                {
                    errors.Add($"[module {module}] account initializer '{name}' declares aggregate '{id}' twice. Fix: list each aggregate once in Touches.");
                }
                else if (string.Equals(id, RequiredProfileAggregate, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"[module {module}] account initializer '{name}' touches '{RequiredProfileAggregate}'. " +
                        "Fix: remove it; the framework writes the profile of a new account.");
                }
                else if (!aggregates.Contains(id))
                {
                    errors.Add($"[module {module}] account initializer '{name}' touches unknown aggregate '{id}'. Fix: register the aggregate or fix Touches.");
                }
            }

            if (registration.MaxClaimMarkers is < 0 or > CommandDescriptor.MaxClaimMarkersLimit)
            {
                errors.Add(
                    $"[module {module}] account initializer '{name}' allows {registration.MaxClaimMarkers} claim markers. " +
                    $"Fix: use 0..{CommandDescriptor.MaxClaimMarkersLimit} (DAT-04).");
            }
        }

        ValidateCreationBudget(errors);

        foreach (var module in _modules)
        {
            if (module.Descriptor.Commands.Any(c => string.Equals(c.Descriptor.Operation, AccountCreation.Operation, StringComparison.Ordinal)))
            {
                errors.Add(
                    $"[module {module.Descriptor.Name}] command '{AccountCreation.Operation}' uses the operation reserved for account creation. " +
                    "Fix: rename the command.");
            }
        }
    }

    /// <summary>The creation is one transaction under the command budget (D4): profile + the union of touches, and the claim sum.</summary>
    private void ValidateCreationBudget(List<string> errors)
    {
        var initializers = AccountInitializers;
        if (initializers.Count == 0)
        {
            return;
        }

        var union = CreationTouches(initializers);
        var count = union.Count + 1;
        if (count > CommandDescriptor.MaxTouches)
        {
            errors.Add(
                $"[deployment] account creation touches {count} aggregates ({RequiredProfileAggregate} + {string.Join(", ", union)} from initializers " +
                $"{string.Join(", ", initializers.Select(i => i.Registration.Name))}); max is {CommandDescriptor.MaxTouches}. " +
                "Fix: move state into fewer aggregates or seed it in a later command (DAT-09).");
        }

        var claiming = initializers.Where(i => i.Registration.MaxClaimMarkers > 0).ToArray();
        var sum = claiming.Sum(i => i.Registration.MaxClaimMarkers);
        if (sum > CommandDescriptor.MaxClaimMarkersLimit)
        {
            errors.Add(
                $"[deployment] account initializers reserve {sum} claim markers " +
                $"({string.Join(", ", claiming.Select(i => $"{i.Registration.Name}: {i.Registration.MaxClaimMarkers}"))}); " +
                $"max is {CommandDescriptor.MaxClaimMarkersLimit}. Fix: reserve fewer claims (DAT-04).");
        }
    }

    /// <summary>Union of the initializers' touches in first-seen order, without <c>profile</c> (data-model §1.1).</summary>
    internal static IReadOnlyList<string> CreationTouches(IEnumerable<RegisteredAccountInitializer> initializers) =>
        initializers
            .SelectMany(i => i.Registration.Touches)
            .Where(id => !string.Equals(id, RequiredProfileAggregate, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The first segment of the module name and a dot (<c>sample.game</c> → <c>sample.</c>): an initializer name starts with it, so
    /// the name says which module's code runs in every creation (T082 m10).
    /// </summary>
    private static string ModulePrefix(string module)
    {
        var dot = module.IndexOf('.', StringComparison.Ordinal);
        return (dot < 0 ? module : module[..dot]) + ".";
    }

    /// <summary>Two or more dot-separated segments, each a lowercase letter then letters, digits or <c>_</c> (data-model §1.1).</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex InitializerNamePattern();
}
