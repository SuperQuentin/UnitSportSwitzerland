namespace UnitSport.Interiors.Pinball;

/// <summary>
/// What a table shows (#1325): the backbox (scores, the player up, the ball, a message, the mission's line,
/// match, tilt) and every playfield lamp's state. Taken from the rules (<see cref="Of"/>) on whoever runs
/// them, packed for the watchers. Pure.
/// </summary>
public sealed class PinballScreen
{
    public PinballRules.Phase Phase;
    public long[] Scores = Array.Empty<long>();
    public int Up, Ball;
    public string Message = "", Mission = "";
    public int Match = -1;
    public bool Tilt, Multiball, Saving;
    /// <summary>Each lamp's <see cref="PinballRules.Lit"/>, in the table's lamp order.</summary>
    public byte[] Lamps = Array.Empty<byte>();

    public static PinballScreen Of(PinballRules r)
    {
        var s = new PinballScreen
        {
            Phase = r.State, Up = r.Up, Message = r.Message, Mission = r.MissionLine(), Match = r.MatchDigits,
            Tilt = r.Tilted, Multiball = r.Multiball, Saving = !double.IsNaN(r.SaveUntil),
            Scores = r.Players.Select(p => p.Score).ToArray(),
            Ball = r.Current?.Ball ?? 0,
            Lamps = new byte[r.Table.Lamps.Count],
        };
        for (int i = 0; i < s.Lamps.Length; i++) s.Lamps[i] = (byte)r.LampState(r.Table.Lamps[i]);
        return s;
    }

    /// <summary>A table nobody plays: attract mode, the last game's scores if any.</summary>
    public static PinballScreen Idle(PinballTable t, long[]? last = null) => new()
    {
        Phase = last is { Length: > 0 } ? PinballRules.Phase.GameOver : PinballRules.Phase.Attract,
        Scores = last ?? Array.Empty<long>(),
        Lamps = t.Lamps.Select(l => (byte)(l.Kind is PinballTable.LampKind.General or PinballTable.LampKind.BumperCap ? PinballRules.Lit.On
            : l.Kind == PinballTable.LampKind.Shot ? PinballRules.Lit.Blink : PinballRules.Lit.Off)).ToArray(),
    };

    public long ScoreUp => Up < Scores.Length ? Scores[Up] : 0;

    /// <summary>A short key: equal when the backbox would draw the same.</summary>
    public string DisplayKey() => $"{Phase}|{string.Join(',', Scores)}|{Up}|{Ball}|{Message}|{Mission}|{Match}|{Tilt}";

    public byte[] Pack()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Phase);
        w.Write((byte)Scores.Length);
        foreach (long s in Scores) w.Write(s);
        w.Write((byte)Up);
        w.Write((byte)Ball);
        w.Write(Message);
        w.Write(Mission);
        w.Write((sbyte)Match);
        w.Write((byte)((Tilt ? 1 : 0) | (Multiball ? 2 : 0) | (Saving ? 4 : 0)));
        w.Write((short)Lamps.Length);
        // two bits a lamp
        for (int i = 0; i < Lamps.Length; i += 4)
        {
            int b = 0;
            for (int k = 0; k < 4 && i + k < Lamps.Length; k++) b |= (Lamps[i + k] & 3) << (2 * k);
            w.Write((byte)b);
        }
        return ms.ToArray();
    }

    public static PinballScreen Unpack(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var s = new PinballScreen { Phase = (PinballRules.Phase)r.ReadByte() };
        s.Scores = new long[r.ReadByte()];
        for (int i = 0; i < s.Scores.Length; i++) s.Scores[i] = r.ReadInt64();
        s.Up = r.ReadByte();
        s.Ball = r.ReadByte();
        s.Message = r.ReadString();
        s.Mission = r.ReadString();
        s.Match = r.ReadSByte();
        int flags = r.ReadByte();
        s.Tilt = (flags & 1) != 0;
        s.Multiball = (flags & 2) != 0;
        s.Saving = (flags & 4) != 0;
        s.Lamps = new byte[r.ReadInt16()];
        for (int i = 0; i < s.Lamps.Length; i += 4)
        {
            int b = r.ReadByte();
            for (int k = 0; k < 4 && i + k < s.Lamps.Length; k++) s.Lamps[i + k] = (byte)(b >> (2 * k) & 3);
        }
        return s;
    }
}
