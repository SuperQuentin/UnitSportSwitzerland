using System.Collections.Generic;
using Godot;
using UnitSport.Player;

namespace UnitSport.Bots;

/// <summary>Someone a bot can see now: who, where (world), how far, and whether they are down.</summary>
public readonly record struct Sighting(long Id, FootPlayer Body, Vector3 At, float Distance, bool Downed);

/// <summary>
/// What one bot perceives (#837), honestly: only what a player standing there could. A brain reads
/// the world through this and nothing else.
/// <list type="bullet">
/// <item><b>Sight</b>: bodies within <see cref="SightRange"/> in a <see cref="FieldOfView"/> cone ahead of
/// where it faces, or anywhere within <see cref="NearSense"/> (you hear and glimpse who is at your elbow),
/// with nothing in the way: a ray on the server's collision (terrain, buildings, trunks round the bot).</item>
/// <item><b>Hearing</b>: shots within their weapon's earshot (<see cref="HearShot"/>), from
/// <c>ItemEvents.ServerHeard</c>, with where they came from roughly (a player hears a direction, not a point).</item>
/// <item><b>Memory</b>: where it last saw each body and when, for the brain to look again or chase.</item>
/// <item><b>Hurt</b>: who hit it last (<see cref="FootPlayer.LastAttacker"/>), and where they were, give or take.</item>
/// </list>
/// Mode-free: who is hostile is the brain's call.
/// </summary>
public sealed class BotSenses
{
    /// <summary>How far a bot sees a standing figure, m (the hunting rifle's band starts beyond it).</summary>
    public const float SightRange = 220f;
    /// <summary>The cone it sees in, degrees, centred on where it faces.</summary>
    public const float FieldOfView = 150f;
    /// <summary>Within this it senses anyone, whatever way it faces.</summary>
    public const float NearSense = 15f;
    /// <summary>A heard shot is placed within this of where it really was.</summary>
    private const float HearingBlur = 25f;
    /// <summary>A sighting older than this is forgotten.</summary>
    public const double MemorySeconds = 30;

    public readonly List<Sighting> Seen = new();
    /// <summary>Last seen place and time per body id.</summary>
    public readonly Dictionary<long, (Vector3 At, double When)> Memory = new();
    /// <summary>When it first saw each body in this sighting streak (the reaction time counts from it).</summary>
    public readonly Dictionary<long, double> FirstSeen = new();
    /// <summary>The latest shot it heard: where (blurred, world) and when; null for none.</summary>
    public (Vector3 At, double When, long Who)? Heard;

    private static readonly System.Random Blur = new();

    /// <summary>The earshot of a shot, m: guns carry far, a pistol less than a rifle.</summary>
    public static float HearShot(Items.ItemId weapon) => weapon switch
    {
        Items.ItemId.Pistol => 250f,
        Items.ItemId.Shotgun => 300f,
        Items.ItemId.Rifle or Items.ItemId.HuntingRifle => 450f,
        _ => 0f,
    };

    /// <summary>Looks round: who it can see now, from the bodies under <c>World/Players</c>.</summary>
    public void Look(Bot bot, IEnumerable<FootPlayer> bodies, PhysicsDirectSpaceState3D space, double now)
    {
        Seen.Clear();
        var me = bot.Body;
        var eye = me.EyePosition;
        float facing = bot.Intent.FaceYaw ?? me.Rotation.Y;
        var ahead = new Vector3(-Mathf.Sin(facing), 0, -Mathf.Cos(facing));
        float cosHalf = Mathf.Cos(Mathf.DegToRad(FieldOfView * 0.5f));
        var exclude = new Godot.Collections.Array<Rid> { me.GetRid() };
        foreach (var other in bodies)
        {
            if (other == me || other.Eliminated || !other.IsInsideTree() || (other.Npc && !other.Bot)) continue;
            if (FootPlayer.NetId(other.Name) is not long id) continue;
            var chest = other.GlobalPosition + Vector3.Up * 1.2f;
            var to = chest - eye;
            float d = to.Length();
            if (d > SightRange || d < 0.01f) continue;
            var flat = new Vector3(to.X, 0, to.Z);
            if (d > NearSense && flat.LengthSquared() > 0.01f && flat.Normalized().Dot(ahead) < cosHalf) continue;
            // nothing solid between the eyes and the chest (other figures do not hide anyone)
            var query = PhysicsRayQueryParameters3D.Create(eye, chest, uint.MaxValue, exclude);
            var hit = space.IntersectRay(query);
            if (hit.Count > 0 && hit["collider"].AsGodotObject() is not FootPlayer) continue;
            bool downed = other.Down != 0;
            Seen.Add(new Sighting(id, other, other.GlobalPosition, d, downed));
            Memory[id] = (other.GlobalPosition, now);
            FirstSeen.TryAdd(id, now);
        }
        // a streak ends when it loses sight: the next sighting reacts afresh
        if (FirstSeen.Count > Seen.Count)
        {
            var gone = new List<long>();
            foreach (var id in FirstSeen.Keys) if (!Seen.Exists(s => s.Id == id)) gone.Add(id);
            foreach (var id in gone) FirstSeen.Remove(id);
        }
        if (Memory.Count > 0)
        {
            var old = new List<long>();
            foreach (var (id, m) in Memory) if (now - m.When > MemorySeconds) old.Add(id);
            foreach (var id in old) Memory.Remove(id);
        }
    }

    /// <summary>A shot was fired at <paramref name="at"/> (world) by <paramref name="who"/>: heard if within earshot.</summary>
    public void Hear(Bot bot, Vector3 at, long who, float earshot, double now)
    {
        if (who == bot.Id || earshot <= 0f || bot.Body.GlobalPosition.DistanceTo(at) > earshot) return;
        var blur = new Vector3((float)(Blur.NextDouble() * 2 - 1), 0, (float)(Blur.NextDouble() * 2 - 1)) * HearingBlur;
        Heard = (at + blur, now, who);
    }
}
