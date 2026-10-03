using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--housepropsnet A|B</c> with <c>--connect</c> (#433, <c>tools/housepropscheck.sh</c>): two
/// clients in one house of the generated world. A finds the nearest house with a sink, walks in,
/// turns the tap on with E and tells B where. B walks into the same house and must see the water
/// run there (the table entry and the stream node). A then sits at an instrument (the house's own,
/// else at the sink as a stand-in) and B must see A's copy seated; A plays notes and B must hear
/// most of them; A stands up and turns the tap off, and B must see it off. Each role ends on a
/// RESULT line.
/// </summary>
public partial class HousePropsProbe : ChatProbe
{
    public static string? Role => RoleArg("--housepropsnet");

    private readonly WorldOrigin _origin;

    public HousePropsProbe(ItemController items, WorldOrigin origin) : base(items, "housepropsnet", "HP", "houseprops_") => _origin = origin;

    public HousePropsProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(240, () => GetParent() is ClientWorld { Stage: LoadStage.Ready })) return;
        await Seconds(2.0);
        if (_role == "B") await RunB();
        else await RunA();
        await Finish(2);
    }

    private static HouseProps Props => HouseProps.Instance!;

    private async Task RunA()
    {
        var me = Me!;
        var (door, layout) = await FindHouse(me);
        if (door is not { } d || layout == null) return;
        int sink = layout.Furniture.FindIndex(f => f.Type == FurnitureType.Sink);
        int instrument = layout.Furniture.FindIndex(f => HouseProps.InstrumentOf(f.Type) != null);
        GD.Print($"{Log} house {layout.Key} at {d.World}: sink #{sink}, instrument #{instrument}");

        if (!await WalkIn(me, d)) return;
        var interiors = InteriorManager.Instance!;
        var node = interiors.CurrentNode!;
        if (!await StandAt(me, layout, node, sink, f => HouseProps.At(me) == f)) return;
        Expect(HouseProps.PromptFor(me)?.Contains("Turn the tap on") == true, $"the prompt offers the tap ({HouseProps.PromptFor(me)})");
        Expect(me.TryInteract(), "E at the sink");
        Expect(await Until(() => Props.Running(layout.Key, sink) && interiors.CurrentNode?.HasNode($"Tap{sink}") == true, 8),
            "the water runs here (the server said so)");
        Shot("tap");

        var (e, n) = _origin.ToLv95(d.World);
        string news = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tap {layout.Key} {sink} {e:F1} {n:F1}");
        bool seen = false;
        for (int i = 0; i < 150 && !seen; i++)
        {
            Say(news);
            seen = await Heard("B", "seen tap", 2);
        }
        Expect(seen, "B came in and saw the tap");
        if (!seen) return;
        Expect(_heard.Any(l => l.Contains("HP B seen tap ok")), "B saw the water run");

        // sit at an instrument: the house's own, else (most houses have none) at the sink
        node = interiors.CurrentNode!;
        int at = instrument;
        if (instrument >= 0 && await StandAt(me, layout, node, instrument, f => HouseProps.At(me) == f))
        {
            Expect(me.TryInteract() && me.PlayingAt == instrument && Props.Ui?.IsOpen == true, "E sits at the instrument and opens the keys");
        }
        else
        {
            at = sink;
            var f = layout.Furniture[sink];
            me.PlayAt(node, sink, HouseProps.FrameOf(layout, f) * new Vector3(0, 0, f.D / 2 + 0.45f), f.Turns * Mathf.Pi / 2, true);
            Props.Ui?.Open(me, layout.Key, sink, Audio.InstrumentKind.Piano);
        }
        await Seconds(1.5);
        Expect(me.PoseKind == FootPlayer.PoseSeat, $"sat (pose {me.PoseKind})");
        bool seated = false;
        for (int i = 0; i < 20 && !seated; i++)
        {
            Say("seated");
            seated = await Heard("B", "seen seat", 2);
        }
        Expect(seated && _heard.Any(l => l.Contains("HP B seen seat ok")), "B saw A seated");
        Shot("seated");

        // a little tune, slow enough for an unreliable channel
        int[] keys = { 0, 2, 4, 5, 7, 9, 11, 12 };
        foreach (int k in keys)
        {
            if (Props.Ui?.IsOpen == true) Props.Ui.Press(k);
            else Props.Strike(layout.Key, at, 60 + k, 0.8f);
            await Seconds(0.2);
        }
        bool heard = false;
        for (int i = 0; i < 15 && !heard; i++)
        {
            Say($"played {keys.Length}");
            heard = await Heard("B", "seen notes", 2);
        }
        Expect(heard && _heard.Any(l => l.Contains("HP B seen notes ok")), "B heard the notes");

        Props.Ui?.Close();
        await Seconds(0.5);
        Expect(me.PlayingAt < 0, "stood up");
        Props.ToggleTap(layout.Key, sink);
        Expect(await Until(() => !Props.Running(layout.Key, sink) && interiors.CurrentNode?.HasNode($"Tap{sink}") != true, 8), "the tap is off");
        bool off = false;
        for (int i = 0; i < 15 && !off; i++)
        {
            Say("tapoff");
            off = await Heard("B", "seen off", 2);
        }
        Expect(off && _heard.Any(l => l.Contains("HP B seen off ok")), "B saw the tap off");
    }

    private async Task RunB()
    {
        var me = Me!;
        if (!await Heard("A", "tap", 420)) { Fail("A never turned a tap on"); return; }
        var words = _heard.Last(l => l.Contains("HP A tap")).Split(' ');
        int w = Array.IndexOf(words, "tap");
        string key = words[w + 1];
        int sink = int.Parse(words[w + 2]);
        double e = double.Parse(words[w + 3], System.Globalization.CultureInfo.InvariantCulture);
        double n = double.Parse(words[w + 4], System.Globalization.CultureInfo.InvariantCulture);
        if (!BuildingKey.TryParse(key, out var bk)) { Fail($"bad key {key}"); return; }
        var spot = _origin.ToWorld(e, n, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        if (!await Until(() => me.IsOnFloor() && DoorIndex.Find(bk) != null, 90)) { Fail("the house's door never loaded"); return; }
        var door = DoorIndex.Find(bk)!.Value;
        var interiors = InteriorManager.Instance!;
        InteriorLayout? layout = null;
        try { layout = await interiors.GetOrCreate(key); } catch { }
        if (layout == null) { Fail("no plan"); return; }
        if (!await WalkIn(me, door)) return;
        if (!await StandAt(me, layout, interiors.CurrentNode!, sink, f => HouseProps.At(me) == f)) return;
        bool running = await Until(() => Props.Running(key, sink) && interiors.CurrentNode?.HasNode($"Tap{sink}") == true, 10);
        Expect(running, "the water runs here too");
        Expect(HouseProps.PromptFor(me)?.Contains("Turn the tap off") == true, $"the prompt offers to turn it off ({HouseProps.PromptFor(me)})");
        Shot("tap");
        Say(running ? "seen tap ok" : "seen tap FAILED");

        if (!await Heard("A", "seated", 60)) { Fail("A never sat down"); return; }
        bool seated = await Until(() => Copy() is { PoseKind: FootPlayer.PoseSeat }, 6);
        Expect(seated, $"A's copy is seated (pose {Copy()?.PoseKind})");
        Shot("seated");
        Say(seated ? "seen seat ok" : "seen seat FAILED");

        if (!await Heard("A", "played", 60)) { Fail("A never played"); return; }
        await Seconds(1.0);
        int notes = Props.NotesHeard;
        Expect(notes >= 5, $"heard A's notes ({notes} of 8)");
        Say(notes >= 5 ? $"seen notes ok {notes}" : $"seen notes FAILED {notes}");

        if (!await Heard("A", "tapoff", 60)) { Fail("A never turned the tap off"); return; }
        bool off = await Until(() => !Props.Running(key, sink) && interiors.CurrentNode?.HasNode($"Tap{sink}") != true, 8);
        Expect(off, "the water stopped here too");
        Expect(Copy() is { PoseKind: not FootPlayer.PoseSeat }, "A's copy stood up");
        Say(off ? "seen off ok" : "seen off FAILED");
    }

    private FootPlayer? Copy()
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != Me && !p.IsMultiplayerAuthority()) return p;
        return null;
    }

    /// <summary>The nearest house with a sink (an instrument too, if one of the nearest has one), with its plan.</summary>
    private async Task<(DoorIndex.Entry?, InteriorLayout?)> FindHouse(FootPlayer me)
    {
        var interiors = InteriorManager.Instance!;
        var tried = new HashSet<string>();
        (DoorIndex.Entry, InteriorLayout)? plain = null;
        for (int round = 0; round < 40; round++)
        {
            var here = me.GlobalPosition;
            var doors = DoorIndex.All().Where(d => d.Kind == BuildingKind.House && !tried.Contains(d.Key.ToString()))
                .OrderBy(d => new Vector2(d.World.X - here.X, d.World.Z - here.Z).Length()).Take(20).ToList();
            foreach (var d in doors)
            {
                tried.Add(d.Key.ToString());
                InteriorLayout? l = null;
                try { l = await interiors.GetOrCreate(d.Key.ToString()); } catch { }
                if (l == null || l.Key != d.Key.ToString() || !l.Furniture.Any(f => f.Type == FurnitureType.Sink)) continue;
                if (l.Furniture.Any(f => HouseProps.InstrumentOf(f.Type) != null)) return (d, l);
                plain ??= (d, l);
                if (tried.Count >= 30) return plain.Value;
            }
            if (plain is { } p) return p;
            await Seconds(3.0);
        }
        Fail($"no house with a sink among {tried.Count} house doors");
        return (null, null);
    }

    protected override string Shot(string name)
    {
        if (DisplayServer.GetName() == "headless") return "";
        string path = base.Shot($"{_role}_{name}");
        GD.Print($"{Log} screenshot {path}");
        return path;
    }
}
