using Godot;
using UnitSport.Audio;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// Foot weapons against players (#178). The shooter traces its own shot against the other
/// players' bodies as it sees them, and sends one <see cref="ItemEventKind.Hit"/> per player hit.
/// The server checks it (PvP allowed, distances, damage cap: <see cref="ItemEvents"/>) and hands it
/// to the victim alone, whose own machine takes the health: the same client-authoritative model
/// as the aircraft guns (<c>Combat/CombatManager</c>). Docs: <c>docs/notes/combat/pvp-weapons.md</c>.
/// </summary>
public static class PlayerHits
{
    /// <summary>A body's hit capsule: radius, and height from the feet.</summary>
    private const float Radius = 0.42f, Height = 1.85f;
    /// <summary>Above this height on the body a hit is a head shot.</summary>
    public const float HeadFrom = 1.5f;

    private static readonly Random Rng = new();

    /// <summary>What one hit says, packed into <see cref="ItemEvent.Extra"/>.</summary>
    public readonly record struct Hit(long Victim, float Damage, ItemId Weapon, bool Head)
    {
        public string Pack() => FormattableString.Invariant($"{Victim}|{Damage:F1}|{(int)Weapon}|{(Head ? 1 : 0)}");

        public static Hit? Parse(string extra)
        {
            var p = extra.Split('|');
            if (p.Length != 4
                || !long.TryParse(p[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long victim)
                || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float damage)
                || !int.TryParse(p[2], out int weapon) || !float.IsFinite(damage) || damage <= 0)
                return null;
            return new Hit(victim, damage, (ItemId)weapon, p[3] == "1");
        }
    }

    /// <summary>
    /// Fires <paramref name="weapon"/> from <paramref name="eye"/> along <paramref name="aim"/>:
    /// every pellet finds the nearest player body it crosses that no wall hides, and each player hit
    /// gets one Hit event with the sum. Returns the number of players hit.
    /// </summary>
    public static int Shoot(FootPlayer shooter, Vector3 eye, Vector3 aim, WeaponDef weapon)
    {
        var targets = Targets(shooter);
        if (targets.Count == 0) return 0;
        var space = shooter.GetWorld3D().DirectSpaceState;
        var sums = new Dictionary<FootPlayer, (float Damage, bool Head, Vector3 At)>();
        var basis = Basis.LookingAt(aim.Normalized(), Mathf.Abs(aim.Normalized().Y) > 0.99f ? Vector3.Forward : Vector3.Up);
        float spread = Mathf.DegToRad(weapon.SpreadDeg);

        for (int i = 0; i < weapon.Pellets; i++)
        {
            // a uniform disc of directions inside the cone
            float r = spread * Mathf.Sqrt((float)Rng.NextDouble()), a = (float)Rng.NextDouble() * Mathf.Tau;
            var dir = (basis * new Vector3(Mathf.Sin(r) * Mathf.Cos(a), Mathf.Sin(r) * Mathf.Sin(a), -Mathf.Cos(r))).Normalized();

            FootPlayer? best = null;
            float bestT = weapon.Range;
            Vector3 bestAt = default;
            foreach (var p in targets)
                if (RayCapsule(eye, dir, p.GlobalPosition, out float t) && t < bestT)
                {
                    best = p;
                    bestT = t;
                    bestAt = eye + dir * t;
                }
            if (best == null || Blocked(space, shooter, best, eye, bestAt)) continue;

            bool head = bestAt.Y - best.GlobalPosition.Y >= HeadFrom;
            float dmg = weapon.DamageAt(bestT) * (head ? weapon.HeadMultiplier : 1f);
            sums[best] = sums.TryGetValue(best, out var s) ? (s.Damage + dmg, s.Head || head, s.At) : (dmg, head, bestAt);
        }

        foreach (var (victim, (damage, head, at)) in sums)
            Send(shooter, victim, at, aim, new Hit(PeerOf(victim), damage, weapon.Id, head));
        return sums.Count;
    }

    /// <summary>A blade: the nearest standing player within reach in front of <paramref name="shooter"/>.</summary>
    public static bool Stab(FootPlayer shooter, Vector3 eye, Vector3 aim, WeaponDef weapon)
    {
        FootPlayer? best = null;
        float bestD = weapon.Range;
        var flat = (aim with { Y = 0 }).Normalized();
        foreach (var p in Targets(shooter))
        {
            var to = p.GlobalPosition + Vector3.Up * 1.1f - eye;
            float d = to.Length();
            if (d > bestD || d < 0.01f) continue;
            // a 50-degree swing in front, any height a knife reaches
            if ((to with { Y = 0 }).Normalized().Dot(flat) < 0.64f) continue;
            best = p;
            bestD = d;
        }
        if (best == null) return false;
        var at = best.GlobalPosition + Vector3.Up * 1.1f;
        if (Blocked(shooter.GetWorld3D().DirectSpaceState, shooter, best, eye, at)) return false;
        Send(shooter, best, at, aim, new Hit(PeerOf(best), weapon.Damage, weapon.Id, false));
        return true;
    }

    /// <summary>The bodies a shot can hit: other real players, standing, as this peer draws them.</summary>
    private static List<FootPlayer> Targets(FootPlayer shooter)
    {
        var list = new List<FootPlayer>();
        foreach (var n in shooter.GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != shooter && !p.Npc && p.Down != 1 && PeerOf(p) > 0 && p.IsInsideTree())   // downed (2) can be finished, #475
                list.Add(p);
        return list;
    }

