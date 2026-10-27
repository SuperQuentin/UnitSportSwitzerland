using Godot;
using UnitSport.Adventure.Versteckis;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Ui;

/// <summary>
/// The Versteckis tab (#1268): the games on the server with a Join each, the open form (where, rounds, bots and their
/// skill), and inside a game its roster with roles and scores, Start for the opener and Leave. Everything goes out as
/// <c>/vs …</c>, the server's one path. Offline there is no game (bots need a server): the tab says how to host one.
/// </summary>
public partial class ActivitiesScreen
{
    private static class VsPick
    {
        public static int Where, Rounds, Bots = 2, Level = 1;
    }

    private static readonly string[] VsWhereItems = { "Nearest village", "Where I stand", "A random village" };
    private static readonly string[] VsRoundItems = { "Auto (one per player)", "2", "3", "4", "5", "6", "8" };
    private static readonly string[] VsBotItems = { "Off", "Fill to 2", "Fill to 4", "Fill to 6", "Fill to 8" };

    private Label _vsHeader = null!, _vsSub = null!;
    private VBoxContainer _vsGames = null!, _vsRoster = null!, _vsRight = null!;
    private VersteckisManager? _vsBound;
    private string _vsShown = "";

    private Control BuildVs()
    {
        var (root, left, right) = Columns();
        _vsHeader = UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true);
        left.AddChild(_vsHeader);
        _vsSub = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, wrap: true);
        left.AddChild(_vsSub);
        left.AddChild(UiKit.Spacer(6));
        _vsRoster = UiKit.VBox(2);
        left.AddChild(_vsRoster);
        left.AddChild(UiKit.Spacer(6));
        left.AddChild(UiKit.Section("Games on this server"));
        _vsGames = UiKit.VBox(4);
        left.AddChild(_vsGames);
        _vsRight = UiKit.VBox(4);
        right.AddChild(_vsRight);
        return root;
    }

    private void UnbindVs()
    {
        if (_vsBound != null) _vsBound.StateChanged -= OnVsState;
        _vsBound = null;
    }

    private void OnVsState()
    {
        _vsShown = "";
        Soon();
    }

    private static string VsOpenCommand()
    {
        var words = new List<string> { "/vs open", VsPick.Where switch { 1 => "here", 2 => "random", _ => "nearest" } };
        if (VsPick.Rounds > 0) words.Add(VsRoundItems[VsPick.Rounds]);
        words.Add("bots");
        words.Add(VsPick.Bots == 0 ? "off" : (VsPick.Bots * 2).ToString());
        words.Add(LevelItems[VsPick.Level].ToLowerInvariant());
        return string.Join(' ', words);
    }

    private void RefreshVs()
    {
        var vs = VersteckisManager.Instance;
        if (vs != _vsBound)
        {
            UnbindVs();
            _vsBound = vs;
            if (vs != null) vs.StateChanged += OnVsState;
            _vsShown = "";
        }
        var mine = vs?.Mine;
        double now = ClockSync.SimNow;
        (_vsHeader.Text, _vsSub.Text) = mine == null
            ? ("Versteckis", "Hide-and-seek in a real village: hiders become hay bales, milk cans, gnomes and cows; one seeks. Everyone who plays earns the same.")
            : mine.Phase switch
            {
                VsPhase.Lobby => ($"Lobby · {mine.Label}", $"{mine.Humans} joined · starts by itself in {Math.Max(0, mine.PhaseEnds - now):0} s"
                    + (mine.Bots > 0 ? $" · bots fill to {mine.Bots}" : "")),
                VsPhase.Results => ($"Results · {mine.Label}", "Then everyone goes back where they were."),
                _ => ($"{mine.Label} · round {mine.Round}/{mine.Rounds}", $"{mine.Phase} · {mine.HidersLeft} hiding"),
            };
        string shape = $"{Permissions.Online}|{mine?.Id}|{mine?.Phase}|{mine?.Round}|{mine?.Opener}|"
            + string.Join(",", (mine?.Entrants ?? new()).Select(e => $"{e.Name}:{e.Role}:{e.Score}:{e.Caught}:{e.Left}"))
            + "|" + string.Join(",", (vs?.Games ?? Array.Empty<VsState>()).Select(g => $"{g.Id}:{g.Phase}:{g.Entrants.Count}"));
        if (shape == _vsShown) return;
        _vsShown = shape;

        Clear(_vsRoster);
        if (mine != null)
        {
            _vsRoster.AddChild(UiKit.Section("Players"));
            foreach (var e in mine.Entrants.Where(e => !e.Left))
            {
                string role = mine.Phase == VsPhase.Lobby ? "" : e.Role == VsRole.Seeker ? "seeker" : e.Caught ? "found" : "hiding";
                var (row, _) = ListRow((e.Peer == vs!.Me ? $"{e.Name}  (you)" : e.Name) + (e.Bot ? " [bot]" : ""), role, e.Bot ? UiTheme.TextDim : UiTheme.Text);
                row.AddChild(UiKit.Text($"{e.Score}", UiTheme.FontBody, UiTheme.Text, bold: true));
                _vsRoster.AddChild(row);
            }
            var actions = UiKit.HBox(8);
            if (mine.Phase == VsPhase.Lobby && mine.Opener == vs!.Me)
            {
                var start = UiKit.Button("Start", primary: true);
                start.Pressed += () => Run("/vs start");
                actions.AddChild(start);
            }
            var leave = UiKit.Button(mine.Phase == VsPhase.Lobby ? "Leave the lobby" : "Leave the game");
            leave.Pressed += mine.Phase == VsPhase.Lobby ? () => Run("/vs leave")
                : () => Modal.Confirm(this, "Leave the game?", "Your rounds so far still count for your reward.", "Leave", () => Run("/vs leave"), danger: true);
            actions.AddChild(leave);
            _vsRoster.AddChild(actions);
        }

        Clear(_vsGames);
        foreach (var g in vs?.Games ?? Array.Empty<VsState>())
        {
            var (row, _) = ListRow(g.Label, $"{g.Phase.ToString().ToLowerInvariant()} · {g.Humans} players" + (g.Round > 0 ? $" · round {g.Round}/{g.Rounds}" : ""));
            if (g.Phase == VsPhase.Lobby && mine == null)
            {
                var join = UiKit.Button("Join", primary: true);
                int id = g.Id;
                join.Pressed += () => Run($"/vs join {id}");
                row.AddChild(join);
            }
            _vsGames.AddChild(row);
        }
        if (_vsGames.GetChildCount() == 0) _vsGames.AddChild(UiKit.Text("No game yet.", UiTheme.FontSmall, UiTheme.TextFaint));

        Clear(_vsRight);
        if (!Permissions.Online)
        {
            _vsRight.AddChild(UiKit.Section("Play"));
            _vsRight.AddChild(UiKit.Text("Versteckis needs a server: its bots run there. Host a game from the main menu (Multiplayer › Host), "
                + "then open one here; bots fill it, so two friends, or you alone, can play.", UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
            return;
        }
        if (mine != null)
        {
            _vsRight.AddChild(UiKit.Section("How to play"));
            _vsRight.AddChild(UiKit.Text(InputHints.Format("Hiders: {use_item} with the Disguise picks a prop; hold {interact_mount} at the bell, the fire "
                + "or the milk can for points; {sprint} bursts. Seeker: {use_item} tags what the crosshair is on; Sniff shows puffs near hiders."),
                UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
            return;
        }
        _vsRight.AddChild(UiKit.Section("Open a game"));
        static void None() { }
        UiKit.OptionRow(_vsRight, "Where", VsWhereItems, VsPick.Where, i => VsPick.Where = i, "A church or chapel of a village or hamlet", commit: None);
        UiKit.OptionRow(_vsRight, "Rounds", VsRoundItems, VsPick.Rounds, i => VsPick.Rounds = i, "Each player seeks once", commit: None);
        UiKit.OptionRow(_vsRight, "Bots", VsBotItems, VsPick.Bots, i => VsPick.Bots = i, "Server-run players fill the game", commit: None);
        UiKit.OptionRow(_vsRight, "Bot skill", LevelItems, VsPick.Level, i => VsPick.Level = i, commit: None);
        var open = UiKit.Button("Open a game", primary: true);
        open.Pressed += () => Run(VsOpenCommand());
        _vsRight.AddChild(open);
        _vsRight.AddChild(UiKit.Text("Your party and players within 1.5 km are invited.", UiTheme.FontTiny, UiTheme.TextFaint));
    }
}
