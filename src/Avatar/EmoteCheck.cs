using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// <c>--emotecheck</c> (#404): the emote wheel's catalog holds together (a name per emote, every
/// one on a page, every <c>DanceId</c> it can send distinct), and every emote builds a figure at
/// every point of two bars, standing and walking, and flowing into the next one, with no vertex
/// off at infinity or out of a 1.3 m reach of the feet. Each emote must move the figure and be a
/// pose of its own, so a wiring slip (two entries on one move, an emote drawing nothing) shows.
/// Headless, no scene.
/// </summary>
public static class EmoteCheck
{
    public static int Run()
    {
        int failed = 0;
        void Fail(string why)
        {
            failed++;
            GD.Print($"[emotecheck] FAIL {why}");
        }

        // EMOTE_ONLY=<text>: only the continuity check, only the moves whose name holds the text (a debugging aid)
        string only = OS.GetEnvironment("EMOTE_ONLY");
        if (only != "")
        {
            int jumps = Continuity(only);
            GD.Print($"[emotecheck] RESULT: {(jumps == 0 ? "ok" : "FAILED")} (continuity of '{only}' only)");
            return jumps == 0 ? 0 : 1;
        }

        int count = HumanMeshBuilder.EmoteCount;
        if (count > HumanMeshBuilder.EmotePages.Length * HumanMeshBuilder.EmotesPerPage)
            Fail($"{count} emotes do not fit on {HumanMeshBuilder.EmotePages.Length} pages");
        var names = new HashSet<string>();
        for (int i = 0; i < count; i++)
            if (!names.Add(HumanMeshBuilder.EmoteName(i))) Fail($"emote {i}: no name, or a name used twice");

        var palette = HumanPalette.ForRider(2);
        var rest = Vertices(HumanMeshBuilder.BuildStride(palette, 0f, 0f));
        var signatures = new List<(int Emote, float[] Sig)>();
        int built = 0;
        for (int i = 0; i < count; i++)
        {
            int move = HumanMeshBuilder.EmoteMoves + i;
            int next = HumanMeshBuilder.EmoteMoves + (i + 1) % count;
            float moved = 0f;
            var sig = new float[8];
            for (int f = 0; f < 32; f++)
            {
                float bars = f / 16f;
                var dance = new DanceParams(Audio.Cd.MusicStyle.Pop, move, (bars * 4f) % 1f, bars % 1f, (int)bars, 1f);
                foreach (float speed in new[] { 0f, 1.4f })
                {
                    var v = Vertices(HumanMeshBuilder.BuildStride(palette, speed, bars % 1f, dance: dance));
                    built++;
                    if (Problem(v) is { } why) Fail($"{HumanMeshBuilder.EmoteName(i)} at bar {bars:F2}, speed {speed}: {why}");
                    if (speed == 0f && v.Length == rest.Length)
                    {
                        moved = Mathf.Max(moved, Distance(v, rest));
                        if (f % 4 == 1) sig[f / 4] = Sum(v);
                    }
                }
                // half way through the crossfade into the next emote
                var flow = dance with { Move = next, PrevMove = move, MoveBlend = 0.5f };
                built++;
                if (Problem(Vertices(HumanMeshBuilder.BuildStride(palette, 0f, 0f, dance: flow))) is { } fwhy)
                    Fail($"{HumanMeshBuilder.EmoteName(i)} flowing into the next: {fwhy}");
            }
            if (moved < 0.05f) Fail($"{HumanMeshBuilder.EmoteName(i)} hardly moves the figure ({moved:F3} m)");
            foreach (var (other, osig) in signatures)
            {
                float d = 0f;
                for (int k = 0; k < sig.Length; k++) d += Mathf.Abs(sig[k] - osig[k]);
                if (d < 1e-3f) Fail($"{HumanMeshBuilder.EmoteName(i)} is drawn exactly like {HumanMeshBuilder.EmoteName(other)}");
            }
            signatures.Add((i, sig));
        }

        // #728: every style's moves and both halves of a break set, standing and walking, and the
        // floor moves flowing in and out: no vertex lost, none through the floor (a floor move may
        // reach further out: the windmill's legs sweep round the shoulders)
        foreach (var style in System.Enum.GetValues<Audio.Cd.MusicStyle>())
        {
            int moves = HumanMeshBuilder.MoveCount(style);
            var all = new List<int>();
            for (int m = 0; m < moves; m++) all.Add(m);
            // #2055: the style's whole generated pool, each flowing in from the one before it (the first from the last table move)
            for (int m = 0; m < HumanMeshBuilder.GeneratedCount(style); m++) all.Add(HumanMeshBuilder.GeneratedMoves + m);
            if (style == Audio.Cd.MusicStyle.HipHop)
                all.AddRange(new[] { HumanMeshBuilder.BreakDown, HumanMeshBuilder.BreakPowerWindmill, HumanMeshBuilder.BreakPowerHeadspin });
            for (int k = 0; k < all.Count; k++)
            {
                int move = all[k];
                bool floor = move >= HumanMeshBuilder.BreakMoves && move < HumanMeshBuilder.GeneratedMoves;
                int prev = move == HumanMeshBuilder.BreakDown ? 0 : floor ? HumanMeshBuilder.BreakDown : all[(k + all.Count - 1) % all.Count];
                float torso = float.MaxValue, head = float.MaxValue, hands = float.MaxValue;
                for (int f = 0; f < 32; f++)
                {
                    float bars = f / 16f;
                    var dance = new DanceParams(style, move, (bars * 4f) % 1f, bars % 1f, (int)bars, 1f,
                        f < 2 ? prev : -1, f < 2 ? 0.5f : 1f);
                    var clearance = HumanMeshBuilder.Clearance(dance);
                    torso = Mathf.Min(torso, clearance.Torso);
                    head = Mathf.Min(head, clearance.Head);
                    hands = Mathf.Min(hands, clearance.Hands);
                    foreach (float speed in new[] { 0f, 1.4f })
                    {
                        built++;
                        if (Problem(Vertices(HumanMeshBuilder.BuildStride(palette, speed, bars % 1f, dance: dance)), floor ? 1.9f : 1.3f) is { } why)
                            Fail($"{style} {HumanMeshBuilder.MoveName(style, move)} at bar {bars:F2}, speed {speed}: {why}");
                    }
                    // #728: the music layer at its extremes — a loud peak on the kick with the melody
                    // at its top and a long note — still keeps every joint in reach and off the floor
                    built++;
                    var peak = dance with { Level = 1f, Kick = 1f, Section = 2, Bpm = 150f, Rise = 1f, Hold = 1f };
                    var peakClearance = HumanMeshBuilder.Clearance(peak);
                    torso = Mathf.Min(torso, peakClearance.Torso);
                    head = Mathf.Min(head, peakClearance.Head);
                    hands = Mathf.Min(hands, peakClearance.Hands);
                    if (Problem(Vertices(HumanMeshBuilder.BuildStride(palette, 0f, 0f, dance: peak)), floor ? 1.9f : 1.3f) is { } pwhy)
                        Fail($"{style} {HumanMeshBuilder.MoveName(style, move)} with the music layer at bar {bars:F2}: {pwhy}");
                }
                // #728: hands must not pass through the torso, the head or each other. Floor moves are
                // excluded: a six-step and a headspin put the hands on the floor beside the body.
                if (!floor && (torso < 0.21f || head < 0.14f || hands < 0.085f))
                    Fail($"{style} {HumanMeshBuilder.MoveName(style, move)}: clearance torso {torso:F2} (>= 0.21), "
                        + $"head {head:F2} (>= 0.14), hands {hands:F2} (>= 0.085)");
            }
        }

        // #907: every aura pose over its whole progress, standing and walking, and flowing from the
        // waiting stance: no vertex lost, none through the floor; each moves the figure and no two match
        var auraPoses = new List<int>();
        foreach (var s in System.Enum.GetValues<Combat.AuraStance>())
            if (s is not Combat.AuraStance.None and < Combat.AuraStance.MoveBase) auraPoses.Add((int)s);
        foreach (var m in Combat.AuraRules.Moves) auraPoses.Add(Combat.AuraRules.PoseOf(m));
        var auraSigs = new List<(int Pose, float[] Sig)>();
        foreach (int pose in auraPoses)
        {
            int move = HumanMeshBuilder.AuraMoves + pose;
            float moved = 0f;
            var sig = new float[8];
            for (int f = 0; f < 32; f++)
            {
                float r = f / 32f;
                var dance = new DanceParams(Audio.Cd.MusicStyle.Pop, move, (r * 8f) % 1f, r, 0, 1f,
                    f < 2 ? HumanMeshBuilder.AuraMoves + (int)Combat.AuraStance.Waiting : -1, f < 2 ? 0.5f : 1f);
                foreach (float speed in new[] { 0f, 1.4f })
                {
                    var v = Vertices(HumanMeshBuilder.BuildStride(palette, speed, r, dance: dance));
                    built++;
                    if (Problem(v) is { } why) Fail($"aura pose {pose} at {r:F2}, speed {speed}: {why}");
                    if (speed == 0f && v.Length == rest.Length)
                    {
                        moved = Mathf.Max(moved, Distance(v, rest));
                        if (f % 4 == 1) sig[f / 4] = Sum(v);
                    }
                }
            }
            if (moved < 0.05f) Fail($"aura pose {pose} hardly moves the figure ({moved:F3} m)");
            foreach (var (other, osig) in auraSigs)
            {
                float d = 0f;
                for (int k = 0; k < sig.Length; k++) d += Mathf.Abs(sig[k] - osig[k]);
                if (d < 1e-3f) Fail($"aura pose {pose} is drawn exactly like {other}");
            }
            auraSigs.Add((pose, sig));
        }

        failed += Continuity();

        int generated = 0;
        foreach (var gs in System.Enum.GetValues<Audio.Cd.MusicStyle>()) generated += HumanMeshBuilder.GeneratedCount(gs);
        GD.Print(failed == 0
            ? $"[emotecheck] RESULT: ok ({count} emotes, {generated} generated moves, {auraPoses.Count} aura poses, {built} figures)"
            : $"[emotecheck] RESULT: FAILED — {failed} problems");
        return failed == 0 ? 0 : 1;
    }

