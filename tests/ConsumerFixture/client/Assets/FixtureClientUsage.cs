#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Fixture.V1;
using GameServer.Client;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Fixture.Client
{
    /// <summary>
    /// Game client code written against the generated files (compile proof, SC-004): routes from <see cref="FixtureRoutes"/>, sections
    /// through their generated keys and table, no copied route string or section id.
    /// </summary>
    public static class FixtureClientUsage
    {
        /// <summary>Once, after creating the client: the store then parses the game's sections too (FG-028).</summary>
        public static void RegisterSections(GameServerClient client) => client.Sections.Register(FixtureSections.Table);

        /// <summary>Saves the note through the generated route constant.</summary>
        public static Task<CommandResult<StringValue>> SaveAsync(GameServerClient client, string text, CancellationToken cancellationToken = default) =>
            client.SendCommandAsync(FixtureRoutes.FixtureSave, new StringValue { Value = text }, StringValue.Parser, cancellationToken);

        /// <summary>The card section received with the snapshot or a command, parsed with its generated key; null when absent.</summary>
        public static CardState? Card(GameServerClient client) => client.Sections.Get(FixtureSections.Card);

        /// <summary>A framework section through the package's generated key.</summary>
        public static GameServer.Modules.Economy.Protocol.V1.WalletState? Wallet(GameServerClient client) => client.Sections.Get(FrameworkSections.Wallet);

        /// <summary>A changed section read by id: parsed when a registered table knows it, kept raw otherwise.</summary>
        public static string Describe(GameServerClient client, string sectionId)
        {
            switch (client.Sections.TryRead(sectionId, out IMessage? message))
            {
                case SectionRead.Parsed when message is CardState card:
                    return "card " + card.Title;
                case SectionRead.Parsed:
                    return "framework section " + sectionId;
                case SectionRead.Unknown:
                    return "unknown section " + sectionId + ": update the client";
                default:
                    return "absent";
            }
        }
    }
}
