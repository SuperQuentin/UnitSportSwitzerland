using Godot;
using UnitSport.Core;
using UnitSport.World;

namespace UnitSport.Ui;

/// <summary>
/// The Race tab (#840): the races on the server on the left, with Join / Leave (the list is asked for
/// once a second while the tab shows: <see cref="RaceManager.RequestRows"/>); a race or a duel to
/// start on the right. Everything goes out as <c>/race …</c>; anyone may start one.
/// </summary>
public partial class ActivitiesScreen
{
    private static class RacePick
    {
        public static int Kind, Distance = 1, Mount, Npcs, AirMount;
        public static string Place = "";
    }

    private static readonly string[] RaceKinds = { "On the road", "Through air gates" };
    private static readonly (string Name, int Metres)[] Distances = { ("1 km", 1000), ("2 km", 2000), ("4 km", 4000), ("8 km", 8000) };
    private static readonly (string Name, string Word)[] RoadMounts =
        { ("What each one rides", "open"), ("On foot", "foot"), ("Road bike", "bike"), ("Skis", "skis"), ("Snowboard", "snowboard"), ("Car", "car"), ("Motorbike", "moto") };
    private static readonly (string Name, string Word)[] AirMounts =
        { ("Plane", "plane"), ("Helicopter", "heli"), ("Paraglider", "paraglider"), ("Wingsuit", "wingsuit") };
    private static readonly int[] NpcCounts = { 0, 1, 2, 4, 8 };

    private VBoxContainer _raceList = null!, _roadOnly = null!, _airOnly = null!;
    private OptionButton _duelPicker = null!;
    private LineEdit _racePlace = null!;
    /// <summary>What the list shows; null to rebuild it.</summary>
    private string? _raceShown;
    private RaceManager? _races;

