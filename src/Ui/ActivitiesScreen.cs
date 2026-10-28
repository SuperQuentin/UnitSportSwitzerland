using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Ui;

/// <summary>
/// The Activities screen (#840): what the console does for an activity, as a page. Tabs Invites,
/// Battle Royale and Race (LB / RB, Q / E). Every button sends the command the console takes
/// (<c>/br open zug 6 normal duos</c>), so the server keeps one path and checks everything again;
/// the reply shows in the status line at the bottom. Versteckis (#1268) has its own tab. Opened from the quick menu, the Start menu and
/// the wrist menu; online only. Docs: <c>docs/notes/ui/activities.md</c>.
/// </summary>
public partial class ActivitiesScreen : Screen
{
    public enum Tab { Invites, BattleRoyale, Race, Versteckis }

    private readonly List<Button> _tabs = new();
    private readonly List<Control> _pages = new();
    private Tab _current;
    private Label _status = null!;
    private ChatManager? _chat;
    private double _tick;

    public static ActivitiesScreen Create(Tab tab) => new() { Name = "Activities", _current = tab };

    /// <summary>Pushes the screen over the world (from the quick menu or the wrist menu).</summary>
    public static void Open(Tab tab)
    {
        if (GameShell.Instance is not { InWorld: true } shell) return;
        if (shell.Top is ActivitiesScreen open) { open.Show(tab); return; }
        shell.Push(Create(tab));
    }

