using System.Security.Cryptography;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using Google.Protobuf;

namespace GameServer.Tests.Shared;

/// <summary>Guest login helpers for HTTP tests.</summary>
public static class IdentityHttp
{
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(32);

    public static string NewLocalId() => "dev-" + Guid.NewGuid().ToString("N")[..16];

    public static Task<HttpResponseMessage> PostLoginAsync(this HttpClient client, string localId, byte[] secret) =>
        client.PostProtobufAsync(IdentityModule.LoginRoute, new GuestLoginRequest { LocalId = localId, Secret = ByteString.CopyFrom(secret) });

    public static async Task<LoginResponse> LoginAsync(this HttpClient client, string localId, byte[] secret)
    {
        var response = await client.PostLoginAsync(localId, secret);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.ReadErrorAsync();
            throw new InvalidOperationException($"Login failed: {(int)response.StatusCode} {error.Code}");
        }

        return await response.ReadProtobufAsync<LoginResponse>();
    }
}
