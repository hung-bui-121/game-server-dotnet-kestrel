#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GameServer.Client;
using SampleGame.Protocol.V1;
using UnityEngine;

namespace SampleGame.Client
{
    /// <summary>
    /// KestrelDemo: SampleGame (G7) driven through the SDK — guest login → config → pending commands → battle start/end → shop. Point
    /// <see cref="serverUrl"/> at a running SampleHost (<c>dotnet run --project samples/SampleHost</c>). The SDK owns transport,
    /// retries and resync; this script only calls commands, reads sections and shows what happened on screen.
    /// </summary>
    public sealed class SampleGameClient : MonoBehaviour
    {
        /// <summary>PlayerPrefs keys <c>gs.localId</c> and <c>gs.secret</c> of the scene client's guest device.</summary>
        private const string DeviceKeyPrefix = "gs";

        [SerializeField]
        private string serverUrl = "http://localhost:5080/";

        private readonly List<string> _lines = new List<string>();

        private async void Start()
        {
            var platform = Application.platform == RuntimePlatform.Android ? "android"
                : Application.platform == RuntimePlatform.IPhonePlayer ? "ios"
                : "editor";
            var client = SampleGameFlow.CreateClient(new Uri(serverUrl), platform, Log);
            var (localId, secret) = SampleGameFlow.StoredDevice(DeviceKeyPrefix);
            try
            {
                await SampleGameFlow.RunAsync(client, localId, secret, Log);
            }
            catch (GameServerException ex)
            {
                Log($"Server error: {ex.Error}");
            }
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(16, 16, Screen.width - 32, Screen.height - 32));
            GUILayout.Label($"KestrelDemo → {serverUrl}");
            foreach (var line in _lines)
            {
                GUILayout.Label(line);
            }

            GUILayout.EndArea();
        }

