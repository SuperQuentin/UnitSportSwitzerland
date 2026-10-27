using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--cinemanet A|B|C</c> with <c>--connect</c> on the generated world (#1238): a cinema's booth
/// (<see cref="CinemaBooths"/>) over the network. The booth's rule is the server's — a peer works it only
/// from inside the building — so any building tests it, and the generated world has a village of houses
/// where it has no cinema. A walks into a house and asks for the lights off and the cat on; the server
/// grants it. B, outside, must see that state arrive, then asks for the opposite and must be refused (it
/// is not inside). C joins late and must find the state from the table the server sends a newcomer.
/// Coordinated through chat lines; A repeats its line until both answer.
/// </summary>
public partial class CinemaNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--cinemanet");

    private readonly WorldOrigin _origin;

    public CinemaNetProbe(ItemController items, WorldOrigin origin) : base(items, "cinema", "CN") => _origin = origin;

    public CinemaNetProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    private static readonly int Show = CinemaBooths.Pack(CinemaBooths.Lights.Off, true);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(180)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        if (!await Until(() => CinemaBooths.Instance != null, 10)) { Fail("no World/CinemaBooths"); return; }
        var booths = CinemaBooths.Instance!;

        // to the village, and the house door nearest it: the same on every peer
        var (e, n) = SpawnPoint.ParseTarget();
        var spot = _origin.ToWorld(e, n, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        DoorIndex.Entry? found = null;
        if (!await Until(() => me.IsOnFloor() && !me.KnockedOut
                && (found = DoorIndex.NearestOfKind(me.GlobalPosition, 200f, BuildingKind.House, also: d => d.Key.Slot == 0)) != null, 90))
        { Fail("no house door near the spot"); return; }
        var door = found!.Value;
        GD.Print($"{Log} the house door {door.Key} at {door.World}");

        if (_role == "A")
        {
            if (!await WalkIn(me, interiors, door)) return;
            string plan = interiors.Current!.Key;
            Expect(booths.StateOf(plan) == (CinemaBooths.Lights.Full, false), "the house starts with its lights full and no picture");
            booths.Ask(plan, Show);
            Expect(await Until(() => booths.StateOf(plan) == (CinemaBooths.Lights.Off, true), 10),
                "the server granted A, inside, the lights off and the cat on");
            // said over and over: C has not joined yet
            bool b = false, c = false;
            for (int t = 0; t < 150 && !(b && c); t++)
            {
                Say($"set {plan}");
                if (t == 0) GD.Print($"{Log} set {plan}");
                await Until(() => _heard.Any(l => l.Contains("CN B done")) && _heard.Any(l => l.Contains("CN C done")), 2);
                b |= _heard.Any(l => l.Contains("CN B done"));
                c |= _heard.Any(l => l.Contains("CN C done"));
            }
            Expect(b, "B saw it, and was refused");
            Expect(c, "C, joining late, found it");
            Expect(booths.StateOf(plan) == (CinemaBooths.Lights.Off, true), "B's refused ask changed nothing for A");
        }
        else
        {
            if (!await Heard("A", "set", 240)) { Fail("A never set anything"); return; }
            string line = _heard.Last(l => l.Contains("CN A set"));
            string plan = line[(line.IndexOf("CN A set", StringComparison.Ordinal) + "CN A set".Length)..].Trim();
            Expect(await Until(() => booths.StateOf(plan) == (CinemaBooths.Lights.Off, true), 10),
                $"{_role} sees {plan}'s lights off and the cat on");
            if (_role == "B")
            {
                // outside: the server must refuse, and nothing changes
                Expect(!me.Indoors, "B is outside");
                booths.Ask(plan, CinemaBooths.Pack(CinemaBooths.Lights.Full, false));
                await Seconds(3);
                Expect(booths.StateOf(plan) == (CinemaBooths.Lights.Off, true), "the server refused B, who is not inside");
            }
            Say("done");
        }
        await Finish(6);
    }

    /// <summary>From just outside the door: open it unless it stands open, and walk through (as <see cref="LiftSyncProbe"/>).</summary>
    private async Task<bool> WalkIn(FootPlayer me, InteriorManager interiors, DoorIndex.Entry door)
    {
        door = DoorIndex.Find(door.Key) ?? door;
        string doorKey = door.Key.ToString();
        var inward = -door.Outward;
        me.LeaveInterior(door.World + door.Outward * 1.2f + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);
        await Until(() => me.IsOnFloor(), 10);
        bool open = false;
        for (int attempt = 0; attempt < 4 && !open; attempt++)
        {
            if (!interiors.IsOpen(doorKey)) me.TryInteract();
            open = await Until(() => interiors.Links.TryGetValue(doorKey, out var lk) && lk.Passable && lk.Swing >= 1f, 5 + attempt * 2);
        }
        if (!open) { Fail($"the door {doorKey} never opened"); return false; }
        Input.ActionPress(PlayerInput.MoveForward);
        bool inside = await Until(() => me.Indoors && interiors.Current != null, 8);
        await Seconds(0.4);
        Input.ActionRelease(PlayerInput.MoveForward);
        if (!inside) { Fail($"could not walk in through {doorKey}"); return false; }
        await Seconds(1.0);
        return true;
    }
}
