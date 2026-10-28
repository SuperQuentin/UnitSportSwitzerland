using Godot;
using UnitSport.Avatar;
using J = UnitSport.Avatar.HumanMeshBuilder.Joint;

namespace UnitSport.Interiors.Patrons;

/// <summary>
/// A patron's little loops (#1244): a seated or standing figure's joints (<see cref="SeatedFigure.Joints"/>,
/// <see cref="HumanMeshBuilder.PoseJoints(HumanPose)"/>) with the arms and the head moved over them, a pure function
/// of the time and the person, so a figure needs no state of its own. Author space: +Z ahead, the figure's own right
/// hand on rig-L (−X); a seated figure's origin is its hip, a standing one's its feet. Dancers dance the avatar's own
/// moves (<see cref="HumanMeshBuilder.PoseStride"/>).
/// </summary>
public static class PatronPoser
{
    private const float Upper = 0.270f, Fore = 0.245f, Reach = 0.50f;
    /// <summary>The beat the dancers keep (bpm): a club's house.</summary>
    private const double Bpm = 124;

    private static readonly Vector3[] Standing = HumanMeshBuilder.PoseJoints(HumanPose.Standing);
    private static readonly Dictionary<int, Vector3[]> Seated = new();
    [ThreadStatic] private static Vector3[]? _buf;

    /// <summary>A seat's joints, its floor <paramref name="floorBelowHip"/> m under the hip, cached by the centimetre.</summary>
    private static Vector3[] SeatedJoints(float floorBelowHip)
    {
        int key = (int)MathF.Round(floorBelowHip * 100);
        if (!Seated.TryGetValue(key, out var j))
            Seated[key] = j = SeatedFigure.Joints(new SeatAnchor(0, Vector3.Zero, 0.1f, -key / 100f));
        return j;
    }

    /// <summary>Poses <paramref name="f"/> as the patron of <paramref name="spot"/> at piece <paramref name="piece"/>, person <paramref name="who"/>, at <paramref name="t"/> s.</summary>
    public static void Pose(SkinnedFigure f, in PatronSpot spot, FurnitureType piece, int who, double t, float cheer = 0f)
    {
        float s = who / 997f;
        if (spot.Activity == PatronActivity.Dance)
        {
            double beats = t * Bpm / 60 + s * 4;
            int bar = (int)Math.Floor(beats / 4);
            var dance = new DanceParams(Audio.Cd.MusicStyle.Electronic, (int)(PatronSchedule.Mix((uint)who, (uint)(bar / 8), 5) % 9),
                (float)(beats - Math.Floor(beats)), (float)(beats / 4 - bar), bar, 1f);
            HumanMeshBuilder.PoseStride(f, 0f, 0f, dance: dance);
            return;
        }
        var j = _buf ??= new Vector3[Standing.Length];
        (spot.Pose == PatronPose.Stand ? Standing : SeatedJoints(spot.At.Y - spot.Floor)).CopyTo(j, 0);
        Move(j, spot.Activity, piece, spot.Place, s, t, cheer);
        HumanMeshBuilder.PoseJoints(f, j);
    }