        private void Log(string line)
        {
            Debug.Log(line);
            _lines.Add(line);
        }
    }

    /// <summary>Outcome of one run of the sample flow.</summary>
    public sealed class SampleGameRun
    {
        public SampleGameRun(
            string playerId, bool isNewPlayer, string startCode, string? endCode, string? endPendingId, string buyCode, long gold, long walletRev)
        {
            PlayerId = playerId;
            IsNewPlayer = isNewPlayer;
            StartCode = startCode;
            EndCode = endCode;
            EndPendingId = endPendingId;
            BuyCode = buyCode;
            Gold = gold;
            WalletRev = walletRev;
        }

        public string PlayerId { get; }

        /// <summary>The login created the player (FG-013).</summary>
        public bool IsNewPlayer { get; }

        public string StartCode { get; }

        /// <summary>Null when the battle did not start or its outcome is unknown (<see cref="EndPendingId"/>).</summary>
        public string? EndCode { get; }

        /// <summary>Request id of a battle end whose outcome is unknown: it stays in the pending store for the next start.</summary>
        public string? EndPendingId { get; }

        public string BuyCode { get; }

        public long Gold { get; }

        public long WalletRev { get; }
    }

    /// <summary>The SampleGame flow, shared by the scene client and the batch-mode end-to-end check.</summary>
    public static class SampleGameFlow
    {
        public static GameServerClient CreateClient(Uri serverUrl, string platform, Action<string> log)
        {
            var client = new GameServerClient(
                new GameServerClientOptions(serverUrl, Application.version, platform, Application.buildGUID)
                {
                    // A command whose answer was lost (app killed, network gone) stays on disk until the next start (FG-022).
                    PendingCommands = UnityPendingCommandStore.Create("local"),
                },
                new UnityWebRequestTransport(),
                config: UnityConfigCache.Create("local"));
            client.SessionReplaced += error => log($"Logged in on another device ({error.Code}). Not logging in again.");
            client.AccountBlocked += error => log($"Account blocked: {error.Code}");
            client.Maintenance += _ => log("Server maintenance.");
            client.UpdateRequired += _ => log("Please update the game.");
            client.ConfigVersionChanged += version => log($"New config {version} available.");
            client.Sections.Register(SampleGameSections.Table);
            return client;
        }

        public static (string LocalId, byte[] Secret) NewDevice()
        {
            var secret = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(secret);
            }

            return ("dev-" + Guid.NewGuid().ToString("N").Substring(0, 16), secret);
        }

        /// <summary>
        /// The guest device kept in PlayerPrefs under <c>{keyPrefix}.localId</c> and <c>{keyPrefix}.secret</c>, created on first use.
        /// The guest credential lives on the device only (IDN-01); a real game should keep it in secure storage.
        /// </summary>
        public static (string LocalId, byte[] Secret) StoredDevice(string keyPrefix)
        {
            var localIdKey = keyPrefix + ".localId";
            var secretKey = keyPrefix + ".secret";
            if (!PlayerPrefs.HasKey(localIdKey))
            {
                var (localId, secret) = NewDevice();
                PlayerPrefs.SetString(localIdKey, localId);
                PlayerPrefs.SetString(secretKey, Convert.ToBase64String(secret));
                PlayerPrefs.Save();
            }

            return (PlayerPrefs.GetString(localIdKey), Convert.FromBase64String(PlayerPrefs.GetString(secretKey)));
        }

        public static async Task<SampleGameRun> RunAsync(GameServerClient client, string localId, byte[] secret, Action<string> log)
        {
            var login = await client.LoginGuestAsync(localId, secret);
            log($"Player {login.PlayerId}, server time {client.ServerNow:O}");

            // The login answer holds every client-visible section with its rev: it is the boot read, no snapshot call (FG-013).
            log($"new player: {login.IsNewPlayer}");

            // The persistent cache (FG-019) holds the last client config: a start whose version is unchanged downloads nothing.
            if (client.ConfigOutdated)
            {
                if (await client.RefreshConfigAsync())
                {
                    log($"config downloaded {client.Config.Version}: {string.Join(", ", client.Config.Files.Keys)}");
                }
            }
            else if (client.Config.Version != null)
            {
                log($"config from cache {client.Config.Version}: {string.Join(", ", client.Config.Files.Keys)}");
            }

            await ResendPendingAsync(client, log);

            var start = await client.SendCommandAsync(SampleGameRoutes.BattleStart, new StartBattleRequest { Stage = 1 }, StartBattleResult.Parser);
            log($"StartBattle → {start.Code}");
            string? endCode = null;
            string? endPendingId = null;
            if (start.IsOk)
            {
                // A caller-owned id: a game could keep it in its own state before sending (FG-022).
                var endRequestId = GameServerClient.NewRequestId();
                try
                {
                    var end = await client.SendCommandAsync(
                        SampleGameRoutes.BattleEnd,
                        new EndBattleRequest { BattleId = start.Result!.BattleId, Won = true },
                        EndBattleResult.Parser,
                        endRequestId);
                    endCode = end.Code;
                    log($"EndBattle → {end.Code}");
                }
                catch (GameServerException) when (IsPending(client, endRequestId))
                {
                    // Unknown outcome (no answer after the retries): the store keeps it and the next start resends it.
                    endPendingId = endRequestId;
                    log($"EndBattle → pending {endRequestId}");
                }
            }

            var buy = await client.SendCommandAsync(SampleGameRoutes.ShopBuy, new BuyItemRequest { ShopItemId = "potion_pack", Quantity = 1 }, BuyItemResult.Parser);
            log($"Buy → {buy.Code}");

            var wallet = client.Sections.Get(FrameworkSections.Wallet);
            var gold = wallet != null && wallet.Balances.TryGetValue("gold", out var value) ? value : 0;
            log($"Gold: {gold} (rev {client.Sections.RevOf(FrameworkSections.WalletId)})");
            return new SampleGameRun(
                login.PlayerId, login.IsNewPlayer, start.Code, endCode, endPendingId, buy.Code, gold, client.Sections.RevOf(FrameworkSections.WalletId));
        }

        /// <summary>
        /// After login: resends every Resendable entry of the pending store with its stored id and bytes (a committed command answers
        /// Replayed with its original receipt) and discards the others (another player's, or expired).
        /// </summary>
        private static async Task ResendPendingAsync(GameServerClient client, Action<string> log)
        {
            // A copy: PendingCommands is the store's live list, and a resend or a discard removes the entry from it.
            foreach (var pending in new List<PendingCommand>(client.PendingCommands))
            {
                var status = client.StatusOf(pending);
                log($"pending {pending.RequestId} {status}");
                if (status != PendingCommandStatus.Resendable)
                {
                    client.DiscardPendingCommand(pending.RequestId);
                    continue;
                }

                bool replayed;
                switch (pending.Route)
                {
                    case SampleGameRoutes.BattleStart:
                        replayed = (await client.ResendPendingAsync(pending, StartBattleResult.Parser)).Replayed;
                        break;
                    case SampleGameRoutes.BattleEnd:
                        replayed = (await client.ResendPendingAsync(pending, EndBattleResult.Parser)).Replayed;
                        break;
                    case SampleGameRoutes.ShopBuy:
                        replayed = (await client.ResendPendingAsync(pending, BuyItemResult.Parser)).Replayed;
                        break;
                    default:
                        // A route this build no longer sends: nothing to parse its result with.
                        client.DiscardPendingCommand(pending.RequestId);
                        continue;
                }

                log($"resent {pending.RequestId}: replayed={replayed}");
            }
        }

        private static bool IsPending(GameServerClient client, string requestId)
        {
            foreach (var pending in client.PendingCommands)
            {
                if (pending.RequestId == requestId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
