using System.Globalization;
using Godot;
using UnitSport.Audio;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// A thrown thing hitting someone (#261): it hurts, never kills, knocks them back a step, rocks
/// the body and plays a bonk, an "oof" and a little dizzy tune over them.
///
/// <para>
/// Whoever simulates the flight (the thrower: its proxy, then the body it has authority over)
/// tests the path the item travelled this physics step against the other players' bodies as it
/// sees them, the way <see cref="PlayerHits"/> traces a shot, and sends one
/// <see cref="ItemEventKind.Bonk"/>. The server checks it (<see cref="ItemEvents"/>: speed and
/// distance plausible, damage under <see cref="MaxDamage"/>) and relays it to everyone who can
/// see either player: every peer plays the reaction on its copy of the victim, and the victim's
/// own machine takes the health and the knock-back.
/// </para>
/// </summary>
public static class ThrowHits
{
    /// <summary>Slower than this (m/s) it is a drop or a roll, not a hit.</summary>
    public const float MinSpeed = 5f;
    /// <summary>The most one throw can take; the server refuses more.</summary>
    public const float MaxDamage = 22f;
    /// <summary>A hit never takes the last of this: a thrown radio stuns, it does not knock out.</summary>
    public const float Floor = 5f;
    /// <summary>The body's hit capsule as a shot sees it, a bit fatter for a tumbling object.</summary>
    private const float Radius = 0.45f, Height = 1.85f;

    private static readonly Random Rng = new();

