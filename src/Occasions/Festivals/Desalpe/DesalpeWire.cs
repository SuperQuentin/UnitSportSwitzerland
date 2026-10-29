// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-desalpe.md).

using System.Buffers.Binary;

namespace UnitSport.Occasions.Festivals.Desalpe;

/// <summary>One cow as a client draws it: metres east and north of the fair, the Godot yaw it faces, how fast it walks 0..1, its state.</summary>
public readonly record struct CowView(float X, float Y, float Yaw, float Gait, CowState State);

/// <summary>
/// The herd's snapshot (#1269 part 7): what the server sends 8 times a second to the peers near the herd, unreliable. 3 bytes of header
/// (phase, count, the leader's progress in 1/255) and 11 bytes a cow (two floats, a yaw byte, a gait byte, the state) = 157 bytes for 14 cows,
/// about 1.3 kB/s a watching peer. Read back defensively: a short or oversized array is refused.
/// </summary>
public static class DesalpeWire
{
    public const int Header = 3, PerCow = 11;

    public static byte[] Pack(DesalpePhase phase, HerdFlock flock)
    {
        var b = new byte[Header + flock.Count * PerCow];
        b[0] = (byte)phase;
        b[1] = (byte)flock.Count;
        b[2] = (byte)Math.Clamp(flock.LeaderS / Math.Max(flock.Route.Length, 1) * 255, 0, 255);
        for (int i = 0; i < flock.Count; i++)
        {
            int at = Header + i * PerCow;
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), (float)flock.X[i]);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 4), (float)flock.Y[i]);
            b[at + 8] = (byte)(((flock.Yaw[i] / MathF.Tau) % 1f + 1f) % 1f * 255f);
            b[at + 9] = (byte)(Math.Clamp(flock.Gait[i], 0f, 1f) * 255f);
            b[at + 10] = (byte)flock.State[i];
        }
        return b;
    }

    public static bool Unpack(byte[] b, out DesalpePhase phase, out float progress, out CowView[] cows)
    {
        phase = DesalpePhase.Grazing;
        progress = 0;
        cows = [];
        if (b.Length < Header || b[1] > 64 || b.Length != Header + b[1] * PerCow || b[0] > (byte)DesalpePhase.Arrived) return false;
        phase = (DesalpePhase)b[0];
        progress = b[2] / 255f;
        cows = new CowView[b[1]];
        for (int i = 0; i < cows.Length; i++)
        {
            int at = Header + i * PerCow;
            float x = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)), y = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 4));
            if (!float.IsFinite(x) || !float.IsFinite(y) || Math.Abs(x) > 20000 || Math.Abs(y) > 20000 || b[at + 10] > (byte)CowState.Lost) return false;
            float yaw = b[at + 8] / 255f * MathF.Tau;
            if (yaw > MathF.PI) yaw -= MathF.Tau;
            cows[i] = new CowView(x, y, yaw, b[at + 9] / 255f, (CowState)b[at + 10]);
        }
        return true;
    }
}