    private static long PeerOf(FootPlayer p) => FootPlayer.NetId(p.Name) ?? 0;

    /// <summary>
    /// The ray against a body: a vertical cylinder standing at <paramref name="feet"/>. True with
    /// the distance along the ray to where it enters.
    /// </summary>
    private static bool RayCapsule(Vector3 from, Vector3 dir, Vector3 feet, out float t)
    {
        float top = feet.Y + Height, best = float.MaxValue;
        var w = new Vector2(from.X - feet.X, from.Z - feet.Z);
        var dh = new Vector2(dir.X, dir.Z);
        // the side: |w + s·dh| = r
        float a = dh.Dot(dh), b = 2f * w.Dot(dh), c = w.Dot(w) - Radius * Radius;
        if (a > 1e-8f && b * b - 4f * a * c is var disc and >= 0f)
        {
            float sq = Mathf.Sqrt(disc);
            foreach (float s in new[] { (-b - sq) / (2f * a), (-b + sq) / (2f * a) })
                if (s >= 0f && from.Y + dir.Y * s is var y && y >= feet.Y && y <= top) best = Mathf.Min(best, s);
        }
        // the top and the bottom
        if (Mathf.Abs(dir.Y) > 1e-6f)
            foreach (float plane in new[] { top, feet.Y })
            {
                float s = (plane - from.Y) / dir.Y;
                if (s >= 0f && (w + dh * s).LengthSquared() <= Radius * Radius) best = Mathf.Min(best, s);
            }
        t = best;
        return best < float.MaxValue;
    }

    /// <summary>A wall (terrain, a building, a tree) between the eye and the hit point.</summary>
    private static bool Blocked(PhysicsDirectSpaceState3D space, FootPlayer shooter, FootPlayer victim, Vector3 from, Vector3 to)
    {
        var query = PhysicsRayQueryParameters3D.Create(from, to, uint.MaxValue,
            new Godot.Collections.Array<Rid> { shooter.GetRid(), victim.GetRid() });
        var hit = space.IntersectRay(query);
        return hit.Count > 0 && hit["collider"].AsGodotObject() is not FootPlayer;
    }

    private static void Send(FootPlayer shooter, FootPlayer victim, Vector3 at, Vector3 aim, Hit hit)
    {
        GD.Print(FormattableString.Invariant($"[pvp] hit peer {hit.Victim} for {hit.Damage:F1} with {hit.Weapon}{(hit.Head ? " (head)" : "")}"));
        ItemEvents.Instance?.Send(ItemEventKind.Hit, at, aim, hit.Pack());
    }

    // ------------------------------------------------------------------------------------
    // the event, on the shooter (a marker) and on the victim (the damage)
    // ------------------------------------------------------------------------------------

    /// <summary>The <see cref="ItemEventKind.Hit"/> handler, registered by <see cref="ItemEvents"/>.</summary>
    public static void OnHit(ItemEvents node, ItemEvent e)
    {
        if (Hit.Parse(e.Extra) is not { } hit) return;
        if (e.Local)
        {
            HitMarker.Show(node, hit.Head);
            return;
        }
        // the server sends a hit to its victim only: that is this peer's own player
        var me = node.GetNodeOrNull<FootPlayer>("../Players/" + node.Multiplayer.GetUniqueId());
        if (me == null || me.Eliminated) return;
        me.ShotHit(hit.Damage, e.Peer);
        node.Sound3D(me.GlobalPosition + Vector3.Up * 1.2f, SfxSynth.ImpactBank.Pick(Rng).Stream, 1.3f, -2f, unitSize: 4f, maxDistance: 40f);
    }
}

/// <summary>The shooter's confirmation: a small cross at the screen centre for a quarter second.</summary>
public partial class HitMarker : Control
{
    private static HitMarker? _instance;
    private double _left;
    private bool _head;

    public static void Show(Node parent, bool head)
    {
        if (_instance == null || !GodotObject.IsInstanceValid(_instance))
        {
            var layer = new CanvasLayer { Name = "HitMarker", Layer = 11 };
            _instance = new HitMarker { MouseFilter = MouseFilterEnum.Ignore };
            _instance.SetAnchorsPreset(LayoutPreset.FullRect);
            layer.AddChild(_instance);
            parent.AddChild(layer);
        }
        _instance._left = 0.25;
        _instance._head = head;
        _instance.QueueRedraw();
        var (stream, _, _) = SfxSynth.TickBank.Pick(Rng);
        var s = new AudioStreamPlayer { Stream = stream, PitchScale = head ? 1.8f : 1.4f, VolumeDb = -4f, Bus = SfxBus.Name };
        _instance.AddChild(s);
        s.Finished += s.QueueFree;
        s.Play();
    }

    private static readonly Random Rng = new();

    public override void _Process(double delta)
    {
        if (_left <= 0) return;
        _left -= delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_left <= 0) return;
        var c = Size * 0.5f;
        var col = _head ? new Color(1f, 0.25f, 0.2f) : Colors.White;
        foreach (var (x, y) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
            DrawLine(c + new Vector2(x, y) * 6f, c + new Vector2(x, y) * 13f, col, 2f);
    }
}