    // ---- continuity (#2055): no joint teleports -------------------------------------------

    /// <summary>
    /// The most a joint may move in 1/64 of a beat, m: 6.4 m per beat, 21 m/s at 200 bpm. The hand-written moves' real hits
    /// (air guitar's strum, the robot's snaps) peak at 0.10 to 0.12; everything above that in them is an elbow flipping or a
    /// gesture swapping kinds mid-crossfade, listed in <see cref="KnownJumps"/>.
    /// </summary>
    private const float StepLimit = 0.10f;

    private static readonly string[] JointNames = { "head", "hip", "wrist L", "wrist R", "elbow L", "elbow R", "knee L", "knee R", "ankle L", "ankle R", "toe L", "toe R" };
    private static readonly int[] JointIndex = { 0, 5, 8, 11, 7, 10, 13, 17, 14, 18, 15, 19 };

    /// <summary>
    /// Hand-written moves that already jump on <c>main</c> (known, reported, not failed): the allowance in m per 1/64 beat.
    /// Generated moves have none.
    /// </summary>
    private static readonly Dictionary<string, float> KnownJumps = new()
    {
        ["Chill ArmWave"] = 0.18f,
        ["Chill BodyRoll"] = 0.17f,
        ["Chill SwimDance"] = 0.13f,
        ["Electronic ArmWave"] = 0.18f,
        ["Electronic Robot"] = 0.13f,
        ["Electronic Sprinkler"] = 0.12f,
        ["Folk ChickenDance"] = 0.12f,
        ["Folk GangnamStyle"] = 0.14f,
        ["Folk Macarena"] = 0.37f,
        ["HipHop BodyRoll"] = 0.17f,
        ["HipHop Dab"] = 0.35f,
        ["HipHop Floss"] = 0.18f,
        ["HipHop Griddy"] = 0.20f,
        ["Pop BodyRoll"] = 0.17f,
        ["Pop Carlton"] = 0.31f,
        ["Pop GangnamStyle"] = 0.27f,
        ["Pop Macarena"] = 0.37f,
        ["Pop OrangeJustice"] = 0.29f,
        ["Pop Ymca"] = 0.14f,
        ["RatDance RatArmPump"] = 0.19f,
        ["RatDance RatSwing"] = 0.19f,
        ["Rock AirDrums"] = 0.41f,
        ["Rock AirGuitar"] = 0.11f,
        ["Rock ArmWave"] = 0.18f,
    };

