using System.Text;

namespace DropHarvester.Models.Twitch;

/// <summary>A Twitch game/category.</summary>
public sealed class Game
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Slug { get; init; }

    /// <summary>The category slug (Twitch's own, or derived from the name) for its directory URL.</summary>
    public string DirectorySlug => string.IsNullOrEmpty(Slug) ? Slugify(Name) : Slug;

    /// <summary>The game's Twitch directory page (live channels) - a place to find a channel to subscribe to.</summary>
    public string DirectoryUrl => $"https://www.twitch.tv/directory/category/{DirectorySlug}";

    /// <summary>Lowercases a game name into a Twitch category slug: apostrophes dropped, other non-alphanumeric
    /// runs collapsed to single hyphens (e.g. "Assassin's Creed Black Flag" -> "assassins-creed-black-flag").</summary>
    /// <param name="name">The game name to slugify.</param>
    /// <returns>The url-safe category slug.</returns>
    static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        var pendingHyphen = false;
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is '\'' or '’') continue; // drop apostrophes rather than hyphenate them
            if (char.IsLetterOrDigit(ch)) { if (pendingHyphen && sb.Length > 0) sb.Append('-'); pendingHyphen = false; sb.Append(ch); }
            else pendingHyphen = true;
        }
        return sb.ToString();
    }

    /// <summary>Returns the game's name for display.</summary>
    public override string ToString() => Name;
}

/// <summary>A reward earned from a drop (badge, emote, or in-game item).</summary>
public sealed class Benefit
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ImageUrl { get; init; }

    /// <summary>distributionType from Twitch: BADGE / EMOTE / DIRECT_ENTITLEMENT / UNKNOWN. Verified live
    /// against Twitch: chat-badge rewards report "BADGE", emote rewards "EMOTE", and in-game items
    /// "DIRECT_ENTITLEMENT" - so it's the authoritative signal for what KIND of reward a drop grants.</summary>
    public string DistributionType { get; init; } = "UNKNOWN";

    /// <summary>A chat badge reward (Twitch grants it automatically, with no claim step).</summary>
    public bool IsBadge => DistributionType is "BADGE";

    /// <summary>An emote reward (Twitch grants it automatically, with no claim step).</summary>
    public bool IsEmote => DistributionType is "EMOTE";

    /// <summary>A badge or emote - both are auto-granted the instant the watch requirement is met, with no
    /// separate claim step and unreliable per-drop progress, so they're handled differently from items.</summary>
    public bool IsBadgeOrEmote => IsBadge || IsEmote;

    /// <summary>The id used to match a reward against the claim history. Matched DIRECTLY against
    /// gameEventDrops ids - the full benefit id (including any "_CUSTOM_ID_..." part) IS the reward
    /// identity; stripping it conflated unrelated rewards.</summary>
    public string MatchKey => Id;
}
