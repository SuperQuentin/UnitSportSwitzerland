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
    /// <summary>Nothing of it shows (a head-cam).</summary>
    public bool Hidden { get; init; }
    /// <summary>A crew member works it (#985): how far below the lens their feet are (a jib's: at its base); 0 nobody stands at it.</summary>
    public float Crewed { get; init; }

    /// <summary>
    /// The ground it takes on the pitch (#985), stadium frame, <paramref name="margin"/> round its kit and
    /// its operator: a track's rails and dolly, a jib's whole sweep (±70°, the counterweight behind), a
    /// crewed tripod. Null for what flies, rides wires or works on the deck.
    /// </summary>
    public CrewZone? Footprint(float margin = 1f)
    {
        static Vector2 Flat(Vector3 p) => new(p.X, p.Z);
        switch (Kind)
        {
            case RigKind.Rail:
                return CrewZone.Along(Name, Flat(A), Flat(B), 0.9f + margin, 0.9f + margin);
            case RigKind.Jib:
                var dir = Flat(B).Normalized();
                return CrewZone.Along(Name, Flat(A) - dir * (0.3f * Arm), Flat(A) + dir * Arm, Arm * MathF.Sin(Mathf.DegToRad(70f)) + margin, margin);
            case RigKind.Fixed when Crewed > 0 && !Hidden:
                return CrewZone.Along(Name, Flat(A), Flat(A), 0.9f + margin, 0.9f + margin);
            default:
                return null;
        }
    }

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
                P = A + new Vector3(BoxMax.X * s(0.2), 0, BoxMax.X * 0.8f * s(0.13, 1f));
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

    /// <summary>A tripod's lens over its platform, and a dolly's over the dolly's deck (<see cref="ConcertCameras"/> draws them so).</summary>
    public const float Tripod = 1.35f, Dolly = 1.29f;

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
        // the deck's front edge along the stage's axis: a round deck's is half its width out
        float front = plan.Round ? hw : 0f;
        // the platforms' cameras on tripods (#985), their operators behind them
        var across = Sp(1, 0, 0) - Sp(0, 0, 0);
        var lift = new Vector3(0, Tripod, 0);
        var fohDeck = plan.FohCamera;
        // the side track beside the deck, on the floor: an arena's is narrow, its seats come close (#985)
        bool sideways = e.Layout is StageLayout.SideEast or StageLayout.SideWest;
        float track = Math.Min(hw + 6f, (sideways ? s.FrontHalfV : s.FrontHalfU) - 2.2f);
        // and short of the floor's far corners (cut off by the seats) ahead of the deck
        float trackEnd = Math.Min(plan.Round ? 30f : 45f, (plan.Round ? s.FrontHalfV : 2 * (sideways ? s.FrontHalfU : s.FrontHalfV) - 1.5f - d) - 3f - s.FrontCorner);
        var list = new List<CameraRig>
        {
            new() { Name = "FOH long lens", Kind = RigKind.Fixed, A = fohDeck + lift - across * 0.7f, Target = onArtist, FovRest = 14f, FovTight = 4f, Crewed = Tripod },
            new() { Name = "Pit dolly", Kind = RigKind.Rail, A = Sp(-hw + 2f, 1.5f, front + 1.3f), B = Sp(hw - 2f, 1.5f, front + 1.3f), Target = onArtist, FovRest = 40f, Crewed = Dolly },
            new() { Name = "Side track", Kind = RigKind.Rail, A = Sp(-track, 1.7f, 4f), B = Sp(-track, 1.7f, trackEnd), Target = _ => deckFront, FovRest = 35f, Crewed = Dolly },
            new()
            {
                Name = "Wire cam", Kind = RigKind.Wire, P = new Vector3(0, 16f, 0),
                BoxMin = new Vector3(-hu, 7f, -hv), BoxMax = new Vector3(hu, 26f, hv), Target = onArtist, FovRest = 55f,
            },
            new() { Name = "Jib", Kind = RigKind.Jib, A = Sp(hw * 0.55f, 0f, front + 7f), B = Sp(0, 0, front - 1f) - Sp(hw * 0.55f, 0f, front + 7f), Arm = 9f, Target = onArtist, FovRest = 42f, Crewed = 1f },
            // the steadicam works the deck's front corner, the crowd camera on the other: never between the artist and the crowd
            new() { Name = "Steadicam", Kind = RigKind.Steadicam, A = Sp(hw * 0.72f, h + 1.6f, plan.Round ? hw * 0.6f : -3f), BoxMax = new Vector3(Math.Min(2.5f, hw * 0.25f), 0, 0), Target = onArtist, FovRest = 60f, Crewed = 1.6f },
            new() { Name = "Crowd", Kind = RigKind.Fixed, A = Sp(-(hw - 1.2f), h + 2.2f, plan.Round ? hw - 1.2f : -0.8f), Target = _ => Sp(0, 1.5f, 25f), FovRest = 70f },
            new() { Name = "Wide FOH", Kind = RigKind.Fixed, A = fohDeck + lift + across * 0.7f, Target = _ => Sp(0, h + 4f, -d * 0.5f), FovRest = 50f, Crewed = Tripod },
            // on its tower out on the floor; with no room for one, on the front of house platform's middle
            new() { Name = "Wide far stand", Kind = RigKind.Fixed, A = (plan.FarCamera ?? fohDeck) + lift, Target = _ => Sp(0, h + 3f, -d * 0.5f), FovRest = 45f, Crewed = Tripod },
            new()
            {
                Name = "Drone", Kind = RigKind.Drone, A = Vector3.Zero, P = new Vector3(120f, 70f, 0),
                BoxMax = new Vector3(120f, 45f, 110f), Target = _ => new Vector3(0, 5f, 0), FovRest = 55f,
            },
            // the artist's own head-cam: nothing to see of it
            new() { Name = "Artist POV", Kind = RigKind.Fixed, A = artist + Sp(0, 0, 0.4f) - Sp(0, 0, 0), Target = _ => Sp(0, 2f, 30f), FovRest = 75f, Hidden = true },
        };
        foreach (var r in list)
        {
            r.Fov = r.FovRest;
            if (r.Kind == RigKind.Steadicam) r.P = r.A;
        }
        return list;
    }
}