    /// <summary>
    /// Every style's moves (the table's and the generated pool's, not the floor sets), standing and walking,
    /// stepped in 1/64 beat over a whole loop and across the crossfade from the move before: no joint (head,
    /// hip, wrists, elbows, knees, ankles, toes) may move more than <see cref="StepLimit"/> in a step.
    /// Prints the worst of each kind; returns the number of failures.
    /// </summary>
    private static int Continuity(string only = "")
    {
        int failures = 0;
        var worstGen = (D: 0f, Why: "");
        var worstTable = (D: 0f, Why: "");
        var known = new List<string>();
        foreach (var style in System.Enum.GetValues<Audio.Cd.MusicStyle>())
        {
            var all = new List<int>();
            for (int m = 0; m < HumanMeshBuilder.MoveCount(style); m++) all.Add(m);
            for (int m = 0; m < HumanMeshBuilder.GeneratedCount(style); m++) all.Add(HumanMeshBuilder.GeneratedMoves + m);
            for (int k = 0; k < all.Count; k++)
            {
                int move = all[k], prev = all[(k + all.Count - 1) % all.Count];
                bool gen = move >= HumanMeshBuilder.GeneratedMoves;
                string name = $"{style} {HumanMeshBuilder.MoveName(style, move)}";
                if (only != "" && !name.Contains(only)) continue;
                var worst = (D: 0f, Why: "");
                foreach (float speed in new[] { 0f, 1.4f })
                {
                    // a whole loop in the slot's own clock, then the crossfade in from the move before
                    var last = HumanMeshBuilder.DanceJoints(Frame(style, move, 0f, -1, 1f), speed);
                    for (int i = 1; i <= 16 * 64; i++)
                    {
                        float t = i / 64f;
                        var j = HumanMeshBuilder.DanceJoints(Frame(style, move, t, -1, 1f), speed);
                        Step(last, j, $"{name} speed {speed} beat {t:F3}", ref worst);
                        last = j;
                    }
                    // the clock keeps running over the change: the mix from the move before, a beat of it (it is complete in 0.9)
                    last = HumanMeshBuilder.DanceJoints(Frame(style, move, 4f, prev, 0f), speed);
                    for (int i = 1; i <= 64; i++)
                    {
                        float t = 4f + i / 64f, into = t - 4f;
                        var d = Frame(style, move, t, prev, Mathf.Min(1f, into / 0.9f));
                        var j = HumanMeshBuilder.DanceJoints(d, speed);
                        Step(last, j, $"{name} crossfade from the move before, speed {speed}, {into:F3} beats in", ref worst);
                        last = j;
                    }
                }
                ref var kind = ref (gen ? ref worstGen : ref worstTable);
                if (worst.D > kind.D) kind = worst;
                if (only != "" && worst.D > 0f) Trace(style, move, prev, worst.Why);
                float allowed = KnownJumps.TryGetValue(name, out float a) ? a : StepLimit;
                if (worst.D > StepLimit) known.Add($"{name}: {worst.D:F3} m ({worst.Why})");
                if (worst.D > allowed)
                {
                    failures++;
                    GD.Print($"[emotecheck] FAIL jump {worst.D:F3} m in 1/64 beat (limit {allowed:F3}): {worst.Why}");
                }
            }
        }
        GD.Print($"[emotecheck] continuity: worst generated step {worstGen.D:F3} m ({worstGen.Why}); worst hand-written {worstTable.D:F3} m ({worstTable.Why}); limit {StepLimit:F3}");
        foreach (var k in known) GD.Print($"[emotecheck] jump over the limit: {k}");
        return failures;
    }

