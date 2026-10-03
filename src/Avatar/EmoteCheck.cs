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

        GD.Print(failed == 0
            ? $"[emotecheck] RESULT: ok ({count} emotes, {built} figures)"
            : $"[emotecheck] RESULT: FAILED — {failed} problems");
        return failed == 0 ? 0 : 1;
    }

    private static Vector3[] Vertices(ArrayMesh mesh) =>
        mesh.GetSurfaceCount() == 0 ? System.Array.Empty<Vector3>()
            : mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();

    private static string? Problem(Vector3[] vertices)
    {
        if (vertices.Length == 0) return "no surface";
        foreach (var v in vertices)
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)
                || Mathf.Abs(v.X) > 1.3f || Mathf.Abs(v.Z) > 1.3f || v.Y < -0.05f || v.Y > 2.7f)
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
