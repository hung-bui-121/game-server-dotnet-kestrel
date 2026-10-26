#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GameServer.Client;
using GameServer.Modules.Economy.Protocol.V1;
using SampleGame.Protocol.V1;
using UnityEngine;

namespace SampleGame.Client
{
    /// <summary>
    /// KestrelDemo: SampleGame (G7) driven through the SDK — guest login → config → battle start/end → shop. Point
    /// <see cref="serverUrl"/> at a running SampleHost (<c>dotnet run --project samples/SampleHost</c>). The SDK owns transport,
    /// retries and resync; this script only calls commands, reads sections and shows what happened on screen.
    /// </summary>
    public sealed class SampleGameClient : MonoBehaviour
    {
        private const string LocalIdKey = "gs.localId";
        private const string SecretKey = "gs.secret";

        [SerializeField]
        private string serverUrl = "http://localhost:5000/";

        private readonly List<string> _lines = new List<string>();

        private async void Start()
        {
            var platform = Application.platform == RuntimePlatform.Android ? "android"
                : Application.platform == RuntimePlatform.IPhonePlayer ? "ios"
                : "editor";
            var client = SampleGameFlow.CreateClient(new Uri(serverUrl), platform, Log);
            var (localId, secret) = Device();
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

        /// <summary>The guest credential lives on the device only (IDN-01); a real game should keep it in secure storage.</summary>
        private static (string LocalId, byte[] Secret) Device()
        {
            if (!PlayerPrefs.HasKey(LocalIdKey))
            {
                var (localId, secret) = SampleGameFlow.NewDevice();
                PlayerPrefs.SetString(LocalIdKey, localId);
                PlayerPrefs.SetString(SecretKey, Convert.ToBase64String(secret));
                PlayerPrefs.Save();
            }

            return (PlayerPrefs.GetString(LocalIdKey), Convert.FromBase64String(PlayerPrefs.GetString(SecretKey)));
        }
    }

    /// <summary>Outcome of one run of the sample flow.</summary>
    public sealed class SampleGameRun
    {
        public SampleGameRun(string playerId, string startCode, string? endCode, string buyCode, long gold, long walletRev)
        {
            PlayerId = playerId;
            StartCode = startCode;
            EndCode = endCode;
            BuyCode = buyCode;
            Gold = gold;
            WalletRev = walletRev;
        }

        public string PlayerId { get; }

        public string StartCode { get; }

        public string? EndCode { get; }

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
                new GameServerClientOptions(serverUrl, Application.version, platform, Application.buildGUID),
                new UnityWebRequestTransport());
            client.SessionReplaced += error => log($"Logged in on another device ({error.Code}). Not logging in again.");
            client.AccountBlocked += error => log($"Account blocked: {error.Code}");
            client.Maintenance += _ => log("Server maintenance.");
            client.UpdateRequired += _ => log("Please update the game.");
            client.ConfigVersionChanged += version => log($"New config {version} available.");
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

        public static async Task<SampleGameRun> RunAsync(GameServerClient client, string localId, byte[] secret, Action<string> log)
        {
            var login = await client.LoginGuestAsync(localId, secret);
            log($"Player {login.PlayerId}, server time {client.ServerNow:O}");

            if (client.ConfigOutdated && await client.RefreshConfigAsync())
            {
                log($"Config {client.Config.Version}: {string.Join(", ", client.Config.Files.Keys)}");
            }

            var start = await client.SendCommandAsync(SampleGameRoutes.StartBattle, new StartBattleRequest { Stage = 1 }, StartBattleResult.Parser);
            log($"StartBattle → {start.Code}");
            string? endCode = null;
            if (start.IsOk)
            {
                var end = await client.SendCommandAsync(
                    SampleGameRoutes.EndBattle,
                    new EndBattleRequest { BattleId = start.Result!.BattleId, Won = true },
                    EndBattleResult.Parser);
                endCode = end.Code;
                log($"EndBattle → {end.Code}");
            }

            var buy = await client.SendCommandAsync(SampleGameRoutes.Buy, new BuyItemRequest { ShopItemId = "potion_pack", Quantity = 1 }, BuyItemResult.Parser);
            log($"Buy → {buy.Code}");

            var wallet = client.Sections.Get("wallet", WalletState.Parser);
            var gold = wallet != null && wallet.Balances.TryGetValue("gold", out var value) ? value : 0;
            log($"Gold: {gold} (rev {client.Sections.RevOf("wallet")})");
            return new SampleGameRun(login.PlayerId, start.Code, endCode, buy.Code, gold, client.Sections.RevOf("wallet"));
        }
    }

    public static class SampleGameRoutes
    {
        public const string StartBattle = "api/v1/battle/start";
        public const string EndBattle = "api/v1/battle/end";
        public const string Buy = "api/v1/shop/buy";
    }
}
