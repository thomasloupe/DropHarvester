using System.Text.Json;

namespace DropHarvester.Services.Twitch;

/// <summary>A single game/category match returned by Twitch's category search.</summary>
public sealed record GameMatch(string Id, string Name, string? Slug);

/// <summary>Autocomplete/validation for game names via Twitch's category search.</summary>
public interface IGameSearchService
{
    /// <summary>Searches Twitch categories for names matching the query text.</summary>
    /// <param name="query">Partial game/category name to search for.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>Matching categories, or an empty list when the query is too short or the call fails.</returns>
    Task<IReadOnlyList<GameMatch>> SearchAsync(string query, CancellationToken ct = default);
}

/// <summary>GraphQL-backed implementation of <see cref="IGameSearchService"/>.</summary>
public sealed class GameSearchService : IGameSearchService
{
    const string Query =
        "query($q:String!){searchCategories(query:$q,first:8){edges{node{id name slug}}}}";

    readonly IGqlClient _gql;

    /// <summary>Creates the service backed by the given Twitch GraphQL client.</summary>
    /// <param name="gql">GraphQL client used to run the category search query.</param>
    public GameSearchService(IGqlClient gql) => _gql = gql;

    /// <summary>Searches Twitch categories for names matching the query text.</summary>
    /// <param name="query">Partial game/category name; searches only run when at least two characters.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>Matching categories, or an empty list when the query is too short or the call fails.</returns>
    public async Task<IReadOnlyList<GameMatch>> SearchAsync(string query, CancellationToken ct = default)
    {
        query = query?.Trim() ?? "";
        if (query.Length < 2)
            return Array.Empty<GameMatch>();

        try
        {
            var root = await _gql.RawAsync(Query, new { q = query }, ct).ConfigureAwait(false);
            var edges = root.Path("data", "searchCategories")?.Prop("edges")?.AsArray()
                ?? Enumerable.Empty<JsonElement>();

            var matches = new List<GameMatch>();
            foreach (var e in edges)
            {
                var n = e.Prop("node");
                if (n is null) continue;
                var name = n.Value.Str("name");
                if (string.IsNullOrEmpty(name)) continue;
                matches.Add(new GameMatch(n.Value.Str("id") ?? "", name, n.Value.Str("slug")));
            }
            return matches;
        }
        catch
        {
            return Array.Empty<GameMatch>();
        }
    }
}
