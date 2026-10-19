using Godot;

namespace UnitSport.Concert;

/// <summary>What carries a broadcast camera, and so how it may move.</summary>
public enum RigKind : byte
{
    /// <summary>On a tripod or a platform: it only pans, tilts and zooms.</summary>
    Fixed,
    /// <summary>A dolly on a track: along the rails, nowhere else.</summary>
    Rail,
    /// <summary>A cable cam on four wires from the roof: anywhere in a box over the pitch.</summary>
    Wire,
    /// <summary>A jib (a crane arm on a base): the arm swings and booms up and down.</summary>
    Jib,
    /// <summary>A Steadicam on stage: walks round the artists.</summary>
    Steadicam,
    /// <summary>A drone: anywhere above and round the stadium.</summary>
    Drone,
}

/// <summary>
/// One broadcast camera of a concert (#885): what carries it (<see cref="RigKind"/>), where it may go
/// (a track's two ends, a wire cam's box, a jib's base and arm), and where it is: <see cref="S"/> along a
/// track or a jib's swing, <see cref="Boom"/> a jib's height, <see cref="P"/> a free camera's position,
/// <see cref="Yaw"/>, <see cref="Pitch"/> and <see cref="Fov"/>. Stadium frame. Nobody at it, it moves
/// on its own from the clock (the same on every peer); a player operating it moves it, and its state
/// goes to everyone (<see cref="ConcertService"/>).
/// </summary>
public sealed class CameraRig
{
    public required string Name { get; init; }
    public required RigKind Kind { get; init; }
    /// <summary>A track's ends; a fixed camera's place (A); a jib's base (A); a Steadicam's and a drone's centre (A).</summary>
    public Vector3 A { get; init; }
    public Vector3 B { get; init; }
    /// <summary>A wire cam's box; a jib's arm length (Box.X); a Steadicam's or a drone's radius (Box.X), a drone's height range (Box.Y..Box.Z).</summary>
    public Vector3 BoxMin { get; init; }
    public Vector3 BoxMax { get; init; }
    public float Arm { get; init; }
    /// <summary>Where it looks when nobody aims it.</summary>
    public required Func<double, Vector3> Target { get; init; }
    /// <summary>Its lens: the widest and the tightest field of view, and the one it rests at.</summary>
    public float FovWide { get; init; } = 70f;
    public float FovTight { get; init; } = 8f;
    public float FovRest { get; init; } = 45f;

    // where it is now
    public float S, Boom = 0.5f;
    public Vector3 P;
    public float Yaw, Pitch, Fov = 45f;
    /// <summary>The player operating it (a peer id; offline 1), 0 when it runs on its own.</summary>
    public long Operator;

    /// <summary>Where the lens is for the current state.</summary>
    public Vector3 Position => Kind switch
    {
        RigKind.Rail => A.Lerp(B, Mathf.Clamp(S, 0f, 1f)),
        RigKind.Jib => JibHead(),
        RigKind.Fixed => A,
        _ => P,
    };

    private Vector3 JibHead()
    {
        // the arm swings round the base (S, -1..1 is ±70°) and booms (0 low, 1 high)
        float swing = S * Mathf.DegToRad(70f), lift = Mathf.Lerp(-0.25f, 0.9f, Boom);
        var dir = new Vector3(MathF.Sin(swing) * MathF.Cos(lift), MathF.Sin(lift), MathF.Cos(swing) * MathF.Cos(lift));
        return A + new Vector3(0, 1.8f, 0) + Turn(dir) * Arm;
    }

    /// <summary>A jib's arm points along B (from its base) at swing 0.</summary>
    private Vector3 Turn(Vector3 d)
    {
        float yaw = Mathf.Atan2(B.X, B.Z);
        return d.Rotated(Vector3.Up, yaw);
    }

    /// <summary>The camera's look, from its yaw and pitch (−Z forward at yaw 0).</summary>
    public Basis Basis => Basis.FromEuler(new Vector3(Pitch, Yaw, 0));

    /// <summary>Aims it at a point (an unoperated camera follows its target).</summary>
    public void AimAt(Vector3 at)
    {
        var d = at - Position;
        if (d.LengthSquared() < 1e-4f) return;
        Yaw = Mathf.Atan2(-d.X, -d.Z);
        Pitch = Mathf.Atan2(d.Y, new Vector2(d.X, d.Z).Length());
    }

