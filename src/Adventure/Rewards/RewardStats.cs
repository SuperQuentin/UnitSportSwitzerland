using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// <c>--rewardstats [E,N[,km]]</c> (#1227, tier 3, on real tiles: <c>--chunks &lt;dir&gt; --systems terrain,adventure</c>):
/// places every tile of a square round the point (default the spawn, 5 km half side) with this client's own
/// <see cref="RewardMap"/> and prints the spots per km² by kind and tier, the sealed share and the seals, then quits.
/// Tiles without terrain are left out of the area. The POIs' chests count only where the region has a <c>pois.bin</c>.
/// </summary>
public partial class RewardStats : Node
{
    public static bool Requested => CmdArgs.Has("--rewardstats");

    public override async void _Ready()
    {
        Name = "RewardStats";
        var arg = CmdArgs.Value("--rewardstats");
        double e = SpawnPoint.DefaultLv95E, n = SpawnPoint.DefaultLv95N, km = 5;
        if (arg != null && !arg.StartsWith("--", StringComparison.Ordinal))
        {
            var p = arg.Split(',');
            if (p.Length >= 2)
            {
                e = double.Parse(p[0], CultureInfo.InvariantCulture);
                n = double.Parse(p[1], CultureInfo.InvariantCulture);
            }
            if (p.Length >= 3) km = double.Parse(p[2], CultureInfo.InvariantCulture);
        }
        for (int i = 0; i < 600 && RewardService.Instance == null; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (RewardService.Instance is not { } svc)
        {
            GD.Print("[rewardstats] RESULT: FAILED (no rewards: --systems ...,adventure)");
            GetTree().Quit(1);
            return;
        }
        var centre = TileId.FromLv95(e, n);
        int r = (int)Math.Ceiling(km);
        int tiles = 0;
        var counts = new Dictionary<string, int>();
        foreach (var de in Enumerable.Range(-r, 2 * r))
            foreach (var dn in Enumerable.Range(-r, 2 * r))
            {
                var spots = await svc.Map.TileAsync(new TileId(centre.E + de, centre.N + dn));
                if (svc.Map.GridOf(new TileId(centre.E + de, centre.N + dn)) == null) continue;
                tiles++;
                foreach (var s in spots)
                {
                    string key = s.Kind == SpotKind.Chest ? $"chest {s.Tier}" : s.Kind.ToString().ToLowerInvariant();
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                    if (s.Seal != SealKind.None) counts["sealed " + s.Seal] = counts.GetValueOrDefault("sealed " + s.Seal) + 1;
                    if (s.PoiId != null && s.Kind == SpotKind.Chest) counts["chest at a place"] = counts.GetValueOrDefault("chest at a place") + 1;
                }
            }
        GD.Print(FormattableString.Invariant($"[rewardstats] {tiles} km² round {e:F0}/{n:F0} (placement v{RewardPlacer.Version}, {Places.PoiCatalogueHost.Current.Count} places)"));
        foreach (var (k, v) in counts.OrderBy(kv => kv.Key))
            GD.Print(FormattableString.Invariant($"[rewardstats] {k}: {v} ({(tiles > 0 ? v / (double)tiles : 0):F3} per km²)"));
        GD.Print(tiles > 0 ? "[rewardstats] RESULT: ok" : "[rewardstats] RESULT: FAILED (no terrain there)");
        GetTree().Quit(tiles > 0 ? 0 : 1);
    }
}