    public override void _Ready()
    {
        _chat = Shell.World?.GetNodeOrNull<ChatManager>(ChatManager.NodeName);
        // the world dimmed behind the page, as under the pause menu: the chat and the HUD do not show through
        var dim = new ColorRect { Color = new Color(0.01f, 0.012f, 0.02f, 0.45f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var view = GetViewportRect().Size;
        var (body, _) = Framed("Activities", "Launch, configure and join; answer what others ask",
            new Vector2(Mathf.Min(view.X - 48, 1040), Mathf.Min(view.Y - 48, 640)));

        var tabRow = UiKit.HBox(4);
        body.AddChild(tabRow);
        var group = new ButtonGroup();
        var pages = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        body.AddChild(pages);

        void AddTab(string name, Control page)
        {
            var b = new Button { Text = name, ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 36) };
            var normal = UiTheme.Flat(new Color(0, 0, 0, 0), 6, 14, 6);
            var on = UiTheme.Flat(new Color(UiTheme.Amber, 0.12f), 6, 14, 6);
            on.BorderColor = UiTheme.Amber; on.BorderWidthBottom = 2;
            b.AddThemeStyleboxOverride("normal", normal);
            b.AddThemeStyleboxOverride("pressed", on);
            b.AddThemeStyleboxOverride("hover_pressed", on);
            UiKit.DimFont(b);
            b.AddThemeColorOverride("font_pressed_color", UiTheme.Amber);
            b.AddThemeColorOverride("font_hover_pressed_color", UiTheme.Amber);
            var tab = (Tab)_tabs.Count;
            b.Toggled += pressed => { if (pressed) Show(tab); };
            tabRow.AddChild(b);
            _tabs.Add(b);
            page.SetAnchorsPreset(LayoutPreset.FullRect);
            page.Visible = false;
            pages.AddChild(page);
            _pages.Add(page);
        }

        AddTab("Invites", BuildInvites());
        AddTab("Battle Royale", BuildBr());
        AddTab("Race", BuildRace());
        AddTab("Versteckis", BuildVs());

        body.AddChild(UiKit.Line());
        _status = UiKit.Text(Permissions.Online ? "" : "Activities need a multiplayer game.", UiTheme.FontSmall, UiTheme.TextDim);
        _status.ClipText = true;
        body.AddChild(_status);

        if (_chat != null) _chat.LineReceived += OnLine;
        if (InviteService.Client is { } invites) invites.Changed += OnInvitesChanged;
        if (Parties.Client is { } parties) parties.Changed += OnInvitesChanged;
        _chat?.RequestPlayerNames();
        if (_chat != null) _chat.NamesReceived += OnNames;
        Show(_current);
    }

    public override void _ExitTree()
    {
        if (_chat != null)
        {
            _chat.LineReceived -= OnLine;
            _chat.NamesReceived -= OnNames;
        }
        if (InviteService.Client is { } invites) invites.Changed -= OnInvitesChanged;
        if (Parties.Client is { } parties) parties.Changed -= OnInvitesChanged;
        UnbindBr();
        UnbindVs();
    }

    /// <summary>The server's private replies and errors while the page is up: the status line.</summary>
    private void OnLine(string line, ChatKind kind)
    {
        if (kind is not (ChatKind.Private or ChatKind.Error)) return;
        _status.Text = line.Split('\n')[0];
        _status.AddThemeColorOverride("font_color", kind == ChatKind.Error ? UiTheme.Bad : UiTheme.TextDim);
    }

    /// <summary>A command, as typed at the console.</summary>
    private void Run(string command)
    {
        GD.Print($"[activities] {command}");
        _status.Text = "…";
        _chat?.Send(command);
    }

    private void Show(Tab tab)
    {
        _current = tab;
        for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == (int)tab;
        if (!_tabs[(int)tab].ButtonPressed) _tabs[(int)tab].SetPressedNoSignal(true);
        Refresh();
    }

    public override void OnShown() => _tabs[(int)_current].CallDeferred(Control.MethodName.GrabFocus);

    /// <summary>LB / RB and Q / E step through the tabs.</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsVisibleInTree() || !e.IsPressed() || e.IsEcho() || Modal.Current != null) return;
        int step = e switch
        {
            InputEventJoypadButton { ButtonIndex: JoyButton.LeftShoulder } => -1,
            InputEventJoypadButton { ButtonIndex: JoyButton.RightShoulder } => 1,
            InputEventKey { PhysicalKeycode: Key.Q } when GetViewport().GuiGetFocusOwner() is not LineEdit => -1,
            InputEventKey { PhysicalKeycode: Key.E } when GetViewport().GuiGetFocusOwner() is not LineEdit => 1,
            _ => 0,
        };
        if (step == 0) return;
        int next = ((int)_current + step + _tabs.Count) % _tabs.Count;
        Show((Tab)next);
        _tabs[next].GrabFocus();
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        // the race list is asked for once a second while its tab shows; the answer comes back through RowsChanged
        if (_current == Tab.Race && (_askIn -= delta) <= 0)
        {
            _askIn = 1.0;
            _races?.RequestRows();
        }
        // the timers, once a second, and at once after a change (the events call Soon)
        if ((_tick -= delta) > 0) return;
        _tick = 1.0;
        Refresh();
    }

    private double _askIn;

    /// <summary>Something the page shows changed: refreshed on the next frame.</summary>
    private void Soon() => _tick = 0;

    private void Refresh()
    {
        switch (_current)
        {
            case Tab.Invites: RefreshInvites(); break;
            case Tab.BattleRoyale: RefreshBr(); break;
            case Tab.Race: RefreshRace(); break;
            case Tab.Versteckis: RefreshVs(); break;
        }
    }

    private void OnInvitesChanged()
    {
        _invitesDirty = true;
        _brShown = "";
        Soon();
    }

    private void OnNames()
    {
        foreach (var picker in _playerPickers) FillPlayers(picker);
    }

    // ---- shared bits ---------------------------------------------------------------------

    private readonly List<OptionButton> _playerPickers = new();

    /// <summary>A drop-down of the other players online, refreshed as the server answers.</summary>
    private OptionButton PlayerPicker()
    {
        var o = new OptionButton { CustomMinimumSize = new Vector2(220, 36), FocusMode = FocusModeEnum.All, ClipText = true, FitToLongestItem = false };
        _playerPickers.Add(o);
        FillPlayers(o);
        return o;
    }