    /// <summary>Nobody at it: where it goes on its own at a moment, and what it looks at.</summary>
    public void Auto(double t)
    {
        float s(double rate, float ph = 0) => MathF.Sin((float)(t * rate) + ph);
        switch (Kind)
        {
            case RigKind.Rail: S = 0.5f + 0.5f * s(0.12); break;
            case RigKind.Jib: S = 0.8f * s(0.17); Boom = 0.5f + 0.45f * s(0.11, 1f); break;
            case RigKind.Wire:
                P = new Vector3(Mathf.Lerp(BoxMin.X, BoxMax.X, 0.5f + 0.45f * s(0.07)), Mathf.Lerp(BoxMin.Y, BoxMax.Y, 0.5f + 0.4f * s(0.05, 2f)),
                    Mathf.Lerp(BoxMin.Z, BoxMax.Z, 0.5f + 0.45f * s(0.09, 1f)));
                break;
            case RigKind.Steadicam:
                P = A + new Vector3(BoxMax.X * s(0.2), 0, BoxMax.X * 0.5f * (1f + s(0.13, 1f)));
                break;
            case RigKind.Drone:
                float a = (float)(t * 0.05);
                P = A + new Vector3(BoxMax.X * MathF.Cos(a), Mathf.Lerp(BoxMax.Y, BoxMax.Z, 0.5f + 0.5f * s(0.03)), BoxMax.X * MathF.Sin(a));
                break;
        }
        AimAt(Target(t));
        Fov = FovRest;
    }

