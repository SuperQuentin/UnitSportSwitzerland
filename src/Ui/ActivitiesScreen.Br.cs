using Godot;
using UnitSport.BattleRoyale;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Ui;

/// <summary>
/// The Battle Royale tab (#840): the lobby or the match on the left (join, leave, team, invite), the
/// match settings on the right for an admin (area, size, length, teams, bots; open, start, force the
/// zone, cancel), a summary for everyone else. Everything goes out as <c>/br …</c>.
/// </summary>
public partial class ActivitiesScreen
{
    /// <summary>The settings an admin last opened a match with, kept for the session.</summary>
    private static class BrPick
    {
        public static int Area, Size, Length = 1, Teams, BotsIndex = 3, Level = 1;
        public static string Town = "";
    }

    private static readonly string[] AreaItems = { "Random region", "Where I stand", "A town…" };
    private static readonly string[] SizeItems = { "Auto (for the field)", "5 × 5 km", "6 × 6 km", "7 × 7 km" };
    private static readonly string[] LengthItems = { "Short", "Normal", "Long" };
    private static readonly string[] TeamItems = { "Solo", "Duos", "Trios", "Squads" };
    private static readonly int[] BotCounts = { 0, 5, 10, 20, 30, 40, 64 };
    private static readonly string[] LevelItems = { "Easy", "Normal", "Hard" };

    private Label _brHeader = null!, _brSub = null!;
    private VBoxContainer _brPlayers = null!, _brActions = null!, _brSettings = null!, _brAdmin = null!;
    private LineEdit _brTeam = null!, _brTown = null!;
    private HBoxContainer _brTownRow = null!, _brTownHits = null!;
    private OptionButton _brInvitePicker = null!;
    private BrManager? _brBound;
    private string _brShown = "";

