using GameServer.Accounts;
using GameServer.Commands;
using GameServer.Errors;

namespace Fixture.Game;

/// <summary>
/// Account initializer <c>fixture.welcome</c> (FG-015): a new player starts with a welcome note, cut to the <c>maxLength</c> of
/// <c>notes.json</c>. It runs in the transaction that creates the account and writes only the hidden <c>notes</c> aggregate, so the
/// client-visible <c>card</c> still starts empty. Pure over the context: the transaction may run it more than once.
/// </summary>
public sealed class FixtureWelcomeInitializer : IAccountInitializer
{
    public const string Name = "fixture.welcome";
    public const string WelcomeText = "Welcome! Write your first note here.";

    public static AccountInitializerRegistration Registration { get; } =
        AccountInitializerRegistration.Of<FixtureWelcomeInitializer>(Name, [FixtureModuleDefinition.NotesAggregate]);

    public ValueTask<ErrorCode?> InitializeAsync(ICommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var maxLength = context.Config.GetFile<NotesRules>(FixtureModuleDefinition.RulesFile).MaxLength;
        var text = WelcomeText.Length <= maxLength ? WelcomeText : WelcomeText[..maxLength];
        context.Set(FixtureModuleDefinition.NotesAggregate, new NotesState { Text = text, Saves = 0 });
        return ValueTask.FromResult<ErrorCode?>(null);
    }
}