    /// <summary>
    /// The operator's hands for a frame: <paramref name="move"/> (x right, y forward, the stick or WASD),
    /// <paramref name="rise"/> (up −1..1), <paramref name="look"/> (yaw, pitch in radians), <paramref name="zoom"/> (in &gt; 0).
    /// Clamped to what the rig allows.
    /// </summary>
    public void Drive(Vector2 move, float rise, Vector2 look, float zoom, float dt)
    {
        Yaw -= look.X;
        Pitch = Mathf.Clamp(Pitch - look.Y, -1.45f, 1.2f);
        Fov = Mathf.Clamp(Fov * MathF.Exp(-zoom * dt * 1.2f), FovTight, FovWide);
        // movement relative to where the camera looks, flat
        var fwd = new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
        var right = new Vector3(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));
        switch (Kind)
        {
            case RigKind.Rail:
                // along the track by the stick's push projected on it
                var track = (B - A) with { Y = 0 };
                float len = Math.Max(1f, track.Length());
                var wish = right * move.X + fwd * move.Y;
                S = Mathf.Clamp(S + wish.Dot(track / len) * 6f * dt / len, 0f, 1f);
                break;
            case RigKind.Jib:
                S = Mathf.Clamp(S + move.X * 0.6f * dt, -1f, 1f);
                Boom = Mathf.Clamp(Boom + (move.Y + rise) * 0.4f * dt, 0f, 1f);
                break;
            case RigKind.Wire:
            case RigKind.Drone:
            case RigKind.Steadicam:
                float speed = Kind == RigKind.Steadicam ? 2.5f : 9f;
                var p = P + (right * move.X + fwd * move.Y) * speed * dt + Vector3.Up * (Kind == RigKind.Steadicam ? 0 : rise * speed * 0.6f * dt);
                P = Kind switch
                {
                    RigKind.Wire => p.Clamp(BoxMin, BoxMax),
                    RigKind.Steadicam => A + ((p - A) with { Y = 0 }).LimitLength(BoxMax.X),
                    _ => p with { Y = Mathf.Clamp(p.Y, BoxMax.Y, BoxMax.Z) },
                };
                break;
        }
    }

    /// <summary>The state as the wire carries it: eight floats.</summary>
    public float[] Pack() => new[] { S, Boom, P.X, P.Y, P.Z, Yaw, Pitch, Fov };

    public void Unpack(float[] v)
    {
        if (v.Length < 8) return;
        S = v[0]; Boom = v[1]; P = new Vector3(v[2], v[3], v[4]); Yaw = v[5]; Pitch = v[6]; Fov = v[7];
    }

    /// <summary>The cameras' names, by number (what <see cref="For"/> builds, in its order).</summary>
    public static readonly string[] Names =
    {
        "FOH long lens", "Pit dolly", "Side track", "Wire cam", "Jib", "Steadicam", "Crowd", "Wide FOH", "Wide far stand", "Drone", "Artist POV",
    };

    /// <summary>A concert's camera rigs, placed from its stage: the order is the cameras' numbers.</summary>
    public static List<CameraRig> For(StagePlan plan)
    {
        var e = plan.Event;
        var s = e.Stadium;
        float h = e.DeckHeight, hw = e.Width / 2, d = plan.Round ? 0 : e.Depth;
        Vector3 Sp(float x, float y, float z) => plan.StagePoint(x, y, z);
        var artist = Sp(0, h + 1.5f, plan.Round ? 0 : -d * 0.35f);
        Func<double, Vector3> onArtist = _ => artist;
        var deckFront = Sp(0, h + 1.2f, -1f);
        float hu = s.FrontHalfU - 4f, hv = s.FrontHalfV - 4f;
        var list = new List<CameraRig>
        {
            new() { Name = "FOH long lens", Kind = RigKind.Fixed, A = plan.Desk + new Vector3(0, 2.8f, 0), Target = onArtist, FovRest = 14f, FovTight = 4f },
            new() { Name = "Pit dolly", Kind = RigKind.Rail, A = Sp(-hw + 2f, 1.5f, 1.3f), B = Sp(hw - 2f, 1.5f, 1.3f), Target = onArtist, FovRest = 40f },
            new() { Name = "Side track", Kind = RigKind.Rail, A = Sp(-(hw + 6f), 1.7f, 4f), B = Sp(-(hw + 6f), 1.7f, plan.Round ? 30f : 45f), Target = _ => deckFront, FovRest = 35f },
            new()
            {
                Name = "Wire cam", Kind = RigKind.Wire, P = new Vector3(0, 16f, 0),
                BoxMin = new Vector3(-hu, 7f, -hv), BoxMax = new Vector3(hu, 26f, hv), Target = onArtist, FovRest = 55f,
            },
            new() { Name = "Jib", Kind = RigKind.Jib, A = Sp(hw * 0.55f, 0f, 7f), B = Sp(0, 0, -1f) - Sp(hw * 0.55f, 0f, 7f), Arm = 9f, Target = onArtist, FovRest = 42f },
            new() { Name = "Steadicam", Kind = RigKind.Steadicam, A = Sp(0, h + 1.6f, plan.Round ? 0 : -d * 0.2f), BoxMax = new Vector3(Math.Min(6f, hw - 2f), 0, 0), Target = onArtist, FovRest = 60f },
            new() { Name = "Crowd", Kind = RigKind.Fixed, A = Sp(0, h + 4f, 0.5f), Target = _ => Sp(0, 1.5f, 25f), FovRest = 70f },
            new() { Name = "Wide FOH", Kind = RigKind.Fixed, A = plan.Desk + new Vector3(0, 6f, 2f), Target = _ => Sp(0, h + 4f, -d * 0.5f), FovRest = 50f },
            new() { Name = "Wide far stand", Kind = RigKind.Fixed, A = Sp(0, 22f, plan.Round ? 60f : 75f), Target = _ => Sp(0, h + 3f, -d * 0.5f), FovRest = 45f },
            new()
            {
                Name = "Drone", Kind = RigKind.Drone, A = Vector3.Zero, P = new Vector3(120f, 70f, 0),
                BoxMax = new Vector3(120f, 45f, 110f), Target = _ => new Vector3(0, 5f, 0), FovRest = 55f,
            },
            new() { Name = "Artist POV", Kind = RigKind.Fixed, A = artist + Sp(0, 0, 0.4f) - Sp(0, 0, 0), Target = _ => Sp(0, 2f, 30f), FovRest = 75f },
        };
        foreach (var r in list)
        {
            r.Fov = r.FovRest;
            if (r.Kind == RigKind.Steadicam) r.P = r.A;
        }
        return list;
    }
}
