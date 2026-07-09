using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>Claims the periodic channel-points "bonus chest" on the watched channel.</summary>
public interface IChannelPointsService
{
    /// <summary>If a bonus claim is available on the channel, claim it. Returns points earned, else null.</summary>
    Task<int?> TryClaimAsync(string channelLogin, CancellationToken ct = default);
}

/// <summary>GraphQL-backed implementation of <see cref="IChannelPointsService"/>.</summary>
public sealed class ChannelPointsService : IChannelPointsService
{
    readonly IGqlClient _gql;

    /// <summary>Creates the service backed by the given Twitch GraphQL client.</summary>
    /// <param name="gql">GraphQL client used for the context query and claim mutation.</param>
    public ChannelPointsService(IGqlClient gql) => _gql = gql;

    /// <summary>Claims the channel's available bonus chest if one is currently offered.</summary>
    /// <param name="channelLogin">Login name of the channel to inspect for an available claim.</param>
    /// <param name="ct">Token to cancel the GraphQL calls.</param>
    /// <returns>Points earned from the claim, or null when nothing was available or the claim failed.</returns>
    public async Task<int?> TryClaimAsync(string channelLogin, CancellationToken ct = default)
    {
        try
        {
            var ctx = await _gql.PersistedAsync(
                "ChannelPointsContext", TwitchConstants.Gql.ChannelPointsContext,
                new { channelLogin }, ct).ConfigureAwait(false);

            var community = ctx.Path("data", "community");
            var channelId = community?.Str("id");
            var claimId = community?.Path("channel", "self", "communityPoints", "availableClaim")?.Str("id");
            if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(claimId))
                return null;

            var res = await _gql.PersistedAsync(
                "ClaimCommunityPoints", TwitchConstants.Gql.ClaimCommunityPoints,
                new { input = new { channelID = channelId, claimID = claimId } }, ct).ConfigureAwait(false);

            var claim = res.Path("data", "claimCommunityPoints");
            if (claim is null || claim.Value.Prop("error") is { ValueKind: System.Text.Json.JsonValueKind.Object })
                return null;

            return claim.Value.Prop("claim")?.IntOr("pointsEarnedTotal") ?? 0;
        }
        catch (GqlAuthException) { throw; }
        catch { return null; }
    }
}
