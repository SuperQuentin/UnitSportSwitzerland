using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Birds;

/// <summary>
/// <c>godot --path . -- --birdcheck[,out.png] [--at E,N] [--birdmonth N]</c>
///
/// <para>
/// Stands a player at the spawn and checks the whole chain: (1) a survey of 600 random points
/// around it prints the habitats found and the species each one draws; (2) automatic spawning
/// runs for a few seconds and must put birds in the world; (3) a carrion crow is placed 25 m
/// away and shot, and must fall, land dead and score as game in season (the probe forces the
/// month to October); (4) the shotgun item fired through the real item path must spend a shell.
/// Non-zero exit on any failure.
/// </para>
/// </summary>
public partial class BirdProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly BirdLife _birds;
    private readonly ItemController _items;
    private readonly string? _shot;
    private bool _ok = true;

    public BirdProbe(ChunkManager chunks, WorldOrigin origin, BirdLife birds, ItemController items, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        _birds = birds;
        _items = items;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--birdcheck"))
            {
                var parts = a.Split(',');
                return (true, parts.Length > 1 ? parts[1] : null);
            }
        return (false, null);
    }

    private void Check(bool condition, string what)
    {
        GD.Print($"[birds] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    private SignalAwaiter Frame() => ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    public override async void _Ready()
    {
        _birds.AutoSpawn = false;
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--birdmonth") < 0) _birds.Month = 10;
        GD.Print($"[birds] catalogue: {BirdCatalog.All.Length} species, {BirdCatalog.All.Count(s => s.IsGame)} game; month {_birds.Month}");
        Check(BirdCatalog.All.All(s => BirdMesh.Get(s).Body.GetSurfaceCount() == 1), "every species builds a mesh");

        var (e, n) = SpawnPoint.ParseTarget();
        var origin = _origin;
        var player = new FootPlayer { Name = "BirdProbe", Terrain = _chunks };
        AddChild(player);
        _birds.PlayerOverride = () => player;
        player.LeaveInterior(origin.ToWorld(e, n, 3000), 0f);

        // wait for the ground and its cover under the spawn
        double t = 0;
        while (t < 30 && !(_chunks.TryGetHeight(player.GlobalPosition, out _) && _chunks.TryGetCover(player.GlobalPosition, out _)))
        {
            await Frame();
            t += GetProcessDeltaTime();
        }
        // No terrain here (a fresh clone): the survey and the spawner cannot be tested, but the
        // hunt itself - catalogue, meshes, shot, fall, score, item path - still can.
        bool terrain = _chunks.TryGetCover(player.GlobalPosition, out _);
        var standAt = player.GlobalPosition;
        if (!terrain) GD.Print("[birds] (no terrain loaded at the spawn: survey and spawning not tested)");
        else
        {
            float ground = _birds.Ground(player.GlobalPosition);
            player.LeaveInterior(player.GlobalPosition with { Y = ground + 1.5f }, 0f);
            player.Velocity = Vector3.Zero;
            for (int i = 0; i < 120 && !player.IsOnFloor(); i++) await Frame();
            Check(_chunks.TryGetCover(player.GlobalPosition, out _), $"terrain and cover loaded at the spawn after {t:0.0} s (ground {ground:0} m)");

            // (1) survey: what each habitat around here draws
            var rng = new Random(7);
            var tally = new Dictionary<Habitat, Dictionary<string, int>>();
            for (int i = 0; i < 600; i++)
            {
                var p = player.GlobalPosition + new Vector3((float)(rng.NextDouble() * 2 - 1) * 400f, 0, (float)(rng.NextDouble() * 2 - 1) * 400f);
                if (!_chunks.TryGetHeight(p, out float h) || !_chunks.TryGetCover(p, out var cover)) continue;
                var habitat = BirdLife.HabitatAt(cover, h);
                var s = _birds.Pick(habitat, h);
                if (!tally.TryGetValue(habitat, out var bySpecies)) tally[habitat] = bySpecies = new();
                string name = s?.Name ?? "(none)";
                bySpecies[name] = bySpecies.GetValueOrDefault(name) + 1;
            }
            foreach (var (habitat, bySpecies) in tally.OrderByDescending(kv => kv.Value.Values.Sum()))
                GD.Print($"[birds]   {habitat,-10} {bySpecies.Values.Sum(),4} pts: "
                         + string.Join(", ", bySpecies.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} {kv.Value}")));
            Check(tally.Count > 0, $"survey found {tally.Count} habitat(s)");

            // (2) automatic spawning
            _birds.AutoSpawn = true;
            for (double w = 0; w < 6; w += GetProcessDeltaTime()) await Frame();
            _birds.AutoSpawn = false;
            var alive = _birds.Birds.Where(b => b.State != Bird.Mode.Dead).ToList();
            Check(alive.Count > 0, $"spawned {alive.Count} birds: "
                + string.Join(", ", alive.GroupBy(b => $"{b.Species.Name} ({b.State})").Select(g => $"{g.Key} x{g.Count()}")));
        }

        // (3) a crow 25 m ahead, shot directly
        var crowSpecies = BirdCatalog.ByName("Carrion Crow")!;
        if (terrain) standAt = player.GlobalPosition;
        // Trunks are solid now and a pellet does not go through one: pick, of 16 headings, one with
        // a clear line to where the crow will sit, or the check tests the forest instead of the gun
        var forward = Vector3.Forward;
        var at = standAt + forward * 25f;
        at.Y = _birds.Ground(at);
        var space = player.GetWorld3D().DirectSpaceState;
        for (int k = 0; k < 16; k++)
        {
            var dir = new Basis(Vector3.Up, k * Mathf.Tau / 16f) * Vector3.Forward;
            var spot = standAt + dir * 25f;
            spot.Y = _birds.Ground(spot);
            var eyeAt = player.GlobalPosition + Vector3.Up * 1.6f;
            var q = PhysicsRayQueryParameters3D.Create(eyeAt, spot + Vector3.Up * 0.3f, uint.MaxValue,
                new Godot.Collections.Array<Rid> { player.GetRid() });
            if (space.IntersectRay(q).Count == 0) { forward = dir; at = spot; break; }
        }
        var crow = _birds.Spawn(crowSpecies, at, Bird.Mode.Ground);
        await Frame();

        if (_shot != null)
        {
            // one of each body plan in a row to the side of the shot line, wings spread, for a look
            string[] lineup = { "Grey Heron", "Mallard", "Common Buzzard", "Tawny Owl", "Carrion Crow",
                "Great Spotted Woodpecker", "Black Grouse", "Yellow-legged Gull", "Great Tit", "Barn Swallow" };
            var row = standAt + forward * 8f + Vector3.Right * 3f;
            _birds.PlayerOverride = () => null;   // or the player 10 m away flushes the lot
            var shown = new List<Bird>();
            for (int i = 0; i < lineup.Length; i++)
            {
                var spot = row + Vector3.Right * (i % 5) * 1.6f + forward * (i / 5) * 2.2f;
                spot.Y = _birds.Ground(spot);
                shown.Add(_birds.Spawn(BirdCatalog.ByName(lineup[i])!, spot, i % 2 == 0 ? Bird.Mode.Ground : Bird.Mode.Hovering, 1.2f));
            }
            var cam = new Camera3D { Fov = 40f };
            AddChild(cam);
            var centre = row + Vector3.Right * 3.2f + forward * 1.1f + Vector3.Up * 0.6f;
            cam.GlobalPosition = centre - forward * 9f + Vector3.Up * 1.5f;
            var look = centre - cam.GlobalPosition;
            cam.GlobalBasis = new Basis(Vector3.Up, Mathf.Atan2(-look.X, -look.Z)) * new Basis(Vector3.Right, Mathf.Atan2(look.Y, new Vector2(look.X, look.Z).Length()));
            cam.MakeCurrent();
            // the picture is of the birds: hide every HUD and menu layer while it is taken
            var layers = GetTree().Root.FindChildren("*", "CanvasLayer", true, false).OfType<CanvasLayer>().Where(l => l.Visible).ToList();
            foreach (var l in layers) l.Visible = false;
            await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(_shot);
            GD.Print($"[birds] wrote {_shot}");
            foreach (var l in layers) l.Visible = true;
            foreach (var b in shown) b.Node.Visible = false;
            _birds.PlayerOverride = () => player;
        }

        int before = _birds.Journal.Score;
        var eye = standAt + Vector3.Up * 1.5f;
        var hit = _birds.Shoot(eye, crow.Centre - eye, player);
        Check(hit == crow, $"shot at the crow 25 m away: hit {hit?.Species.Name ?? "nothing"}");
        if (hit == crow)
        {
            int points = _birds.Journal.Bag(crow.Species, _birds.Month);
            Check(points > 0 && _birds.Journal.Score == before + points, $"scored as game in season: {points:+0;-0} (score {_birds.Journal.Score})");
            for (int i = 0; i < 300 && crow.State != Bird.Mode.Dead; i++) await Frame();
            Check(crow.State == Bird.Mode.Dead, $"the crow fell and lies dead ({crow.State})");
        }

        // a protected species costs points
        var robin = BirdCatalog.ByName("European Robin")!;
        int penalty = _birds.Journal.Bag(robin, _birds.Month);
        Check(penalty < 0, $"a protected European Robin is penalised: {penalty}");

        // (3b) the bug of #143: a bird on the ground, in plain sight, was "behind a wall" - the
        // ground it stands on. The old test cast to the body's centre (a few cm above the ground)
        // and called any hit more than 0.5 m short of it a wall: at a grazing angle the ground
        // itself is that. Sparrows at 8-30 m all around; every one whose top the eye can see must
        // fall, and the log says how many the old rule lost.
        if (terrain)
        {
            var sparrow = BirdCatalog.ByName("House Sparrow")!;
            var rng2 = new Random(11);
            int visible = 0, hits = 0, oldRule = 0;
            _birds.PlayerOverride = () => null;
            var eye2 = standAt + Vector3.Up * 1.6f;
            var skip = new Godot.Collections.Array<Rid> { player.GetRid() };
            for (int k = 0; k < 80; k++)
            {
                float d = 8f + (float)rng2.NextDouble() * 22f;
                var dir = new Basis(Vector3.Up, (float)rng2.NextDouble() * Mathf.Tau) * Vector3.Forward;
                var spot = standAt + dir * d;
                spot.Y = _birds.Ground(spot);
                var bird = _birds.Spawn(sparrow, spot, Bird.Mode.Ground);
                bool seen = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye2, bird.Centre + Vector3.Up * 0.1f, uint.MaxValue, skip)).Count == 0;
                var old = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye2, bird.Centre, uint.MaxValue, skip));
                bool oldBlocked = old.Count > 0 && eye2.DistanceTo(old["position"].AsVector3()) < (bird.Centre - eye2).Dot((bird.Centre - eye2).Normalized()) - 0.5f;
                var got = _birds.Shoot(eye2, bird.Centre - eye2, player);
                if (seen) { visible++; if (got == bird) hits++; if (oldBlocked) oldRule++; }
                bird.Node.QueueFree();
                _birds.Remove(bird);
            }
            GD.Print($"[birds] sparrows on the ground in sight: {hits}/{visible} hit (the old wall test would have lost {oldRule})");
            Check(visible >= 10 && hits >= visible * 0.95, "a bird on the ground in plain sight is hit when aimed at");

            // the same for birds perched on a tree top: they sit inside the trunk's collision
            // cylinder, which the old test took for a wall. Seen = nothing but trunks in the way.
            var great = BirdCatalog.ByName("Great Tit")!;
            int pSeen = 0, pHits = 0, pOld = 0;
            uint noTrunks = uint.MaxValue & ~World.TreeColliders.Layer;
            for (int k = 0; k < 120 && pSeen < 30; k++)
            {
                float d = 10f + (float)rng2.NextDouble() * 25f;
                var dir = new Basis(Vector3.Up, (float)rng2.NextDouble() * Mathf.Tau) * Vector3.Forward;
                var spot = standAt + dir * d;
                var bird = _birds.Spawn(great, spot, Bird.Mode.Perched);
                var c = bird.Centre;
                float along = (c - eye2).Length();
                if (bird.State == Bird.Mode.Perched && along is > 6f and < 45f
                    && space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye2, c + Vector3.Up * 0.1f, noTrunks, skip)).Count == 0)
                {
                    var old = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye2, c, uint.MaxValue, skip));
                    bool oldBlocked = old.Count > 0 && eye2.DistanceTo(old["position"].AsVector3()) < along - 0.5f;
                    var got = _birds.Shoot(eye2, c - eye2, player);
                    pSeen++;
                    if (got == bird) pHits++;
                    if (oldBlocked) pOld++;
                }
                bird.Node.QueueFree();
                _birds.Remove(bird);
            }
            GD.Print($"[birds] tits perched on tree tops, no building or ground in the way: {pHits}/{pSeen} hit (the old wall test would have lost {pOld})");
            // another tree's trunk may still be in the way of a few
            Check(pSeen == 0 || pHits >= pSeen * 0.85, "a bird perched on a tree is hit when aimed at");
            _birds.PlayerOverride = () => player;
        }

        // (4) the item path spends a shell
        int Shells() => Enumerable.Range(0, Inventory.Size).Where(i => _items.Inventory[i].Id == ItemId.Shells).Sum(i => _items.Inventory[i].Count);
        int gun = Enumerable.Range(0, Inventory.Size).FirstOrDefault(i => _items.Inventory[i].Id == ItemId.Shotgun, -1);
        Check(gun >= 0, "the player has a shotgun");
        if (gun >= 0)
        {
            if (Shells() == 0) _items.Inventory.Add(ItemId.Shells, 1);
            int shells = Shells();
            _items.UseSlot(player, gun);
            Check(Shells() == shells - 1, $"firing spent a shell ({shells} -> {Shells()})");
        }

        GD.Print(_ok ? "[birds] RESULT: ok" : "[birds] RESULT: FAILED");
        _chunks.RemoveAnchor(player);
        GetTree().Quit(_ok ? 0 : 1);
    }
}
