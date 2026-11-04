using GameServer.Actors;
using GameServer.Commands;
using GameServer.Platform.Registry;

namespace GameServer.Platform.Commands;

/// <summary>The account creation of a first login (FG-015): a Platform transaction written by <see cref="IAccountWriter"/>.</summary>
public static class AccountCreation
{
    /// <summary>Reserved operation of the creation receipt; no module command may use it.</summary>
    public const string Operation = "identity.create_account";

    /// <summary>
    /// The creation descriptor (data-model §1.1): operation <see cref="Operation"/>, touches = the union of the initializers'
    /// touches in first-seen order, claim markers = their sum, profile Player (the audit rule expects no entry).
    /// </summary>
    internal static CommandDescriptor Describe(ModuleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var initializers = registry.AccountInitializers;
        return new CommandDescriptor(
            Operation,
            ModuleRegistry.CreationTouches(initializers),
            maxClaimMarkers: initializers.Sum(i => i.Registration.MaxClaimMarkers),
            profile: SecurityProfile.Player);
    }

    /// <summary>The operation label of one initializer in rule lines: <c>identity.create_account (initializer {name})</c>.</summary>
    internal static string InitializerOperation(string initializerName) => $"{Operation} (initializer {initializerName})";
}
