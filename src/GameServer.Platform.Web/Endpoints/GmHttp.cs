using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Errors;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Protobuf;
using Microsoft.AspNetCore.Http;

namespace GameServer.Platform.Web.Endpoints;

/// <summary>JSON page of a GM list (GM-06).</summary>
public sealed record GmPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Helpers shared by GM JSON endpoints of all modules.</summary>
public static partial class GmHttp
{
    public const string JsonContentType = "application/json";

    /// <summary>
    /// Reads <c>pageSize</c> (mandatory, 1..200) and the opaque <c>cursor</c> from the query string (GM-06, ACC-24).
    /// </summary>
    public static (int PageSize, string? Cursor) ReadPageRequest(HttpContext context)
    {
        var raw = context.Request.Query["pageSize"].ToString();
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize) || pageSize is < 1 or > PagedQuery.MaxPageSize)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, $"pageSize is required and must be 1..{PagedQuery.MaxPageSize}.");
        }

        var cursor = context.Request.Query["cursor"].ToString();
        return (pageSize, cursor.Length == 0 ? null : cursor);
    }

    /// <summary>Runs a paged query for the request's page parameters and maps each document.</summary>
    public static async Task<GmPage<T>> QueryAsync<T>(
        HttpContext context,
        string collection,
        string filterField,
        string filterValue,
        string orderByField,
        bool descending,
        Func<StoredDocument, T> map)
    {
        var (pageSize, cursor) = ReadPageRequest(context);
        var store = context.RequestServices.GetService(typeof(IPagedQueryStore)) as IPagedQueryStore
            ?? throw new InvalidOperationException("No IPagedQueryStore is registered.");
        var page = await store.QueryPageAsync(
            new PagedQuery(collection, filterField, filterValue, orderByField, descending, pageSize, cursor),
            context.RequestAborted);
        return new GmPage<T>(page.Items.Select(map).ToArray(), page.NextCursor);
    }

    /// <summary>Reads a bounded JSON body (GM body limit, API-06).</summary>
    public static async Task<T> ReadJsonAsync<T>(HttpContext context)
    {
        var options = (GameServerOptions)context.RequestServices.GetService(typeof(GameServerOptions))!;
        var body = await BoundedBody.ReadAsync(context, options.Limits.GmBodyBytes, JsonContentType);
        try
        {
            return JsonSerializer.Deserialize<T>(body, GameServerJson.Options)
                ?? throw new GameServerException(CoreErrorCodes.BadRequest, "Body is required.");
        }
        catch (JsonException ex)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "Body is not valid JSON for this endpoint.", ex);
        }
    }

    public static IResult Json<T>(T value) => Results.Json(value, GameServerJson.Options);

    /// <summary>Validates a target <c>playerId</c> from a GM route (400 otherwise).</summary>
    public static string RequirePlayerId(string? playerId) =>
        playerId is not null && PlayerIdPattern().IsMatch(playerId)
            ? playerId
            : throw new GameServerException(CoreErrorCodes.BadRequest, "Invalid playerId.");

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex PlayerIdPattern();
}
