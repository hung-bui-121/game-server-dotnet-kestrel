#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Fixture.V1;
using GameServer.Client;
using Google.Protobuf.WellKnownTypes;

namespace Fixture.Client
{
    /// <summary>
    /// Game client code written against the generated files (compile proof, SC-004): routes from <see cref="FixtureRoutes"/>, the
    /// card section through its generated key, no copied route string or section id.
    /// </summary>
    public static class FixtureClientUsage
    {
        /// <summary>Saves the note through the generated route constant.</summary>
        public static Task<CommandResult<StringValue>> SaveAsync(GameServerClient client, string text, CancellationToken cancellationToken = default) =>
            client.SendCommandAsync(FixtureRoutes.FixtureSave, new StringValue { Value = text }, StringValue.Parser, cancellationToken);

        /// <summary>The card section received with the snapshot or a command, parsed with its generated key; null when absent.</summary>
        public static CardState? Card(GameServerClient client) =>
            client.Sections.Get(FixtureSections.Card.Id, FixtureSections.Card.TypedParser);

        /// <summary>The generated table lists the card section.</summary>
        public static bool KnowsCard() => FixtureSections.Table.TryGet(FixtureSections.CardId, out _);
    }
}
