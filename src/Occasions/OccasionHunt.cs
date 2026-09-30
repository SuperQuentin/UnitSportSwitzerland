using System.Text.Json;
using Godot;
using UnitSport.Items;

namespace UnitSport.Occasions;

/// <summary>
/// The treat / gift hunt: spots an occasion leaves by doors and under town trees, each claimable
/// <b>once per player per occasion instance</b> (<c>halloween-2026</c>). Client only.
///
/// <para>
/// <b>Local, like the inventory and <see cref="Loot.Gathering"/>.</b> A per-player claim is not
/// contested by anyone — two players can each take the treat at the same door — and what it
/// grants lands in an inventory that is local anyway, so a server round-trip would add latency
/// and no trust. Claims are saved to <c>user://occasions/claims.json</c> by instance, so next
/// year's Halloween starts a fresh hunt and last year's claims stay as a record.
/// </para>
///
/// <para>
/// Taking a spot is a <see cref="Loot.Gathering"/> hold (G / pad X) rather than E, because the
/// spots stand beside front doors where E already means "go in".
/// </para>
/// </summary>
public partial class OccasionHunt : Node
{
    private const string File = "user://occasions/claims.json";
    private const float Reach = 1.4f;

    public static OccasionHunt? Instance { get; private set; }

    private Dictionary<string, HashSet<string>> _claims = new();
    private readonly Dictionary<string, (Vector3 World, string Label)> _seen = new();
    private readonly Random _rng = new();

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        Name = "OccasionHunt";
        _probe = OS.GetCmdlineUserArgs().Contains("--huntcheck");
        // a probe must leave the player's real claims alone: it starts from none and never saves
        if (!_probe) Load();
        if (OccasionDecor.Instance is { } decor) decor.IsClaimed = IsClaimed;
    }

    // ---- --huntcheck -----------------------------------------------------------------------------

    private bool _probe;
    private double _probeTime;

    /// <summary>
    /// <c>--huntcheck</c> (with an occasion running): waits for a drawn hunt spot, claims it in
    /// memory — no items granted, nothing saved — and checks it is then claimed and no longer drawn;
    /// prints the reward odds and what the occasion adds to a kitchen counter's loot. Exits non-zero
    /// on a failure.
    /// </summary>
    public override void _Process(double delta)
    {
        if (!_probe) return;
        _probeTime += delta;
        var decor = OccasionDecor.Instance;
        var first = decor?.AllSpots().FirstOrDefault();
        if (first is not { Spot.Key: not null } found)
        {
            if (_probeTime > 90) Finish(false, "no hunt spot was drawn within 90 s");
            return;
        }

        var (spot, world) = found;
        bool ok = true;
        void Check(bool cond, string what) { ok &= cond; GD.Print($"[huntcheck] {(cond ? "ok  " : "FAIL")} {what}"); }

        Check(!IsClaimed(spot.Key), $"{spot.Key} starts unclaimed");
        Check(SpotNear(world, world) == spot.Key, "the gather hold finds it from where it stands");
        var occasion = OccasionOf(spot.Key)!;
        if (!_claims.TryGetValue(occasion.Instance, out var set)) _claims[occasion.Instance] = set = new();
        set.Add(spot.Key);
        Check(IsClaimed(spot.Key), $"claimed in {occasion.Instance}");
        decor!.Refresh(world);
        Check(decor.SpotsNear(world, 0.3f).All(s => s.Spot.Key != spot.Key), "the claimed spot is no longer drawn");

        var odds = new Dictionary<ItemId, int>();
        var rng = new Random(1);
        for (int i = 0; i < 10000; i++)
        {
            var (id, n) = occasion.Content.HuntReward(rng);
            odds[id] = odds.GetValueOrDefault(id) + 1;
        }
        GD.Print($"[huntcheck] rewards per 10k: {string.Join(", ", odds.OrderByDescending(o => o.Value).Select(o => $"{o.Key} {o.Value}"))}");

        var loot = new Dictionary<ItemId, int>();
        for (int i = 0; i < 2000; i++)
            foreach (var stack in Loot.LootTables.Roll(Terrain.Format.BuildingKind.House, Interiors.FurnitureType.Counter, new Random(i)))
                loot[stack.Id] = loot.GetValueOrDefault(stack.Id) + stack.Count;
        bool treats = loot.Keys.Any(id => ItemDefs.Get(id)?.Category == ItemCategory.Food && (int)id >= (int)ItemId.Candy);
        Check(!occasion.Has(OccasionFacets.Loot) || treats, "a kitchen counter gives the occasion's treats");
        GD.Print($"[huntcheck] 2000 counters: {string.Join(", ", loot.OrderByDescending(o => o.Value).Take(8).Select(o => $"{o.Key} {o.Value}"))}");
        Finish(ok, null);
    }

    private void Finish(bool ok, string? why)
    {
        _probe = false;
        GD.Print(ok ? "[huntcheck] all passed" : $"[huntcheck] FAILED{(why == null ? "" : ": " + why)}");
        GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>The running instance a spot key belongs to, if that occasion's hunt is on.</summary>
    private static ActiveOccasion? OccasionOf(string key)
    {
        int colon = key.IndexOf(':');
        if (colon <= 0 || OccasionManager.Instance is not { } m) return null;
        string id = key[..colon];
        return m.Active.FirstOrDefault(a => a.Id == id && a.Has(OccasionFacets.Hunt));
    }

    public bool IsClaimed(string key) =>
        OccasionOf(key) is { } a && _claims.TryGetValue(a.Instance, out var set) && set.Contains(key);

    /// <summary>How many spots this player has found in an instance.</summary>
    public int Found(string instance) => _claims.TryGetValue(instance, out var set) ? set.Count : 0;

    /// <summary>For <see cref="Loot.Gathering"/>: the unclaimed spot in reach, ahead first, as its key.</summary>
    public string? SpotNear(Vector3 feet, Vector3 ahead)
    {
        if (OccasionDecor.Instance is not { } decor) return null;
        foreach (var at in new[] { ahead, feet })
            foreach (var (spot, world) in decor.SpotsNear(at, Reach))
            {
                if (IsClaimed(spot.Key) || OccasionOf(spot.Key) == null) continue;
                _seen[spot.Key] = (world, spot.Label);
                return spot.Key;
            }
        return null;
    }

    /// <summary>"a treat", "a gift": what the prompt calls the spot.</summary>
    public string LabelFor(string key) => _seen.TryGetValue(key, out var s) ? s.Label : "it";

    /// <summary>Takes a spot: grants the occasion's reward, remembers the claim, and takes the prop away.</summary>
    public void Claim(string key, ItemController items)
    {
        if (OccasionOf(key) is not { } occasion || IsClaimed(key)) return;
        var (id, count) = occasion.Content.HuntReward(_rng);
        if (id != ItemId.None)
        {
            if (items.Inventory.Room(id) < count)
            {
                items.Ui.Toast("No room in your pack.");
                return;
            }
            items.Inventory.Add(id, count);
        }

        if (!_claims.TryGetValue(occasion.Instance, out var set)) _claims[occasion.Instance] = set = new();
        set.Add(key);
        Save();

        string what = ItemDefs.Get(id)?.Name ?? "nothing";
        string rare = ItemDefs.Get(id)?.Use == ItemUse.Wear ? " — a rare find!" : "";
        items.Ui.Toast($"+{count} {what}{rare}  ({set.Count} found this {occasion.Content.Title})");
        if (_seen.TryGetValue(key, out var seen)) OccasionDecor.Instance?.Refresh(seen.World);
    }

    private void Load()
    {
        try
        {
            if (!Godot.FileAccess.FileExists(File)) return;
            using var f = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Read);
            _claims = JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(f.GetAsText()) ?? new();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[occasions] could not read {File}: {e.Message}");
        }
    }

    private void Save()
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute("user://occasions");
            using var f = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Write);
            f.StoreString(JsonSerializer.Serialize(_claims, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            GD.PushWarning($"[occasions] could not write {File}: {e.Message}");
        }
    }
}