    private Control BuildBr()
    {
        var (root, left, right) = Columns();
        _brHeader = UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true);
        left.AddChild(_brHeader);
        _brSub = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, wrap: true);
        left.AddChild(_brSub);
        left.AddChild(UiKit.Spacer(6));
        left.AddChild(UiKit.Section("Players"));
        _brPlayers = UiKit.VBox(2);
        left.AddChild(_brPlayers);
        left.AddChild(UiKit.Spacer(6));
        _brActions = UiKit.VBox(6);
        left.AddChild(_brActions);

        // the team name and the invite stay built: a list refresh must not eat what is being typed
        var teamRow = UiKit.HBox(8);
        _brTeam = new LineEdit { PlaceholderText = "team name (friends type the same)", SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = 16 };
        _brTeam.FocusEntered += () => UiFocus.Set(_brTeam, true);
        _brTeam.FocusExited += () => UiFocus.Set(_brTeam, false);
        _brTeam.TextSubmitted += t => Run($"/br team {t}");
        teamRow.AddChild(_brTeam);
        var setTeam = UiKit.Button("Set team");
        setTeam.Pressed += () => Run($"/br team {_brTeam.Text}");
        teamRow.AddChild(setTeam);
        _brActions.AddChild(teamRow);
        var inviteRow = UiKit.HBox(8);
        _brInvitePicker = PlayerPicker();
        _brInvitePicker.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        inviteRow.AddChild(_brInvitePicker);
        var invite = UiKit.Button("Invite to the lobby");
        invite.Pressed += () => { if (Picked(_brInvitePicker) is { } who) Run($"/br invite {who}"); };
        inviteRow.AddChild(invite);
        _brActions.AddChild(inviteRow);
        _brJoinRow = UiKit.HBox(8);
        _brActions.AddChild(_brJoinRow);

        // the right: an admin's settings and buttons, a summary for everyone else
        _brSettings = UiKit.VBox(2);
        right.AddChild(_brSettings);
        _brSettings.AddChild(UiKit.Section("Match settings"));
        static void None() { }
        UiKit.OptionRow(_brSettings, "Area", AreaItems, BrPick.Area, i => { BrPick.Area = i; ShowTownRow(); }, commit: None);
        _brTownRow = UiKit.HBox(8);
        _brTown = new LineEdit { PlaceholderText = "town", Text = BrPick.Town, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _brTown.FocusEntered += () => UiFocus.Set(_brTown, true);
        _brTown.FocusExited += () => UiFocus.Set(_brTown, false);
        _brTown.TextChanged += t => { BrPick.Town = t; TownHits(t); };
        _brTownRow.AddChild(_brTown);
        _brSettings.AddChild(_brTownRow);
        _brTownHits = UiKit.HBox(4);
        _brSettings.AddChild(_brTownHits);
        UiKit.OptionRow(_brSettings, "Size", SizeItems, BrPick.Size, i => BrPick.Size = i, commit: None);
        UiKit.OptionRow(_brSettings, "Length", LengthItems, BrPick.Length, i => BrPick.Length = i, "How fast the zone closes", commit: None);
        UiKit.OptionRow(_brSettings, "Teams", TeamItems, BrPick.Teams, i => BrPick.Teams = i, "A party plays as one team", commit: None);
        UiKit.OptionRow(_brSettings, "Bots", BotCounts.Select(n => n == 0 ? "Off" : $"Fill to {n}").ToArray(), BrPick.BotsIndex,
            i => BrPick.BotsIndex = i, "Server-run players top the field up at GO", commit: None);
        UiKit.OptionRow(_brSettings, "Bot skill", LevelItems, BrPick.Level, i => BrPick.Level = i, commit: None);
        _brAdmin = UiKit.VBox(6);
        right.AddChild(_brAdmin);
        ShowTownRow();
        return root;
    }

    private HBoxContainer _brJoinRow = null!;

    private void ShowTownRow()
    {
        _brTownRow.Visible = BrPick.Area == 2;
        _brTownHits.Visible = BrPick.Area == 2;
    }

    /// <summary>Up to four towns matching what is typed, as buttons that fill the field.</summary>
    private void TownHits(string typed)
    {
        Clear(_brTownHits);
        if (typed.Trim().Length < 2 || _chat?.PlaceSearch is not { } search) return;
        foreach (var place in search.Search(typed, 4))
        {
            var b = UiKit.Button(place.Name);
            string name = place.Name;
            b.Pressed += () => { _brTown.Text = name; BrPick.Town = name; Clear(_brTownHits); };
            _brTownHits.AddChild(b);
        }
    }

    private void UnbindBr()
    {
        if (_brBound != null) _brBound.StateChanged -= OnBrState;
        _brBound = null;
    }

    private void OnBrState()
    {
        _brShown = "";
        Soon();
    }

    /// <summary>The <c>/br open</c> line for the settings picked.</summary>
    private static string OpenCommand()
    {
        var words = new List<string> { "/br open" };
        if (BrPick.Area == 1) words.Add("here");
        else if (BrPick.Area == 2 && BrPick.Town.Trim().Length > 0) words.Add(BrPick.Town.Trim());
        if (BrPick.Size > 0) words.Add((BrPick.Size + 4).ToString());
        words.Add(LengthItems[BrPick.Length].ToLowerInvariant());
        words.Add(TeamItems[BrPick.Teams].ToLowerInvariant());
        return string.Join(' ', words);
    }

    private static string BotsCommand() =>
        BotCounts[BrPick.BotsIndex] == 0 ? "/br bots off" : $"/br bots {BotCounts[BrPick.BotsIndex]} {LevelItems[BrPick.Level].ToLowerInvariant()}";

    private void RefreshBr()
    {
        var br = BrManager.Instance;
        if (br != _brBound)
        {
            UnbindBr();
            _brBound = br;
            if (br != null) br.StateChanged += OnBrState;
            _brShown = "";
        }
        var s = br?.State ?? new BrState();
        long me = br?.Me ?? 0;
        var mine = s.Find(me);
        double now = ClockSync.ServerNow;

        // the header ticks every second; the lists only when the state's shape changes
        string mode = s.TeamSize switch { 2 => "duos", 3 => "trios", 4 => "squads", _ => "solo" };
        (_brHeader.Text, _brSub.Text) = s.Phase switch
        {
            BrPhase.Idle => ("No match open", Permissions.IsAdmin ? "Pick the settings on the right and open a lobby." : "An admin opens one; you will be invited."),
            BrPhase.Lobby => ($"Lobby · {s.AreaName}", $"{s.Side / 1000:F0} × {s.Side / 1000:F0} km · {mode} · {s.Entrants.Count} joined"
                + (s.Bots > 0 ? $" · bots fill to {s.Bots}" : "") + $" · starts once {BrManager.MinPlayers} are in"),
            BrPhase.Countdown => ($"Starting in {Math.Max(0, s.CountdownEnds - now):0} s · {s.AreaName}",
                $"{s.Side / 1000:F0} × {s.Side / 1000:F0} km · {mode} · {s.Entrants.Count} joined" + (s.Bots > 0 ? $" · bots fill to {s.Bots}" : "")),
            BrPhase.Playing => ($"Match in {s.AreaName}", $"{s.AliveCount} of {s.Entrants.Count} alive" + (mine != null ? mine.Alive ? " · you are in it" : $" · you placed #{mine.Place}" : "")),
            _ => ($"Match over · {s.AreaName}", "Results, then everyone goes back."),
        };

        bool admin = Permissions.IsAdmin;
        string shape = $"{s.Phase}|{s.Seed}|{admin}|{mine != null}|{string.Join(",", s.Entrants.Select(e => $"{e.Name}:{e.Party}:{e.Team}:{e.Alive}"))}|{s.Bots}";
        if (shape == _brShown) return;
        _brShown = shape;

        Clear(_brPlayers);
        foreach (var e in s.Entrants.Where(e => !e.Bot).Take(40))
        {
            string team = s.Running && e.Team != 0 ? $"team {e.Team}" : e.Party.Length > 0 ? $"[{(e.Party.StartsWith("party") ? "party" : e.Party)}]" : "";
            var (row, _) = ListRow(e.Peer == me ? $"{e.Name}  (you)" : e.Name, team, e.Alive ? UiTheme.Text : UiTheme.TextFaint);
            _brPlayers.AddChild(row);
        }
        int bots = s.Entrants.Count(e => e.Bot);
        if (bots > 0) _brPlayers.AddChild(UiKit.Text($"+ {bots} bots", UiTheme.FontSmall, UiTheme.TextDim));
        if (s.Entrants.Count == 0)
            _brPlayers.AddChild(UiKit.Text(s.Phase == BrPhase.Idle ? "—" : "Nobody yet.", UiTheme.FontSmall, UiTheme.TextFaint));
        if (s.Phase is BrPhase.Lobby or BrPhase.Countdown && s.Leaders.Count > 0)
        {
            _brPlayers.AddChild(UiKit.Spacer(6));
            _brPlayers.AddChild(UiKit.Section("Leaderboard"));
            foreach (var line in s.Leaders) _brPlayers.AddChild(UiKit.Text(line, UiTheme.FontSmall, UiTheme.TextDim));
        }

        bool lobby = s.Phase is BrPhase.Lobby or BrPhase.Countdown;
        _brActions.Visible = lobby;
        Clear(_brJoinRow);
        if (lobby)
        {
            var join = UiKit.Button(mine == null ? "Join the match" : "Leave the lobby", primary: mine == null);
            join.Pressed += () => Run(mine == null ? "/br join" : "/br leave");
            _brJoinRow.AddChild(join);
            if (Parties.Client is { InParty: true })
                _brJoinRow.AddChild(UiKit.Text("Your party plays as one team.", UiTheme.FontSmall, UiTheme.TextDim));
        }
        else if (s.Phase == BrPhase.Playing && mine is { Alive: true })
        {
            _brActions.Visible = true;
            var leave = UiKit.Button("Leave the match");
            leave.Pressed += () => Modal.Confirm(this, "Leave the match?", "You are out of it, as if knocked out.", "Leave", () => Run("/br leave"), danger: true);
            _brJoinRow.AddChild(leave);
        }

        // the right column
        _brSettings.Visible = admin && s.Phase == BrPhase.Idle;
        Clear(_brAdmin);
        if (!admin)
        {
            _brAdmin.AddChild(UiKit.Section("How it works"));
            _brAdmin.AddChild(UiKit.Text("An admin opens a lobby and everyone online is invited. Join, pick a team name or come as a party, "
                + "and the cargo plane takes off when the countdown ends. The last one standing wins; everyone then goes back where they were, "
                + "with their own things.", UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
            return;
        }
        void Admin(string text, string command, bool primary = false, bool confirm = false)
        {
            var b = UiKit.Button(text, primary);
            b.Pressed += confirm
                ? () => Modal.Confirm(this, $"{text}?", "Everyone in it goes back where they were.", text, () => Run(command), danger: true)
                : () => Run(command);
            _brAdmin.AddChild(b);
        }
        _brAdmin.AddChild(UiKit.Spacer(8));
        switch (s.Phase)
        {
            case BrPhase.Idle:
            {
                var open = UiKit.Button("Open the lobby", primary: true);
                open.Pressed += () =>
                {
                    if (BrPick.Area == 2 && BrPick.Town.Trim().Length == 0) { _status.Text = "Type a town first."; return; }
                    Run(BotsCommand());
                    Run(OpenCommand());
                };
                _brAdmin.AddChild(open);
                _brAdmin.AddChild(UiKit.Text("Everyone online gets an invite.", UiTheme.FontTiny, UiTheme.TextFaint));
                break;
            }
            case BrPhase.Lobby or BrPhase.Countdown:
                _brAdmin.AddChild(UiKit.Section("Admin"));
                Admin("Start in 10 s", "/br start", primary: true);
                UiKit.OptionRow(_brAdmin, "Bots", BotCounts.Select(n => n == 0 ? "Off" : $"Fill to {n}").ToArray(),
                    Math.Max(0, Array.IndexOf(BotCounts, s.Bots)), i => { BrPick.BotsIndex = i; Run(BotsCommand()); }, commit: () => { });
                Admin("Cancel the lobby", "/br cancel", confirm: true);
                break;
            case BrPhase.Playing:
                _brAdmin.AddChild(UiKit.Section("Admin"));
                Admin("Close the zone now", "/br zone");
                Admin("Cancel the match", "/br cancel", confirm: true);
                break;
        }
    }
}