    private static void Move(Vector3[] j, PatronActivity a, FurnitureType piece, int place, float s, double t, float cheer)
    {
        // both hands forward onto the table or the controls, before anything moves them
        var sl = j[(int)J.ShoulderL];
        var sr = j[(int)J.ShoulderR];
        var restL = sl + new Vector3(0.06f, -0.27f, 0.30f);
        var restR = sr + new Vector3(-0.06f, -0.27f, 0.30f);
        var mouth = j[(int)J.Neck] + new Vector3(0, 0.10f, 0.14f);
        double period = 6 + 4 * s, local = (t + s * 40) % period;
        switch (a)
        {
            case PatronActivity.Dine or PatronActivity.Snack:
            {
                // the fork (the right hand) up to the mouth now and then, cutting between, the head over the plate
                float lift = Bump(local, 0, 0.7, 1.4, 2.1);
                var cut = new Vector3(0.02f * MathF.Sin((float)t * 11 + s * 9), 0, 0);
                Arm(j, true, restL.Lerp(mouth + new Vector3(-0.03f, 0, 0), lift) + cut * (1 - lift));
                Arm(j, false, a == PatronActivity.Snack ? restR.Lerp(mouth + new Vector3(0.04f, -0.02f, 0), lift) : restR - cut);
                Talk(j, t, s, 0.6f);
                Head(j, 0.18f * (1 - lift), 0);
                break;
            }
            case PatronActivity.Coffee or PatronActivity.Drink:
            {
                // the cup or glass up for a sip, the other hand talking
                float sip = Bump(local, 0, 0.8, 2.0, 2.8);
                Arm(j, true, restL.Lerp(mouth + new Vector3(-0.02f, 0.01f, 0.02f), sip));
                float talk = Talking(t, s);
                Arm(j, false, restR.Lerp(Gesture(j, t, s), talk * (1 - sip)));
                Talk(j, t, s, 1f - sip);
                Head(j, -0.2f * sip, 0);
                break;
            }
            case PatronActivity.Watch:
            {
                // hands in the lap, a handful of popcorn now and then; a laugh lifts the head
                double p = 12 + 7 * s, k = (t + s * 70) % p;
                float bite = Bump(k, 0, 0.6, 1.2, 1.8);
                if (bite > 0) Arm(j, true, Lerp(j[(int)J.WristL], mouth, bite));
                Head(j, -0.08f * Bump((t + s * 13) % 31, 0, 0.3, 1.5, 2.2), 0.05f * MathF.Sin((float)t * 0.3f + s * 7));
                break;
            }
            case PatronActivity.Arcade:
                Arcade(j, piece, place, s, t);
                break;
            case PatronActivity.Queue:
            {
                // waiting: the phone out for a while, looking about otherwise
                float phone = Bump((t + s * 60) % (14 + 9 * s), 0, 0.8, 5.5, 6.3);
                if (phone > 0)
                {
                    var at = j[(int)J.Chest] + new Vector3(-0.02f, 0.02f, 0.26f);
                    Arm(j, true, Lerp(j[(int)J.WristL], at, phone));
                    Arm(j, false, Lerp(j[(int)J.WristR], at + new Vector3(0.06f, 0, 0), phone));
                }
                Head(j, 0.4f * phone, 0.12f * MathF.Sin((float)t * 0.5f + s * 11) * (1 - phone));
                break;
            }
            case PatronActivity.Settee:
            {
                // talking on the settee, both arms up when a ball goes in (cheer 0..1)
                float talk = Talking(t, s);
                Arm(j, false, Lerp(j[(int)J.WristR], Gesture(j, t, s), talk));
                Talk(j, t, s, 1f);
                if (cheer > 0)
                {
                    float wave = 0.06f * MathF.Sin((float)t * 14);
                    Arm(j, true, Lerp(j[(int)J.WristL], sl + new Vector3(-0.12f + wave, 0.47f, 0.08f), cheer));
                    Arm(j, false, Lerp(j[(int)J.WristR], sr + new Vector3(0.12f - wave, 0.47f, 0.08f), cheer));
                    Head(j, -0.3f * cheer, 0);
                }
                break;
            }
            case PatronActivity.Pool:
                Pool(j, place, s, t);
                break;
            case PatronActivity.Darts:
                Darts(j, place, s, t);
                break;
            case PatronActivity.TableFootball:
            {
                // bent over the box, a hand on a rod each, spinning and pushing
                Lean(j, 0.32f);
                float push = 0.04f * MathF.Sin((float)t * 7 + s * 5), spin = 0.03f * MathF.Sin((float)t * 13 + s * 3);
                Arm(j, true, new Vector3(-0.25f + push, 0.92f + spin, 0.36f));
                Arm(j, false, new Vector3(0.25f - push, 0.92f - spin, 0.36f));
                Head(j, 0.3f, 0);
                break;
            }
        }
    }

