namespace GameServer.Platform.Commands;

/// <summary>The account creation of a first login (FG-015): a Platform transaction written by <see cref="IAccountWriter"/>.</summary>
public static class AccountCreation
{
    /// <summary>Reserved operation of the creation receipt; no module command may use it.</summary>
    public const string Operation = "identity.create_account";
}