    /// <summary>What one bonk says, packed into <see cref="ItemEvent.Extra"/>.</summary>
    public readonly record struct Bonk(long Victim, float Damage, ItemId Item)
    {
        public string Pack() => string.Create(CultureInfo.InvariantCulture, $"{Victim}|{Damage:F1}|{(int)Item}");

        public static Bonk? Parse(string extra)
        {
            var p = extra.Split('|');
            if (p.Length != 3
                || !long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long victim)
                || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float damage)
                || !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int item)
                || !float.IsFinite(damage) || damage <= 0)
                return null;
            return new Bonk(victim, damage, (ItemId)item);
        }
    }

    /// <summary>How hard a thing of <paramref name="mass"/> kg at <paramref name="speed"/> m/s hits.</summary>
    public static float DamageFor(float speed, float mass) =>
        Mathf.Clamp((speed - MinSpeed * 0.6f) * (0.55f + 0.45f * Mathf.Clamp(mass, 0.1f, 4f)), 3f, MaxDamage);

    /// <summary>
    /// One physics step of a flying item on the peer that simulates it: true (and the event sent)
    /// when its path from <paramref name="from"/> to <paramref name="to"/> went through someone.
    /// The caller stops testing after a hit, so one throw bonks once.
    /// </summary>
    public static bool Step(RigidBody3D item, Vector3 from, Vector3 to, Vector3 velocity, ItemId id)
    {
        float speed = velocity.Length();
        if (speed < MinSpeed || ItemEvents.Instance is not { } events) return false;
        var path = to - from;
        float len = path.Length();
        if (len < 1e-4f) return false;
        var dir = path / len;

        FootPlayer? thrower = null, best = null;
        float bestT = len + Radius;
        foreach (var node in item.GetTree().GetNodesInGroup(FootPlayer.Group))
        {
            if (node is not FootPlayer p || p.Npc || !p.IsInsideTree()) continue;
            if (p.IsMultiplayerAuthority()) { thrower = p; continue; }   // never the thrower: it leaves their hand
            if (p.Down != 0 || FootPlayer.NetId(p.Name) is not long peer || peer <= 0) continue;
            if (Through(from, dir, p.GlobalPosition, out float t) && t < bestT) { bestT = t; best = p; }
        }
        if (best == null || thrower == null) return false;

        var at = from + dir * Mathf.Min(bestT, len);
        var bonk = new Bonk(FootPlayer.NetId(best.Name) ?? 0, DamageFor(speed, item.Mass), id);
        GD.Print(FormattableString.Invariant($"[bonk] {id} at {speed:F1} m/s hit peer {bonk.Victim} for {bonk.Damage:F1}"));
        events.Send(ItemEventKind.Bonk, at, velocity, bonk.Pack());
        // it bounces off the body rather than flying on through it
        item.LinearVelocity = -velocity * 0.25f + Vector3.Up * 2.2f + (at - best.GlobalPosition with { Y = at.Y }).Normalized() * 1.5f;
        item.AngularVelocity *= -0.6f;
        return true;
    }

    /// <summary>The path entering a body: a vertical cylinder standing at <paramref name="feet"/>.</summary>
    private static bool Through(Vector3 from, Vector3 dir, Vector3 feet, out float t)
    {
        t = float.MaxValue;
        var w = new Vector2(from.X - feet.X, from.Z - feet.Z);
        var dh = new Vector2(dir.X, dir.Z);
        // already inside it (spawned against someone): a hit at once
        if (w.LengthSquared() <= Radius * Radius && from.Y >= feet.Y && from.Y <= feet.Y + Height) { t = 0; return true; }
        float a = dh.Dot(dh), b = 2f * w.Dot(dh), c = w.Dot(w) - Radius * Radius;
        if (a < 1e-8f || b * b - 4f * a * c is var disc && disc < 0f) return false;
        float s = (-b - Mathf.Sqrt(disc)) / (2f * a);
        if (s < 0f) return false;
        float y = from.Y + dir.Y * s;
        if (y < feet.Y || y > feet.Y + Height) return false;
        t = s;
        return true;
    }

    // ------------------------------------------------------------------------------------
    // the event, on every peer that gets it
    // ------------------------------------------------------------------------------------

    /// <summary>The <see cref="ItemEventKind.Bonk"/> handler, registered by <see cref="ItemEvents"/>.</summary>
    public static void OnBonk(ItemEvents node, ItemEvent e)
    {
        if (Bonk.Parse(e.Extra) is not { } bonk) return;
        var victim = node.GetNodeOrNull<FootPlayer>("../Players/" + bonk.Victim);
        var at = victim != null && victim.IsInsideTree() ? victim.GlobalPosition + Vector3.Up * 1.5f : e.Position;
        var push = e.Direction with { Y = 0 };
        push = push.LengthSquared() > 1e-4f ? push.Normalized() : Vector3.Zero;

        // what everyone near hears: the knock, the grunt, and the tune of seeing stars
        float heavy = Mathf.Clamp(bonk.Damage / MaxDamage, 0f, 1f);
        var (knock, kp, kv) = SfxSynth.BonkBank.Pick(Rng);
        node.Sound3D(e.Position, knock, kp * Mathf.Lerp(1.15f, 0.8f, heavy), kv - 1f, unitSize: 6f, maxDistance: 60f);
        var (oof, op, ov) = SfxSynth.OofBank.Pick(Rng);
        node.Sound3D(at, oof, op * (victim != null ? VoicePitch(victim) : 1f), ov - 2f, unitSize: 5f, maxDistance: 45f);
        var (tune, tp, tv) = SfxSynth.DizzyBank.Pick(Rng);
        node.Sound3D(at + Vector3.Up * 0.4f, tune, tp, tv - 8f, unitSize: 4f, maxDistance: 35f);

        if (victim == null) return;
        // the body rocks away from the blow on every screen
        victim.Flinch(push, 0.6f + 0.8f * heavy);
        if (e.Local) HitMarker.Show(node, head: false);
        // the victim's own machine: the health and the step back
        if (victim.IsMultiplayerAuthority() && !e.Local) victim.Bonked(bonk.Damage, push, e.Peer);
    }

    /// <summary>A voice of its own per player, the same on every peer.</summary>
    private static float VoicePitch(FootPlayer p)
    {
        uint h = 2166136261u;
        foreach (char c in p.Name.ToString()) { h ^= c; h *= 16777619u; }
        return 0.85f + (h % 1000u) / 1000f * 0.35f;
    }
}