    private Control BuildRace()
    {
        _races = Shell.World?.GetNodeOrNull<RaceManager>(RaceManager.NodeName);
        var (root, left, right) = Columns();
        left.AddChild(UiKit.Section("Races on the server"));
        _raceList = UiKit.VBox(4);
        left.AddChild(_raceList);

        right.AddChild(UiKit.Section("Start a race"));
        static void None() { }
        UiKit.OptionRow(right, "Course", RaceKinds, RacePick.Kind, i => { RacePick.Kind = i; ShowRaceKind(); }, commit: None);
        _roadOnly = UiKit.VBox(2);
        right.AddChild(_roadOnly);
        UiKit.OptionRow(_roadOnly, "Distance", Distances.Select(d => d.Name).ToArray(), RacePick.Distance, i => RacePick.Distance = i,
            "Along the road from where you stand", commit: None);
        UiKit.OptionRow(_roadOnly, "Ridden on", RoadMounts.Select(m => m.Name).ToArray(), RacePick.Mount, i => RacePick.Mount = i, commit: None);
        UiKit.OptionRow(_roadOnly, "NPC racers", NpcCounts.Select(n => n == 0 ? "None" : n.ToString()).ToArray(), RacePick.Npcs,
            i => RacePick.Npcs = i, "They drive in to the grid", commit: None);
        _airOnly = UiKit.VBox(2);
        right.AddChild(_airOnly);
        var placeRow = UiKit.HBox(8);
        placeRow.AddChild(UiKit.Text("To", UiTheme.FontBody, UiTheme.TextDim));
        _racePlace = new LineEdit { PlaceholderText = "a town, or empty: 3 km ahead", Text = RacePick.Place, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _racePlace.FocusEntered += () => UiFocus.Set(_racePlace, true);
        _racePlace.FocusExited += () => UiFocus.Set(_racePlace, false);
        _racePlace.TextChanged += t => RacePick.Place = t;
        placeRow.AddChild(_racePlace);
        _airOnly.AddChild(placeRow);
        UiKit.OptionRow(_airOnly, "Flown in", AirMounts.Select(m => m.Name).ToArray(), RacePick.AirMount, i => RacePick.AirMount = i, commit: None);

        right.AddChild(UiKit.Spacer(6));
        var open = UiKit.Button("Open the race to everyone", primary: true);
        open.Pressed += () =>
        {
            Run($"/race start {RaceArgs()}");
            if (RacePick.Kind == 0 && NpcCounts[RacePick.Npcs] > 0) Run($"/race npc {NpcCounts[RacePick.Npcs]}");
        };
        right.AddChild(open);
        right.AddChild(UiKit.Text("Everyone online gets an invite; 15 s to join.", UiTheme.FontTiny, UiTheme.TextFaint));

        right.AddChild(UiKit.Spacer(10));
        right.AddChild(UiKit.Section("Or a duel"));
        var duelRow = UiKit.HBox(8);
        _duelPicker = PlayerPicker();
        _duelPicker.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        duelRow.AddChild(_duelPicker);
        var duel = UiKit.Button("Challenge");
        duel.Pressed += () => { if (Picked(_duelPicker) is { } who) Run($"/race duel {who} {RaceArgs()}"); };
        duelRow.AddChild(duel);
        right.AddChild(duelRow);
        var npcDuel = UiKit.Button("Duel an NPC");
        npcDuel.Pressed += () => Run($"/race duel npc {(RacePick.Kind == 0 ? $"{Distances[RacePick.Distance].Metres} {RoadMounts[RacePick.Mount].Word}" : "")}".TrimEnd());
        right.AddChild(npcDuel);
        ShowRaceKind();
        return root;
    }

    private void ShowRaceKind()
    {
        _roadOnly.Visible = RacePick.Kind == 0;
        _airOnly.Visible = RacePick.Kind == 1;
    }

    /// <summary>What follows <c>/race start</c> (and <c>/race duel &lt;player&gt;</c>) for the settings picked.</summary>
    private static string RaceArgs()
    {
        if (RacePick.Kind == 1)
        {
            string to = RacePick.Place.Trim().Length > 0 ? RacePick.Place.Trim() : "3000";
            return $"air {to} {AirMounts[RacePick.AirMount].Word}";
        }
        return $"{Distances[RacePick.Distance].Metres} {RoadMounts[RacePick.Mount].Word}";
    }

    private void RefreshRace()
    {
        if (_races == null) return;
        if (!_racesBound)
        {
            _racesBound = true;
            _races.RowsChanged += Soon;
        }
        var rows = _races.Rows;
        string shape = string.Join(",", rows.Select(r => $"{r.Id}:{r.Entrants}:{r.Running}:{r.Mine}:{r.Joinable}:{(int)r.SecondsLeft}"));
        if (shape == _raceShown) return;
        _raceShown = shape;
        Clear(_raceList);
        foreach (var r in rows)
        {
            string state = r.Running ? "racing" : $"{Mathf.CeilToInt(r.SecondsLeft)} s to join";
            var (row, _) = ListRow($"#{r.Id} {r.Host}'s {(r.Km > 0 ? $"{r.Km:0.0} km " : "")}{r.What}", $"{r.Entrants} in · {state}{(r.Mine ? " · you are in it" : "")}",
                r.Mine ? UiTheme.Amber : UiTheme.Text);
            if (r.Mine)
            {
                var leave = UiKit.Button("Leave");
                leave.Pressed += () => Run("/race leave");
                row.AddChild(leave);
            }
            else if (r.Joinable)
            {
                var join = UiKit.Button("Join", primary: true);
                int id = r.Id;
                join.Pressed += () => Run($"/race join {id}");
                row.AddChild(join);
            }
            _raceList.AddChild(row);
        }
        if (rows.Count == 0) _raceList.AddChild(UiKit.Text("No race right now. Open one on the right.", UiTheme.FontSmall, UiTheme.TextFaint));
    }

    private bool _racesBound;
}