    /// <summary>Prints the joints round the worst step of a move (the text of <see cref="Step"/>'s report names the beat).</summary>
    private static void Trace(Audio.Cd.MusicStyle style, int move, int prev, string why)
    {
        // "... speed S beat B" or "... speed S, X beats in"
        float speed = why.Contains("speed 1") ? 1.4f : 0f;
        int at = why.LastIndexOf(" beat ");
        bool cross = why.Contains("crossfade");
        float t;
        if (cross) { int a = why.LastIndexOf(", ") + 2; t = 4f + float.Parse(why.Substring(a, why.IndexOf(" beats in") - a)); }
        else t = float.Parse(why.Substring(at + 6));
        for (int i = -3; i <= 2; i++)
        {
            float tt = t + i / 64f, into = tt - 4f;
            var d = !cross ? Frame(style, move, tt, -1, 1f) : into < 0f ? Frame(style, prev, tt, -1, 1f) : Frame(style, move, tt, prev, Mathf.Min(1f, into / 0.9f));
            var j = HumanMeshBuilder.DanceJoints(d, speed);
            GD.Print($"[emotecheck]   {tt:F3} shL {Fmt(j[6])} elL {Fmt(j[7])} wrL {Fmt(j[8])} | shR {Fmt(j[9])} elR {Fmt(j[10])} wrR {Fmt(j[11])} | hip {Fmt(j[5])} head {Fmt(j[0])}");
        }
    }