    private void FillPlayers(OptionButton o)
    {
        string? was = o.Selected >= 0 && o.ItemCount > 0 ? o.GetItemText(o.Selected) : null;
        o.Clear();
        string me = GameShell.Instance?.PlayerName ?? "";
        var names = (_chat?.PlayerNames ?? Array.Empty<string>()).Where(n => !n.Equals(me, StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count == 0) o.AddItem("Nobody else online");
        foreach (var n in names) o.AddItem(n);
        o.Disabled = names.Count == 0;
        int keep = was == null ? -1 : names.IndexOf(was);
        o.Selected = keep >= 0 ? keep : 0;
    }

    private static string? Picked(OptionButton o) => o.Disabled || o.Selected < 0 ? null : o.GetItemText(o.Selected);

    /// <summary>A two-column body: the left for what is going on, the right for what to start.</summary>
    private static (Control Root, VBoxContainer Left, VBoxContainer Right) Columns()
    {
        var root = UiKit.HBox(24);
        var (ls, left) = UiKit.ScrollPage(6);
        var (rs, right) = UiKit.ScrollPage(6);
        ls.SizeFlagsStretchRatio = 1f;
        rs.SizeFlagsStretchRatio = 1f;
        root.AddChild(ls);
        root.AddChild(new VSeparator());
        root.AddChild(rs);
        return (root, left, right);
    }

    /// <summary>A row of rows' worth: a card-like line with text on the left and buttons on the right.</summary>
    private static (HBoxContainer Row, VBoxContainer Text) ListRow(string title, string? detail, Color? color = null)
    {
        var row = UiKit.HBox(8);
        row.CustomMinimumSize = new Vector2(0, 40);
        var text = UiKit.VBox(0);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.Alignment = BoxContainer.AlignmentMode.Center;
        var t = UiKit.Text(title, UiTheme.FontBody, color ?? UiTheme.Text);
        t.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.AddChild(t);
        if (!string.IsNullOrEmpty(detail)) text.AddChild(UiKit.Text(detail, UiTheme.FontSmall, UiTheme.TextDim));
        row.AddChild(text);
        return (row, text);
    }

    private static void Clear(Container c)
    {
        foreach (var child in c.GetChildren())
        {
            c.RemoveChild(child);
            child.QueueFree();
        }
    }

    // ---- Invites -------------------------------------------------------------------------

    private VBoxContainer _incoming = null!, _sent = null!, _party = null!;
    private OptionButton _invitePicker = null!;
    private bool _invitesDirty = true;
    private string _invitesShown = "";

    private Control BuildInvites()
    {
        var (root, left, right) = Columns();
        left.AddChild(UiKit.Section("Waiting for you"));
        _incoming = UiKit.VBox(4);
        left.AddChild(_incoming);
        left.AddChild(UiKit.Spacer(10));
        left.AddChild(UiKit.Section("You sent"));
        _sent = UiKit.VBox(4);
        left.AddChild(_sent);

        right.AddChild(UiKit.Section("Ask someone"));
        var pickRow = UiKit.HBox(8);
        pickRow.AddChild(UiKit.Text("Player", UiTheme.FontBody, UiTheme.TextDim));
        pickRow.AddChild(UiKit.Spacer(expand: true));
        _invitePicker = PlayerPicker();
        pickRow.AddChild(_invitePicker);
        right.AddChild(pickRow);
        void Ask(string text, string verb, string hint)
        {
            var b = UiKit.Button(text);
            b.TooltipText = hint;
            b.Pressed += () => { if (Picked(_invitePicker) is { } who) Run($"/{verb} {who}"); };
            right.AddChild(b);
        }
        Ask("Ask to teleport to them", "tpa", "They accept, and you go to them (/tpa)");
        Ask("Ask them to teleport to you", "tphere", "They accept, and they come to you (/tphere)");
        Ask("Invite to my party", "party invite", "A party plays a Battle Royale as one team (/party invite)");
        Ask("Challenge to a fist fight", "fight", "Within 12 m, both on foot (/fight)");
        Ask("Challenge to an aura battle", "aura", "Within 12 m, both on foot: plan a combo, cut it on the beat (/aura)");

        right.AddChild(UiKit.Spacer(10));
        right.AddChild(UiKit.Section("Party"));
        _party = UiKit.VBox(4);
        right.AddChild(_party);
        return root;
    }

    private void RefreshInvites()
    {
        var invites = InviteService.Client;
        var parties = Parties.Client;
        // rebuilt only when what it shows changes (the seconds left are redrawn in place below)
        string shown = invites == null ? "" : string.Join(",", invites.Incoming.Select(v => v.Id)) + "|" + string.Join(",", invites.Sent.Select(v => v.Id))
            + "|" + string.Join(",", parties?.Members.Select(m => m.Name) ?? Array.Empty<string>());
        if (!_invitesDirty && shown == _invitesShown)
        {
            foreach (var label in _secondsLabels) label.Value.Text = $"{Mathf.CeilToInt((float)label.Key.Left)} s";
            return;
        }
        _invitesDirty = false;
        _invitesShown = shown;
        _secondsLabels.Clear();
        Clear(_incoming);
        Clear(_sent);
        Clear(_party);
        if (invites == null) return;

        foreach (var v in invites.Incoming)
        {
            var (row, text) = ListRow(v.Title, v.Detail);
            Seconds(text, v);
            var accept = UiKit.Button("Accept", primary: true);
            int id = v.Id;
            accept.Pressed += () => { invites.Accept(id); _status.Text = "…"; };
            var decline = UiKit.Button("Decline");
            decline.Pressed += () => invites.Decline(id);
            row.AddChild(accept);
            row.AddChild(decline);
            _incoming.AddChild(row);
        }
        if (_incoming.GetChildCount() == 0) _incoming.AddChild(UiKit.Text("Nothing waiting.", UiTheme.FontSmall, UiTheme.TextFaint));

        foreach (var v in invites.Sent)
        {
            var (row, text) = ListRow(v.Title, v.Peer == Invite.Everyone ? "To everyone online" : $"To {v.PeerName}", UiTheme.TextDim);
            Seconds(text, v);
            var withdraw = UiKit.Button("Withdraw");
            int id = v.Id;
            withdraw.Pressed += () => invites.Withdraw(id);
            row.AddChild(withdraw);
            _sent.AddChild(row);
        }
        if (_sent.GetChildCount() == 0) _sent.AddChild(UiKit.Text("Nothing sent.", UiTheme.FontSmall, UiTheme.TextFaint));

        if (parties is { InParty: true })
        {
            foreach (var (peer, name) in parties.Members)
            {
                var (row, _) = ListRow(peer == parties.Members[0].Peer ? $"{name}  (leader)" : name, null);
                // playing together (#1266): hold hands or rope up with a party member near you
                if (peer != Multiplayer.GetUniqueId() && Adventure.Together.TogetherService.Instance is { } together && together.MayAskLink(peer))
                {
                    string who = name;
                    var hands = UiKit.Button("Hold hands");
                    hands.Pressed += () => Run($"/rope hands {who}");
                    var rope = UiKit.Button("Rope up");
                    rope.Pressed += () => Run($"/rope {who}");
                    row.AddChild(hands);
                    row.AddChild(rope);
                }
                if (parties.IsLeader && peer != Multiplayer.GetUniqueId())
                {
                    var kick = UiKit.Button("Put out");
                    string who = name;
                    kick.Pressed += () => Run($"/party kick {who}");
                    row.AddChild(kick);
                }
                _party.AddChild(row);
            }
            var leave = UiKit.Button("Leave the party");
            leave.Pressed += () => Run("/party leave");
            _party.AddChild(leave);
        }
        else _party.AddChild(UiKit.Text("Not in a party. Invite someone to make one.", UiTheme.FontSmall, UiTheme.TextFaint));
    }

    private readonly Dictionary<InviteView, Label> _secondsLabels = new();

    private void Seconds(VBoxContainer text, InviteView v)
    {
        var l = UiKit.Text($"{Mathf.CeilToInt((float)v.Left)} s", UiTheme.FontTiny, UiTheme.TextFaint);
        text.AddChild(l);
        _secondsLabels[v] = l;
    }
}
