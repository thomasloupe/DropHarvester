using System.Text.Json;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>Lightweight campaign row from the dashboard (no drops yet).</summary>
public sealed record CampaignSummary(
    string Id, string Name, Game Game, CampaignStatus Status,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Linked);

/// <summary>
/// Discovers drop campaigns and their drops, syncs watch progress from the inventory, claims
/// earned drops, and polls the active drop session. All GQL runs through the Android client id
/// (see GqlClient) to pass Twitch's integrity gate.
/// </summary>
public interface IInventoryService
{
    /// <summary>Cheap: all campaigns from the dashboard (id, game, status, link) with no drops.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<CampaignSummary>> FetchDashboardAsync(CancellationToken ct = default);

    /// <summary>Full campaign with its drops and (merged) progress.</summary>
    /// <param name="campaignId">Twitch campaign id to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DropsCampaign?> FetchCampaignDetailsAsync(string campaignId, CancellationToken ct = default);

    /// <summary>Active campaigns worth harvesting (with drops + synced progress): linked ones, plus
    /// unlinked ones whose game is in <paramref name="alsoUnlinkedGames"/> (opt-in).</summary>
    /// <param name="includeBadgesEmotes">Whether badge/emote reward campaigns are included.</param>
    /// <param name="alsoUnlinkedGames">Game names whose unlinked campaigns to also include.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DropsCampaign>> FetchCampaignsAsync(bool includeBadgesEmotes, IReadOnlyCollection<string> alsoUnlinkedGames, CancellationToken ct = default);

    /// <summary>Everything for the Inventory tab: all campaigns; drops fetched for linked ones.</summary>
    /// <param name="progress">Reports (done, total) as campaign details are fetched.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DropsCampaign>> FetchInventoryAsync(IProgress<(int done, int total)>? progress, CancellationToken ct = default);

    /// <summary>Merges Twitch inventory progress into the given campaigns' drops.</summary>
    /// <param name="campaigns">Campaigns whose drops receive synced progress.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SyncInventoryAsync(IEnumerable<DropsCampaign> campaigns, CancellationToken ct = default);

    /// <summary>Claims a drop's reward via its claim id.</summary>
    /// <param name="drop">Drop to claim (must have a claim id).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the drop is considered claimed.</returns>
    Task<bool> ClaimDropAsync(TimedDrop drop, CancellationToken ct = default);

    /// <summary>Reads the current drop session (active drop id and minutes watched) for a channel.</summary>
    /// <param name="channelId">Channel id to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The active drop id and minutes watched, or (null, 0) if none.</returns>
    Task<(string? dropId, int currentMinutes)> FetchCurrentSessionAsync(string channelId, CancellationToken ct = default);

    /// <summary>The drop-campaign ids Twitch reports as ACTIVE on this channel's current stream right now
    /// (its <c>viewerDropCampaigns</c>) - i.e. the campaigns that would actually credit by watching it. An
    /// empty set means the channel has NO drop active (so an allow-listed channel not really airing the
    /// drop can be skipped); NULL means the check couldn't be made (never skip a channel on a failed check).</summary>
    /// <param name="channelId">Channel id to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Active campaign ids on the channel, or null if the query failed.</returns>
    Task<IReadOnlyCollection<string>?> FetchChannelDropCampaignIdsAsync(string channelId, CancellationToken ct = default);

    /// <summary>Benefits the user has already been awarded (Twitch's ~6-month claimed-drop history,
    /// from the inventory's gameEventDrops), mapped to when each was last awarded (null if unknown).
    /// Used to skip drops already claimed in the current campaign and, opt-in, de-dupe owned rewards.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<Dictionary<string, DateTimeOffset?>> GetClaimedBenefitsAsync(CancellationToken ct = default);

    /// <summary>Raw JSON of the claim history (gameEventDrops), for the debug server.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GetClaimHistoryRawJsonAsync(CancellationToken ct = default);
}

public sealed class InventoryService : IInventoryService
{
    const int DetailConcurrency = 6;

    readonly IGqlClient _gql;
    readonly ITwitchAuth _auth;

    /// <summary>Creates the service over a GQL client and Twitch auth.</summary>
    /// <param name="gql">Client used to run persisted GQL operations.</param>
    /// <param name="auth">Twitch auth state (supplies the current user id).</param>
    public InventoryService(IGqlClient gql, ITwitchAuth auth)
    {
        _gql = gql;
        _auth = auth;
    }

