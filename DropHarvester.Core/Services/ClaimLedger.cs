namespace DropHarvester.Services;

/// <summary>
/// A persisted local record of claimed drop rewards (reward id -> when it was claimed), kept for ~6
/// months in the data folder (<c>claimed-drops.json</c>). It's OUR own source of truth: once we claim a
/// drop - or Twitch's inventory confirms one - we remember it forever, so "claimed" survives Twitch's
/// lagging per-drop <c>self.isClaimed</c> flag and its incomplete/ambiguous <c>gameEventDrops</c> history.
/// Merged into the claim map that drives the Finished filter and the harvesting skip logic. Core-level, so
/// the desktop app and the headless daemon both keep the ledger on their data volume.
/// </summary>
public interface IClaimLedger
{
    /// <summary>Record that a reward was claimed at <paramref name="at"/> (keeps the earliest time seen).</summary>
    void Record(string rewardId, DateTimeOffset at);

    /// <summary>Fold Twitch's claim history into the ledger so it accumulates permanently, even after a
    /// later inventory read no longer reports those rewards.</summary>
    void RecordAll(IEnumerable<KeyValuePair<string, DateTimeOffset?>> entries);

    /// <summary>Reward id -> claimed time. A snapshot copy, safe to merge into a working claim map.</summary>
    IReadOnlyDictionary<string, DateTimeOffset> All { get; }

    /// <summary>Record that a specific DROP TIER was claimed, keyed by its stable drop-definition id.
    /// This is distinct from <see cref="Record"/> (reward id): a single campaign often lists the SAME
    /// reward on several time tiers (Marbles' 15-coin drops at 2h/4h/8h/10h; R6S Esports packs), so a
    /// reward id can't say WHICH tier is done. A per-DROP id can - and being per-tier it never
    /// over-attributes one claim to a sibling tier. Immune to Twitch's per-drop self lagging back to 0.</summary>
    void RecordDrop(string dropId, DateTimeOffset at);

    /// <summary>Drop-definition id -> when we recorded it claimed. A snapshot copy.</summary>
    IReadOnlyDictionary<string, DateTimeOffset> Drops { get; }
}

/// <summary>JSON-file-backed implementation of <see cref="IClaimLedger"/>.</summary>
public sealed class ClaimLedger : IClaimLedger
{
    const string FileName = "claimed-drops.json";
    static readonly TimeSpan Retention = TimeSpan.FromDays(183); // ~6 months

    readonly object _lock = new();
    readonly Dictionary<string, DateTimeOffset> _claims;
    readonly Dictionary<string, DateTimeOffset> _drops;

    /// <summary>Loads the ledger, drops entries past the retention window, and re-saves if any were pruned.</summary>
    public ClaimLedger()
    {
        var data = JsonStore.Load<ClaimLedgerData>(FileName);
        var cutoff = DateTimeOffset.UtcNow - Retention;
        _claims = data.Claims
            .Where(kv => kv.Value >= cutoff)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        _drops = data.Drops
            .Where(kv => kv.Value >= cutoff)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        if (_claims.Count != data.Claims.Count || _drops.Count != data.Drops.Count)
            Save(); // dropped entries older than the retention window
    }

    public IReadOnlyDictionary<string, DateTimeOffset> All
    {
        get { lock (_lock) return new Dictionary<string, DateTimeOffset>(_claims, StringComparer.OrdinalIgnoreCase); }
    }

    public IReadOnlyDictionary<string, DateTimeOffset> Drops
    {
        get { lock (_lock) return new Dictionary<string, DateTimeOffset>(_drops, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Records a claimed drop tier by its drop-definition id, keeping the earliest time seen.</summary>
    /// <param name="dropId">Stable drop-definition id of the claimed tier; blank ids are ignored.</param>
    /// <param name="at">Time the drop was claimed.</param>
    public void RecordDrop(string dropId, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(dropId))
            return;
        lock (_lock)
        {
            if (_drops.TryGetValue(dropId, out var existing) && existing <= at)
                return; // already have an earlier/equal claim time
            _drops[dropId] = at;
            Save();
        }
    }

    /// <summary>Records a claimed reward by its reward id, keeping the earliest time seen.</summary>
    /// <param name="rewardId">Reward id that was claimed; blank ids are ignored.</param>
    /// <param name="at">Time the reward was claimed.</param>
    public void Record(string rewardId, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(rewardId))
            return;
        lock (_lock)
        {
            if (_claims.TryGetValue(rewardId, out var existing) && existing <= at)
                return; // already have an earlier/equal claim time
            _claims[rewardId] = at;
            Save();
        }
    }

    /// <summary>Folds many reward-id claim times into the ledger, keeping the earliest for each and saving once.</summary>
    /// <param name="entries">Reward id to claim-time pairs; entries with a blank id or null time are skipped.</param>
    public void RecordAll(IEnumerable<KeyValuePair<string, DateTimeOffset?>> entries)
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var kv in entries)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value is not { } t)
                    continue;
                if (_claims.TryGetValue(kv.Key, out var existing) && existing <= t)
                    continue;
                _claims[kv.Key] = t;
                changed = true;
            }
            if (changed)
                Save();
        }
    }

    /// <summary>Persists the current claim and drop maps to disk.</summary>
    void Save() // caller holds _lock
        => JsonStore.Save(FileName, new ClaimLedgerData { Claims = new(_claims), Drops = new(_drops) });
}

/// <summary>On-disk shape of the claim ledger.</summary>
public sealed class ClaimLedgerData
{
    /// <summary>Reward id -> claimed time.</summary>
    public Dictionary<string, DateTimeOffset> Claims { get; set; } = new();

    /// <summary>Drop-definition id -> claimed time (per-tier, immune to shared-reward ambiguity).</summary>
    public Dictionary<string, DateTimeOffset> Drops { get; set; } = new();
}
