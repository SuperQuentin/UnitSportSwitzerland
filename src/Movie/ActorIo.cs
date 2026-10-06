using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Movie;

/// <summary>
/// Between a <see cref="FootPlayer"/> and an <see cref="ActorState"/> (#638): what the recorder
/// reads off any player, local or remote, and what a puppet is given back. The properties are
/// read and written as C# properties, not through <c>Get</c>/<c>Set</c>, which would box a
/// Variant and copy every string 30 times a second per player.
/// </summary>
public static class ActorIo
{
    /// <summary>The change-only properties, in the order a track stores them: <see cref="FootPlayer.OnChangeProperties"/>.</summary>
    public static readonly string[] Names =
    {
        "RideKindId", "CarSetupId", "TuningBits", "DoorsOpen", "TrailerCode", "RidingWith", "SeatIndex", "DeckOn",
        "DeckSection", "HeldItemId", "ItemAction", "PoseKind", "HeadwearId", "OutfitBits", "AppearanceBits", "DanceId",
        "FightPose", "HeldRadio", "BackItemId", "CarRadio", "CarCd", "Down",
    };

    public const int RideKind = 0, RidingWith = 5;

    // a pose array per length the setter accepts (8, 11, 14, 17): handed over without allocating
    private static readonly float[][] PoseBuffers = { new float[8], new float[11], new float[14], new float[17] };

    /// <summary>The names missing on either side, for <c>--moviecheck</c>; empty when the two lists agree.</summary>
    public static string Drift()
    {
        var replicated = FootPlayer.OnChangeProperties.Select(p => p.TrimStart('.', ':')).ToHashSet();
        var missing = replicated.Except(Names).Concat(Names.Except(replicated).Select(n => "-" + n));
        return string.Join(", ", missing);
    }

    /// <summary>Player <paramref name="p"/> as it is drawn now, into <paramref name="s"/>.</summary>
    public static void Read(FootPlayer p, ActorState s)
    {
        var at = p.Global;
        s.E = at.E; s.N = at.N; s.Alt = at.Alt;
        var f = s.F;
        var v = p.WorldVelocity;
        f[Channels.Vel] = v.X; f[Channels.Vel + 1] = v.Y; f[Channels.Vel + 2] = v.Z;
        f[Channels.Yaw] = p.NetYaw;
        var a = p.Anim;
        f[Channels.Anim] = a.X; f[Channels.Anim + 1] = a.Y; f[Channels.Anim + 2] = a.Z; f[Channels.Anim + 3] = a.W;
        var d = p.DeckPos;
        f[Channels.DeckPos] = d.X; f[Channels.DeckPos + 1] = d.Y; f[Channels.DeckPos + 2] = d.Z;
        f[Channels.DeckYaw] = p.DeckYaw;
        var pose = p.NetPose;   // the owner's packing buffer, or the last one received: no copy made
        int len = Math.Min(pose.Length, Channels.MaxPose);
        f[Channels.PoseLen] = len;
        Array.Copy(pose, 0, f, Channels.Pose, len);

        var n = s.Num;
        n[0] = p.RideKindId; n[1] = p.CarSetupId; n[2] = p.TuningBits; n[3] = p.DoorsOpen; n[4] = p.TrailerCode;
        n[5] = p.RidingWith; n[6] = p.SeatIndex; n[8] = p.DeckSection; n[9] = p.HeldItemId; n[10] = p.ItemAction;
        n[11] = p.PoseKind; n[12] = p.HeadwearId; n[13] = p.OutfitBits; n[14] = p.AppearanceBits; n[15] = p.DanceId;
        n[16] = p.FightPose; n[18] = p.BackItemId; n[19] = p.CarRadio; n[21] = p.Down;
        s.Str[7] = p.DeckOn; s.Str[17] = p.HeldRadio; s.Str[20] = p.CarCd;
    }

    /// <summary>
    /// Puppet <paramref name="p"/> drawn as <paramref name="s"/>. <paramref name="ridingWith"/> is the
    /// recorded host already mapped to its puppet's name (0: none here). <paramref name="stamp"/>
    /// must differ every call: setting <c>NetTime</c> last is what applies the state.
    /// </summary>
    public static void Write(ActorState s, FootPlayer p, int ridingWith, double stamp)
    {
        var n = s.Num;
        p.RideKindId = (int)n[0]; p.CarSetupId = (int)n[1]; p.TuningBits = n[2]; p.DoorsOpen = (byte)n[3];
        p.TrailerCode = (int)n[4]; p.RidingWith = ridingWith; p.SeatIndex = (int)n[6]; p.DeckSection = (int)n[8];
        p.HeldItemId = (int)n[9]; p.ItemAction = (int)n[10]; p.PoseKind = (int)n[11]; p.HeadwearId = (int)n[12];
        p.OutfitBits = n[13]; p.AppearanceBits = (int)n[14]; p.DanceId = (int)n[15]; p.FightPose = (int)n[16];
        p.BackItemId = (int)n[18]; p.CarRadio = (int)n[19]; p.Down = (int)n[21];
        if (p.DeckOn != s.Str[7]) p.DeckOn = s.Str[7];
        if (p.HeldRadio != s.Str[17]) p.HeldRadio = s.Str[17];
        if (p.CarCd != s.Str[20]) p.CarCd = s.Str[20];

        var f = s.F;
        p.NetVel = new Vector3(f[Channels.Vel], f[Channels.Vel + 1], f[Channels.Vel + 2]);
        p.NetYaw = f[Channels.Yaw];
        p.Anim = new Vector4(f[Channels.Anim], f[Channels.Anim + 1], f[Channels.Anim + 2], f[Channels.Anim + 3]);
        p.DeckPos = new Vector3(f[Channels.DeckPos], f[Channels.DeckPos + 1], f[Channels.DeckPos + 2]);
        p.DeckYaw = f[Channels.DeckYaw];
        int len = s.PoseLength;
        int which = len switch { 8 => 0, 11 => 1, 14 => 2, 17 => 3, _ => -1 };
        if (which >= 0)
        {
            var buffer = PoseBuffers[which];
            Array.Copy(f, Channels.Pose, buffer, 0, len);
            p.NetPose = buffer;
        }
        p.NetGlobal = new GlobalPos(s.E, s.N, s.Alt);
        p.NetTime = stamp;
    }
}