    /// <summary>Fetches every dashboard campaign as a lightweight summary, de-duped by id.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<CampaignSummary>> FetchDashboardAsync(CancellationToken ct = default)
    {
        var dashboard = await _gql.PersistedAsync(
            "ViewerDropsDashboard", TwitchConstants.Gql.ViewerDropsDashboard, new { fetchRewardCampaigns = true }, ct)
            .ConfigureAwait(false);

        var rows = dashboard.Path("data", "currentUser", "dropCampaigns")?.AsArray()
            ?? Enumerable.Empty<JsonElement>();

        var list = new List<CampaignSummary>();
        // the dashboard can list a campaign twice
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in rows)
        {
            var id = c.Str("id");
            var game = ParseGame(c.Prop("game"));
            if (string.IsNullOrEmpty(id) || game is null || !seen.Add(id))
                continue;

            list.Add(new CampaignSummary(
                id,
                c.Str("name") ?? "Campaign",
                game,
                ParseStatus(c.Str("status")),
                c.Date("startAt") ?? DateTimeOffset.UtcNow,
                c.Date("endAt") ?? DateTimeOffset.UtcNow,
                c.Path("self")?.BoolOr("isAccountConnected") ?? false));
        }
        return list;
    }

    /// <summary>Fetches, with synced progress, the active campaigns worth harvesting: linked ones plus opted-in unlinked games.</summary>
    /// <param name="includeBadgesEmotes">Whether badge/emote reward campaigns are included.</param>
    /// <param name="alsoUnlinkedGames">Game names whose unlinked campaigns to also include.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<DropsCampaign>> FetchCampaignsAsync(bool includeBadgesEmotes, IReadOnlyCollection<string> alsoUnlinkedGames, CancellationToken ct = default)
    {
        var summaries = await FetchDashboardAsync(ct).ConfigureAwait(false);
        var unlinked = new HashSet<string>(alsoUnlinkedGames, StringComparer.OrdinalIgnoreCase);

        // fetching details for every unlinked campaign would be huge, so only opted-in games are included
        var targets = summaries
            .Where(s => s.Status == CampaignStatus.Active
                        && (s.Linked || unlinked.Contains(s.Game.Name)))
            .ToList();

        var campaigns = await FetchDetailsBoundedAsync(targets.Select(t => t.Id), null, ct).ConfigureAwait(false);
        await SyncInventoryAsync(campaigns, ct).ConfigureAwait(false);
        return campaigns;
    }

    /// <summary>Fetches full details for every dashboard campaign (falling back to summaries) and syncs progress, for the Inventory tab.</summary>
    /// <param name="progress">Reports (done, total) as campaign details are fetched.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<DropsCampaign>> FetchInventoryAsync(IProgress<(int done, int total)>? progress, CancellationToken ct = default)
    {
        var summaries = await FetchDashboardAsync(ct).ConfigureAwait(false);

        // fetch details for every campaign so Inventory cards show icons/drops regardless of the active filter
        var detailIds = summaries.Select(s => s.Id).ToHashSet();
        var detailed = await FetchDetailsBoundedAsync(detailIds, progress, ct).ConfigureAwait(false);
        var byId = detailed.ToDictionary(c => c.Id);

        var result = new List<DropsCampaign>(summaries.Count);
        foreach (var s in summaries)
        {
            if (byId.TryGetValue(s.Id, out var full))
                result.Add(full);
            else
                result.Add(FromSummary(s));
        }

        await SyncInventoryAsync(result, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Fetches campaign details for each id with bounded concurrency, skipping ones that fail and reporting progress.</summary>
    /// <param name="ids">Campaign ids to fetch (de-duped).</param>
    /// <param name="progress">Reports (done, total) as each id completes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The successfully-fetched campaigns.</returns>
    async Task<List<DropsCampaign>> FetchDetailsBoundedAsync(IEnumerable<string> ids, IProgress<(int, int)>? progress, CancellationToken ct)
    {
        var idList = ids.Distinct().ToList();
        var results = new List<DropsCampaign>();
        var gate = new SemaphoreSlim(DetailConcurrency);
        var done = 0;

        var tasks = idList.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var camp = await FetchCampaignDetailsAsync(id, ct).ConfigureAwait(false);
                if (camp is not null)
                    lock (results) results.Add(camp);
            }
            catch (GqlAuthException) { throw; }
            catch { /* skip a campaign whose details fail */ }
            finally
            {
                gate.Release();
                progress?.Report((Interlocked.Increment(ref done), idList.Count));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>Fetches one campaign's full details (game, allowed channels, drops, link state); null if missing or invalid.</summary>
    /// <param name="campaignId">Twitch campaign id to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DropsCampaign?> FetchCampaignDetailsAsync(string campaignId, CancellationToken ct = default)
    {
        var userId = _auth.State.UserId ?? "";
        var root = await _gql.PersistedAsync(
            "DropCampaignDetails", TwitchConstants.Gql.DropCampaignDetails,
            new { channelLogin = userId, dropID = campaignId }, ct).ConfigureAwait(false);

        var c = root.Path("data", "user", "dropCampaign");
        if (c is null)
            return null;
        var camp = c.Value;

        var game = ParseGame(camp.Prop("game"));
        if (game is null)
            return null;

        // allow.channels is null when the campaign accepts any drops-enabled channel.
        var allowed = (camp.Path("allow", "channels")?.AsArray() ?? Enumerable.Empty<JsonElement>())
            .Select(ch => ch.Str("name") ?? ch.Str("displayName") ?? "")
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        var drops = new List<TimedDrop>();
        foreach (var d in camp.Items("timeBasedDrops"))
        {
            var drop = ParseDrop(d);
            if (drop is not null)
                drops.Add(drop);
        }

        var campaign = new DropsCampaign
        {
            Id = camp.Str("id") ?? campaignId,
            Name = camp.Str("name") ?? "Campaign",
            Game = game,
            StartsAt = camp.Date("startAt") ?? DateTimeOffset.UtcNow,
            EndsAt = camp.Date("endAt") ?? DateTimeOffset.UtcNow,
            ImageUrl = FixImageDims(camp.Str("imageURL")),
            LinkUrl = camp.Str("accountLinkURL"),
            DetailsUrl = camp.Str("detailsURL"),
            AllowedChannels = allowed,
            Drops = drops,
            Linked = camp.Path("self")?.BoolOr("isAccountConnected") ?? false,
        };

        foreach (var drop in drops)
            drop.Campaign = campaign;

        return campaign;
    }

    /// <summary>Merges Twitch inventory progress (minutes watched, claimed state, claim id) into the given campaigns' drops, applied on the UI thread.</summary>
    /// <param name="campaigns">Campaigns whose drops receive synced progress.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SyncInventoryAsync(IEnumerable<DropsCampaign> campaigns, CancellationToken ct = default)
    {
        var byDropId = campaigns.SelectMany(c => c.Drops).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        if (byDropId.Count == 0)
            return;

        JsonElement root;
        try
        {
            root = await _gql.PersistedAsync("Inventory", TwitchConstants.Gql.Inventory, new { fetchRewardCampaigns = true }, ct).ConfigureAwait(false);
        }
        catch (GqlAuthException) { throw; }
        catch { return; }

        var inProgress = root.Path("data", "currentUser", "inventory", "dropCampaignsInProgress")?.AsArray()
            ?? Enumerable.Empty<JsonElement>();

        // parse off the UI thread, but apply updates on it: these drops are UI-bound, so mutating them off-thread trips WinUI's cross-thread failfast (combase/CoreMessagingXP)
        var updates = new List<(TimedDrop drop, int minutes, bool claimed, string? claimId, int currentSubs)>();
        foreach (var camp in inProgress)
        {
            foreach (var d in camp.Items("timeBasedDrops"))
            {
                var dropId = d.Str("id");
                if (dropId is null || !byDropId.TryGetValue(dropId, out var drop))
                    continue;
                var self = d.Prop("self");
                if (self is null)
                    continue;
                updates.Add((
                    drop,
                    self.Value.IntOr("currentMinutesWatched"),
                    self.Value.BoolOr("isClaimed"),
                    self.Value.Str("dropInstanceID"),
                    self.Value.IntOr("currentSubs")));
            }
        }
        if (updates.Count == 0)
            return;

        await UiDispatch.Current.InvokeAsync(() =>
        {
            foreach (var (drop, minutes, claimed, claimId, currentSubs) in updates)
            {
                drop.RealCurrentMinutes = minutes;
                drop.ExtraCurrentMinutes = 0;
                drop.IsClaimed = claimed;
                drop.CurrentSubs = currentSubs;
                if (!string.IsNullOrEmpty(claimId))
                    drop.ClaimId = claimId;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Maps each already-awarded benefit id to when it was last awarded (null if unknown), from the inventory claim history.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Dictionary<string, DateTimeOffset?>> GetClaimedBenefitsAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        JsonElement root;
        try
        {
            root = await _gql.PersistedAsync("Inventory", TwitchConstants.Gql.Inventory, new { fetchRewardCampaigns = true }, ct).ConfigureAwait(false);
        }
        catch (GqlAuthException) { throw; }
        catch { return map; }

        var awarded = root.Path("data", "currentUser", "inventory", "gameEventDrops")?.AsArray()
            ?? Enumerable.Empty<JsonElement>();
        foreach (var b in awarded)
        {
            var id = b.Str("id");
            if (string.IsNullOrEmpty(id))
                continue;
            // match on the benefit id directly (no id munging);
            // lastAwardedAt distinguishes "claimed this campaign" from "owned from a past run"
            map[id!] = b.Date("lastAwardedAt");
        }
        return map;
    }

    /// <summary>Raw dump of the inventory's <c>gameEventDrops</c> (the claim history) for the debug
    /// server - the exact objects Twitch returns, so their id/name format can be inspected.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> GetClaimHistoryRawJsonAsync(CancellationToken ct = default)
    {
        JsonElement root;
        try
        {
            root = await _gql.PersistedAsync("Inventory", TwitchConstants.Gql.Inventory, new { fetchRewardCampaigns = true }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { return $"{{ \"error\": \"{ex.Message}\" }}"; }

        var awarded = root.Path("data", "currentUser", "inventory", "gameEventDrops");
        return awarded is { } a
            ? JsonSerializer.Serialize(a, new JsonSerializerOptions { WriteIndented = true })
            : "[]";
    }

    /// <summary>Claims the drop via its claim id, marking it claimed on success.</summary>
    /// <param name="drop">Drop to claim (must have a claim id).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when claimed; false if the drop has no claim id or Twitch reports an error.</returns>
    public async Task<bool> ClaimDropAsync(TimedDrop drop, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(drop.ClaimId))
            return false;

        var root = await _gql.PersistedAsync(
            "DropsPage_ClaimDropRewards", TwitchConstants.Gql.DropsPageClaimDropRewards,
            new { input = new { dropInstanceID = drop.ClaimId } }, ct).ConfigureAwait(false);

        var claim = root.Path("data", "claimDropRewards");
        if (claim is not null)
        {
            var status = claim.Value.Str("status");
            if (status is null or "ELIGIBLE_FOR_ALL" or "DROP_INSTANCE_ALREADY_CLAIMED" or "CLAIMED")
            {
                drop.IsClaimed = true;
                return true;
            }
        }

        var hasErrors = root.Prop("errors") is not null;
        if (!hasErrors)
        {
            drop.IsClaimed = true;
            return true;
        }
        return false;
    }

    /// <summary>Returns the drop id and minutes watched of the current drop session on a channel, or (null, 0) if none.</summary>
    /// <param name="channelId">Channel id to query.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<(string? dropId, int currentMinutes)> FetchCurrentSessionAsync(string channelId, CancellationToken ct = default)
    {
        try
        {
            var root = await _gql.PersistedAsync(
                "DropCurrentSessionContext", TwitchConstants.Gql.DropCurrentSessionContext,
                new { channelID = channelId, channelLogin = "" }, ct).ConfigureAwait(false);

            var session = root.Path("data", "currentUser", "dropCurrentSession");
            if (session is null)
                return (null, 0);
            return (session.Value.Str("dropID"), session.Value.IntOr("currentMinutesWatched"));
        }
        catch (GqlAuthException) { throw; }
        catch { return (null, 0); }
    }

    /// <summary>Queries the drop campaigns Twitch reports active on a channel's current stream.</summary>
    /// <param name="channelId">Channel id to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The active campaign ids, or null when the query fails.</returns>
    public async Task<IReadOnlyCollection<string>?> FetchChannelDropCampaignIdsAsync(string channelId, CancellationToken ct = default)
    {
        try
        {
            var root = await _gql.PersistedAsync(
                "DropsHighlightService_AvailableDrops", TwitchConstants.Gql.DropsHighlightServiceAvailableDrops,
                new { channelID = channelId }, ct).ConfigureAwait(false);

            var camps = root.Path("data", "channel")?.Prop("viewerDropCampaigns");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (camps?.ValueKind == JsonValueKind.Array)
                foreach (var c in camps.Value.AsArray())
                {
                    var id = c.Str("id");
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id!);
                }
            return ids; // empty = channel has no drop active right now (not a failure)
        }
        catch (GqlAuthException) { throw; }
        catch { return null; } // couldn't check - caller must NOT skip the channel on a null
    }

    /// <summary>Builds a drops-less DropsCampaign from a lightweight summary.</summary>
    /// <param name="s">Summary row to convert.</param>
    static DropsCampaign FromSummary(CampaignSummary s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Game = s.Game,
        StartsAt = s.StartsAt,
        EndsAt = s.EndsAt,
        Drops = Array.Empty<TimedDrop>(),
        Linked = s.Linked,
    };

    /// <summary>Maps a Twitch status string to a CampaignStatus (defaults to Upcoming).</summary>
    /// <param name="status">Raw Twitch status string.</param>
    static CampaignStatus ParseStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "ACTIVE" => CampaignStatus.Active,
        "EXPIRED" => CampaignStatus.Expired,
        _ => CampaignStatus.Upcoming,
    };

    /// <summary>Parses a Game from a game JSON node; null if the id or name is missing.</summary>
    /// <param name="gel">Game JSON node, or null.</param>
    static Game? ParseGame(JsonElement? gel)
    {
        if (gel is not { } g)
            return null;
        var id = g.Str("id");
        var name = g.Str("displayName") ?? g.Str("name");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            return null;
        return new Game { Id = id, Name = name, Slug = g.Str("slug") };
    }

    /// <summary>Parses a TimedDrop (benefits, preconditions, and self progress) from a drop JSON node; null if it has no id.</summary>
    /// <param name="d">Drop JSON node.</param>
    static TimedDrop? ParseDrop(JsonElement d)
    {
        var id = d.Str("id");
        if (string.IsNullOrEmpty(id))
            return null;

        var benefits = new List<Benefit>();
        foreach (var edge in d.Items("benefitEdges"))
        {
            var b = edge.Prop("benefit");
            if (b is null) continue;
            var bid = b.Value.Str("id");
            var bname = b.Value.Str("name");
            if (string.IsNullOrEmpty(bid) || string.IsNullOrEmpty(bname)) continue;
            benefits.Add(new Benefit
            {
                Id = bid,
                Name = bname,
                ImageUrl = b.Value.Str("imageAssetURL"),
                DistributionType = b.Value.Str("distributionType") ?? "UNKNOWN",
            });
        }

        var preconditions = d.Items("preconditionDrops")
            .Select(p => p.Str("id"))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();

        var self = d.Prop("self");
        var drop = new TimedDrop
        {
            Id = id,
            Name = d.Str("name") ?? "Drop",
            RequiredMinutes = d.IntOr("requiredMinutesWatched"),
            RequiredSubs = d.IntOr("requiredSubs"),
            StartsAt = d.Date("startAt"),
            EndsAt = d.Date("endAt"),
            Benefits = benefits,
            PreconditionDropIds = preconditions,
        };
        if (self is not null)
        {
            drop.RealCurrentMinutes = self.Value.IntOr("currentMinutesWatched");
            drop.IsClaimed = self.Value.BoolOr("isClaimed");
            drop.CurrentSubs = self.Value.IntOr("currentSubs");
            var claim = self.Value.Str("dropInstanceID");
            if (!string.IsNullOrEmpty(claim))
                drop.ClaimId = claim;
        }
        return drop;
    }

    /// <summary>Substitutes concrete width/height into a Twitch image URL template.</summary>
    /// <param name="url">Image URL template containing {width}/{height} placeholders, or null.</param>
    static string? FixImageDims(string? url)
        => url?.Replace("{width}", "285").Replace("{height}", "380");
}
