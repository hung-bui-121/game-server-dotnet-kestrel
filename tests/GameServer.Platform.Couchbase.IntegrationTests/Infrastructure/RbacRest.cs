using System.Net.Http.Headers;
using System.Text;

namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>
/// Throw-away local RBAC users (<c>gs-it-*</c>) through the cluster manager REST API with the admin credential. Roles use the 7.6
/// syntax: <c>role[bucket]</c> or <c>role[bucket:scope]</c> (<c>[bucket:scope:*]</c> is refused). Passwords are never printed.
/// </summary>
internal static class RbacRest
{
    public const string UserPrefix = "gs-it-";

    public static string NewUserName() => UserPrefix + Guid.NewGuid().ToString("N")[..10];

    public static async Task CreateUserAsync(string name, string password, IEnumerable<string> roles)
    {
        using var client = AdminClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = password, ["roles"] = string.Join(',', roles) });
        var response = await client.PutAsync($"settings/rbac/users/local/{name}", form);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Creating the RBAC user {name} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
    }

    public static async Task DeleteUserAsync(string name)
    {
        using var client = AdminClient();
        await client.DeleteAsync($"settings/rbac/users/local/{name}");
    }

    private static HttpClient AdminClient()
    {
        var client = new HttpClient { BaseAddress = IntegrationCluster.ManagementUri };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{IntegrationCluster.AdminUser}:{IntegrationCluster.AdminPassword}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        return client;
    }
}
