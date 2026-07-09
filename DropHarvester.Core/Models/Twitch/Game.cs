namespace DropHarvester.Models.Twitch;

/// <summary>A Twitch game/category.</summary>
public sealed class Game
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Slug { get; init; }

    /// <summary>Returns the game's name for display.</summary>
    public override string ToString() => Name;
}

/// <summary>A reward earned from a drop (badge, emote, or in-game item).</summary>
public sealed class Benefit
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ImageUrl { get; init; }

    /// <summary>distributionType from Twitch: BADGE / EMOTE / DIRECT_ENTITLEMENT / UNKNOWN.</summary>
    public string DistributionType { get; init; } = "UNKNOWN";

    public bool IsBadgeOrEmote =>
        DistributionType is "BADGE" or "EMOTE";

    /// <summary>The id used to match a reward against the claim history. Matched DIRECTLY against
    /// gameEventDrops ids - the full benefit id (including any "_CUSTOM_ID_..." part) IS the reward
    /// identity; stripping it conflated unrelated rewards.</summary>
    public string MatchKey => Id;
}