    private static string Fmt(Vector3 v) => $"({v.X:F2},{v.Y:F2},{v.Z:F2})";

    private static DanceParams Frame(Audio.Cd.MusicStyle style, int move, float beats, int prev, float blend) =>
        new(style, move, beats % 1f, (beats / 4f) % 1f, (int)(beats / 4f), 1f, prev, blend);

    private static void Step(Vector3[] a, Vector3[] b, string where, ref (float D, string Why) worst)
    {
        for (int k = 0; k < JointIndex.Length; k++)
        {
            float d = a[JointIndex[k]].DistanceTo(b[JointIndex[k]]);
            if (d > worst.D) worst = (d, $"{JointNames[k]}, {where}");
        }
    }

    private static Vector3[] Vertices(ArrayMesh mesh) =>
        mesh.GetSurfaceCount() == 0 ? System.Array.Empty<Vector3>()
            : mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();

    private static string? Problem(Vector3[] vertices, float reach = 1.3f)
    {
        if (vertices.Length == 0) return "no surface";
        foreach (var v in vertices)
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)
                || Mathf.Abs(v.X) > reach || Mathf.Abs(v.Z) > reach || v.Y < -0.05f || v.Y > 2.7f)
                return $"a vertex at {v}";
        return null;
    }

    /// <summary>The largest distance any vertex is from where it stands at rest.</summary>
    private static float Distance(Vector3[] a, Vector3[] b)
    {
        float max = 0f;
        for (int i = 0; i < a.Length; i++) max = Mathf.Max(max, a[i].DistanceTo(b[i]));
        return max;
    }

    private static float Sum(Vector3[] v)
    {
        float s = 0f;
        for (int i = 0; i < v.Length; i++) s += v[i].X * 0.7f + v[i].Y + v[i].Z * 1.3f;
        return s;
    }
}
