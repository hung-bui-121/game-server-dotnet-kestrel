using GameServer.Commands;
using GameServer.Errors;

namespace GameServer.Accounts;

/// <summary>
/// Runs once per account, inside the transaction that creates the account (FG-015). Pure over the context like a command
/// handler (CMD-04): the transaction may run it several times. Returns null for OK, or a code that fails the creation
/// (logged with the initializer's name; the login answers INTERNAL). Created per run from DI (constructor injection).
/// </summary>
public interface IAccountInitializer
{
    ValueTask<ErrorCode?> InitializeAsync(ICommandContext context);
}

/// <summary>
/// An initializer with the aggregates it may write and the claim markers it may reserve, listed in
/// <c>ModuleDescriptor.AccountInitializers</c>. The values are checked at startup (one report line per fault), not here.
/// </summary>
public sealed class AccountInitializerRegistration
{
    private AccountInitializerRegistration(string name, Type initializerType, IReadOnlyList<string> touches, int maxClaimMarkers)
    {
        Name = name;
        InitializerType = initializerType;
        Touches = touches;
        MaxClaimMarkers = maxClaimMarkers;
    }

    /// <summary><c>&lt;module&gt;.&lt;name&gt;</c>, lowercase, unique per deployment.</summary>
    public string Name { get; }

    public Type InitializerType { get; }

    /// <summary>Aggregates the initializer may read and write, in declaration order; never <c>profile</c>.</summary>
    public IReadOnlyList<string> Touches { get; }

    /// <summary>Claim markers the initializer may reserve (0..2; the sum over every initializer is at most 2).</summary>
    public int MaxClaimMarkers { get; }

    /// <summary>Registers <typeparamref name="TInitializer"/>; <paramref name="touches"/> is copied in its order.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="touches"/> is null.</exception>
    public static AccountInitializerRegistration Of<TInitializer>(string name, IReadOnlyList<string> touches, int maxClaimMarkers = 0)
        where TInitializer : IAccountInitializer
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(touches);
        return new AccountInitializerRegistration(name, typeof(TInitializer), touches.ToArray(), maxClaimMarkers);
    }
}