    /// <summary>A cabinet's controls, a pinball's flippers, a racer's wheel, a claw's stick, the drums, the hoop.</summary>
    private static void Arcade(Vector3[] j, FurnitureType piece, int place, float s, double t)
    {
        var sl = j[(int)J.ShoulderL];
        var sr = j[(int)J.ShoulderR];
        float ft = (float)t;
        switch (piece)
        {
            case FurnitureType.Racer:
            {
                // a wheel 0.17 m across before the chest, turned through the bends
                float steer = 0.5f * MathF.Sin(ft * 0.9f + s * 8) + 0.15f * MathF.Sin(ft * 2.7f + s * 3);
                var c = (sl + sr) / 2 + new Vector3(0, -0.12f, 0.40f);
                Arm(j, true, c + new Vector3(-0.17f * MathF.Cos(steer), 0.17f * MathF.Sin(steer), 0));
                Arm(j, false, c + new Vector3(0.17f * MathF.Cos(steer), -0.17f * MathF.Sin(steer), 0));
                Head(j, 0, -0.15f * steer);
                break;
            }
            case FurnitureType.Pinball:
            {
                // a hand at each flipper button, tapping
                Arm(j, true, new Vector3(-0.27f, 0.88f + 0.025f * Tap(ft * 3.1f + s * 9), 0.30f));
                Arm(j, false, new Vector3(0.27f, 0.88f + 0.025f * Tap(ft * 2.6f + s * 4), 0.30f));
                Lean(j, 0.12f);
                Head(j, 0.35f, 0);
                break;
            }
            case FurnitureType.DrumGame:
            {
                // sticks on the two drums, on the beat
                float bl = Tap(ft * 4.1f + s * 7), br = Tap(ft * 4.1f + 1.6f + s * 7);
                Arm(j, true, new Vector3(-0.2f, 0.82f + 0.12f * bl, 0.32f));
                Arm(j, false, new Vector3(0.2f, 0.82f + 0.12f * br, 0.32f));
                Head(j, 0.1f + 0.06f * MathF.Sin(ft * 8.6f), 0);
                break;
            }
            case FurnitureType.BasketballGame:
            {
                // a ball shot every second and a half: low, up, out
                double k = (t + s * 5) % 1.5;
                float up = Bump(k, 0, 0.5, 0.65, 0.9);
                var low = new Vector3(0, 1.0f, 0.3f);
                var high = new Vector3(0, 1.75f, 0.32f);
                Arm(j, true, low.Lerp(high, up) + new Vector3(-0.08f, 0, 0));
                Arm(j, false, low.Lerp(high, up) + new Vector3(0.08f, 0, 0));
                Head(j, -0.25f, 0);
                break;
            }
            case FurnitureType.AirHockey:
            {
                // the mallet swept about the near half
                var at = new Vector3(0.18f * MathF.Sin(ft * 2.3f + s * 6), 0.85f, 0.34f + 0.08f * MathF.Sin(ft * 3.7f + s * 2));
                Arm(j, true, at);
                Lean(j, 0.25f);
                Head(j, 0.3f, 0);
                break;
            }
            default:
            {
                // a cabinet or a claw: the stick in the left hand circling, the right tapping buttons
                float r = piece == FurnitureType.ClawMachine ? 0.02f : 0.035f;
                Arm(j, false, new Vector3(0.14f + r * MathF.Cos(ft * 5.5f + s * 3), 1.02f, 0.36f + r * MathF.Sin(ft * 4.3f + s * 5)));
                Arm(j, true, new Vector3(-0.08f, 1.0f + 0.02f * Tap(ft * 6.3f + s * 8), 0.36f));
                Head(j, 0.05f, 0);
                break;
            }
        }
    }

    /// <summary>A pool table: the one at the foot bends to its shot, strokes and plays; the other waits on its cue.</summary>
    private static void Pool(Vector3[] j, int place, float s, double t)
    {
        double k = (t + s * 30) % 14;
        if (place == 0)
        {
            float bend = Bump(k, 0, 1.2, 9.0, 10.5);
            Lean(j, 1.0f * bend);
            float stroke = k is > 3 and < 8.4 ? 0.12f * MathF.Sin((float)(k - 3) * 2.4f) : 0f;
            if (k is >= 8.4 and < 8.8) stroke = 0.2f;
            // the bridge hand flat on the cloth, the cue hand back by the hip
            var bridge = new Vector3(0.08f, 0.86f, 0.62f);
            var cue = new Vector3(-0.12f, 0.96f, 0.02f + stroke);
            Arm(j, false, Lerp(j[(int)J.WristR], bridge, bend));
            Arm(j, true, Lerp(j[(int)J.WristL], cue, bend));
            Head(j, 0.35f * bend, 0);
        }
        else
        {
            // both hands on the upright cue before the chest
            Arm(j, true, new Vector3(-0.04f, 1.12f, 0.22f));
            Arm(j, false, new Vector3(0.04f, 1.0f, 0.22f));
            Head(j, 0.1f, 0.06f * MathF.Sin((float)t * 0.4f + s * 4));
        }
    }

    /// <summary>Three darts and a pause; the one waiting sips.</summary>
    private static void Darts(Vector3[] j, int place, float s, double t)
    {
        var sl = j[(int)J.ShoulderL];
        if (place != 0)
        {
            Move(j, PatronActivity.Drink, FurnitureType.HighTable, 0, s, t, 0);
            return;
        }
        double k = (t + s * 20) % 12;
        if (k > 7.5) return;
        double d = k % 2.5;
        // aim by the face, a flick out, back
        var aim = sl + new Vector3(0.06f, 0.14f, 0.16f);
        var out_ = sl + new Vector3(0.02f, 0.10f, 0.48f);
        float flick = Bump(d, 1.4, 1.6, 1.7, 2.1);
        Arm(j, true, aim.Lerp(out_, flick));
        Head(j, -0.05f, 0);
    }

    // —— pieces ——

    /// <summary>0 before <paramref name="a"/>, up to 1 by <paramref name="b"/>, held to <paramref name="c"/>, down to 0 by <paramref name="d"/> (eased).</summary>
    private static float Bump(double x, double a, double b, double c, double d)
    {
        if (x <= a || x >= d) return 0f;
        double u = x < b ? (x - a) / (b - a) : x <= c ? 1 : 1 - (x - c) / (d - c);
        return (float)(u * u * (3 - 2 * u));
    }

    /// <summary>A quick press: 0 most of the time, 1 at each beat.</summary>
    private static float Tap(float x)
    {
        float f = x - MathF.Floor(x);
        return f < 0.25f ? MathF.Sin(f * 4 * MathF.PI) : 0f;
    }

    private static Vector3 Lerp(Vector3 a, Vector3 b, float u) => a.Lerp(b, u);

    /// <summary>How much this person is talking now, 0..1: spells of a few seconds.</summary>
    private static float Talking(double t, float s)
    {
        float x = MathF.Sin((float)t * 0.45f + s * 17) + 0.5f * MathF.Sin((float)t * 1.1f + s * 5);
        return Math.Clamp((x - 0.2f) * 2, 0, 1);
    }

    /// <summary>The left hand's talking loop before the chest.</summary>
    private static Vector3 Gesture(Vector3[] j, double t, float s)
    {
        float a = (float)t * 2.4f + s * 9;
        return j[(int)J.Chest] + new Vector3(0.12f + 0.05f * MathF.Cos(a), -0.08f + 0.05f * MathF.Sin(a * 1.3f), 0.28f);
    }

    /// <summary>Nods and tilts while talking, as much as <paramref name="weight"/>.</summary>
    private static void Talk(Vector3[] j, double t, float s, float weight)
    {
        float talk = Talking(t, s) * weight;
        if (talk <= 0) return;
        Head(j, 0.08f * talk * MathF.Sin((float)t * 5.2f + s * 3), 0.12f * talk * MathF.Sin((float)t * 0.7f + s * 13));
    }

    /// <summary>The head tipped forward by <paramref name="pitch"/> and to the side by <paramref name="roll"/> (rad) about its base.</summary>
    private static void Head(Vector3[] j, float pitch, float roll)
    {
        if (pitch == 0 && roll == 0) return;
        var b = j[(int)J.HeadBase];
        var axis = j[(int)J.HeadTop] - b;
        var turned = new Basis(Vector3.Forward, roll) * (new Basis(Vector3.Right, pitch) * axis);
        j[(int)J.HeadTop] = b + turned;
    }

    /// <summary>The upper body bent forward over the hips by <paramref name="angle"/> (rad); the arms are set after.</summary>
    private static void Lean(Vector3[] j, float angle)
    {
        if (angle == 0) return;
        var pivot = j[(int)J.Hip];
        var turn = new Basis(Vector3.Right, angle);
        foreach (var k in UpperBody) j[(int)k] = pivot + turn * (j[(int)k] - pivot);
    }

    private static readonly J[] UpperBody =
    {
        J.HeadTop, J.HeadBase, J.Neck, J.Chest, J.Waist, J.ShoulderL, J.ElbowL, J.WristL, J.ShoulderR, J.ElbowR, J.WristR,
    };

    /// <summary>
    /// An arm reaching for <paramref name="wrist"/> (clamped to its reach): the elbow solved from the two bones, bent
    /// down and out. <paramref name="right"/> is the figure's own right hand (rig-L, −X).
    /// </summary>
    private static void Arm(Vector3[] j, bool right, Vector3 wrist)
    {
        var sh = j[(int)(right ? J.ShoulderL : J.ShoulderR)];
        var to = wrist - sh;
        float dist = to.Length();
        if (dist > Reach) { to *= Reach / dist; dist = Reach; }
        if (dist < 0.05f) { to = new Vector3(0, -0.05f, 0); dist = 0.05f; }
        var dir = to / dist;
        float along = (Upper * Upper - Fore * Fore + dist * dist) / (2 * dist);
        float up = MathF.Sqrt(MathF.Max(0, Upper * Upper - along * along));
        // the elbow hangs down and a little out to its side, square to the reach
        var pole = new Vector3(right ? -0.35f : 0.35f, -1f, -0.2f);
        var bend = (pole - dir * pole.Dot(dir)).Normalized();
        j[(int)(right ? J.ElbowL : J.ElbowR)] = sh + dir * along + bend * up;
        j[(int)(right ? J.WristL : J.WristR)] = sh + to;
    }
}
