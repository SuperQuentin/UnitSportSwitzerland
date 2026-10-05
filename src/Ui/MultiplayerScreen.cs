using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Ui;

/// <summary>
/// Multiplayer: who you are (asked the first time, an edit button after), your saved and recent
/// servers with whether they are up, how full and how far, the servers found on your network,
/// a direct address field, and hosting a game from this machine.
///
/// <para>
/// Status comes from the UDP query on game port + 1 (<see cref="ServerQuery"/>), broadcast for
/// the LAN list and unicast per saved server; servers that only advertise over mDNS
/// (<see cref="LanDiscovery"/>, the Linux deploy) are merged in. Rows are updated in place,
/// not rebuilt, so the pad focus does not jump while the numbers change.
/// </para>
/// </summary>
public partial class MultiplayerScreen : Screen
{
    private readonly ServerQuery _query = new();
    private readonly LanDiscovery _mdns = new();
    private VBoxContainer _saved = null!, _lan = null!;
    private Label _lanStatus = null!, _name = null!;
    private LineEdit _address = null!;
    private Button _firstJoin = null!;
    private PanelContainer? _banner;
    private double _sinceBroadcast = 10, _sinceProbe = 10;
    private readonly Dictionary<string, ServerRow> _savedRows = new();
    private readonly Dictionary<string, ServerRow> _lanRows = new();

    public static MultiplayerScreen Create() => new() { Name = "Multiplayer" };

    public override void _Ready()
    {
        var (body, header) = Framed("Multiplayer", Platform.CanSpawnProcesses ? "Join a server, or host one from this machine" : "Join a server", new Vector2(1000, 590));

        // --- the name chip, top right
        var chip = UiKit.HBox(6);
        chip.AddChild(new TextureRect { Texture = Icons.User, StretchMode = TextureRect.StretchModeEnum.KeepCentered, Modulate = UiTheme.TextDim, CustomMinimumSize = new Vector2(20, 20) });
        _name = UiKit.Text("", UiTheme.FontBody, UiTheme.Text, bold: true);
        chip.AddChild(_name);
        var edit = UiKit.IconButton(Icons.Pencil, "Change your name");
        edit.Pressed += () => AskName(mandatory: false);
        chip.AddChild(edit);
        header.AddChild(chip);

        if (Shell.TakeError() is { } error) body.AddChild(_banner = Banner(error));

        var columns = UiKit.HBox(18);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddChild(columns);

        // --- left: saved and LAN lists
        var (scroll, rows) = UiKit.ScrollPage(6);
        scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        columns.AddChild(scroll);

        var savedHead = UiKit.HBox(8);
        var savedTitle = UiKit.Section("Saved and recent");
        savedTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        savedTitle.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        savedHead.AddChild(savedTitle);
        var add = UiKit.IconButton(Icons.Plus, "Add a server", 30);
        add.Pressed += () => EditServer(null);
        savedHead.AddChild(add);
        rows.AddChild(savedHead);
        _saved = UiKit.VBox(4);
        rows.AddChild(_saved);

        rows.AddChild(UiKit.Spacer(8));
        var lanHead = UiKit.HBox(8);
        var lanTitle = UiKit.Section("On your network");
        lanTitle.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        lanHead.AddChild(lanTitle);
        _lanStatus = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint);
        _lanStatus.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        lanHead.AddChild(_lanStatus);
        rows.AddChild(lanHead);
        _lan = UiKit.VBox(4);
        rows.AddChild(_lan);

        // --- right: direct connect and host
        var side = UiKit.VBox(14);
        side.CustomMinimumSize = new Vector2(290, 0);
        columns.AddChild(side);

        var direct = UiKit.VBox(8);
        direct.AddChild(UiKit.Text("Direct connect", 17, UiTheme.Text, bold: true));
        direct.AddChild(UiKit.Text("An address or name, with :port if not 7777", UiTheme.FontTiny, UiTheme.TextDim, wrap: true));
        _address = new LineEdit { PlaceholderText = "192.168.1.20 or host:port", Text = GameSettings.Current.LastHost == "127.0.0.1" ? "" : GameSettings.Current.LastHost };
        _address.FocusEntered += () => UiFocus.Set(_address, true);
        _address.FocusExited += () => UiFocus.Set(_address, false);
        _address.TextSubmitted += _ => JoinTyped();
        direct.AddChild(_address);
        var join = UiKit.Button("Join", primary: true);
        join.Pressed += JoinTyped;
        direct.AddChild(join);
        side.AddChild(UiKit.Card(direct, margin: 16));

        // hosting starts a server process, which a phone cannot do (#63)
        if (Platform.CanSpawnProcesses) side.AddChild(UiKit.Card(HostCard(), margin: 16));

        RebuildSaved();
        ShowName();
    }

    private Control HostCard()
    {
        var host = UiKit.VBox(8);
        var hostTitle = UiKit.HBox(8);
        hostTitle.AddChild(new TextureRect { Texture = Icons.Host, StretchMode = TextureRect.StretchModeEnum.KeepCentered, Modulate = UiTheme.Amber, CustomMinimumSize = new Vector2(20, 20) });
        hostTitle.AddChild(UiKit.Text("Host a game", 17, UiTheme.Text, bold: true));
        host.AddChild(hostTitle);
        host.AddChild(UiKit.Text("Runs a server on this machine and joins it. Friends on your network see it in their list.",
            UiTheme.FontTiny, UiTheme.TextDim, wrap: true));
        var hostButton = UiKit.Button("Host…");
        hostButton.Pressed += HostDialog;
        host.AddChild(hostButton);
        return host;
    }

    public override void OnShown()
    {
        _query.Start();
        if (GameSettings.Current.LanDiscovery) _mdns.Start();
        _sinceBroadcast = _sinceProbe = 10;
        if (Shell.PlayerName.Length == 0) AskName(mandatory: true);
        else if (_firstJoin != null && IsInstanceValid(_firstJoin)) _firstJoin.CallDeferred(Control.MethodName.GrabFocus);
        else _address.CallDeferred(Control.MethodName.GrabFocus);
    }

    public override void OnHidden()
    {
        _query.Stop();
        _mdns.Stop();
    }

    public override void _ExitTree()
    {
        _query.Dispose();
        _mdns.Dispose();
    }

    public override void _Process(double delta)
    {
        if (!_query.Running) return;
        bool lanOn = GameSettings.Current.LanDiscovery;
        if ((_sinceBroadcast += delta) >= 2 && lanOn) { _sinceBroadcast = 0; _query.Broadcast(); }
        if ((_sinceProbe += delta) >= 4)
        {
            _sinceProbe = 0;
            foreach (var s in Shell.Book.Servers) _query.Probe(s.Endpoint);
        }
        bool changed = _query.Poll();
        if (_mdns.Running) changed |= _mdns.Poll();
        if (changed || _lanRows.Count == 0) RefreshStatus(lanOn);
    }

    // ---- name -----------------------------------------------------------------------------------

    private void ShowName() => _name.Text = Shell.PlayerName.Length > 0 ? Shell.PlayerName : "no name yet";

    private void AskName(bool mandatory)
    {
        Modal.Prompt(this, mandatory ? "Choose your name" : "Your name",
            mandatory ? "This is how other players see you. You can change it later with the pencil." : "Letters, digits, - and _; up to 20.",
            Shell.PlayerName, "e.g. Heidi_92",
            text =>
            {
                string t = text.Trim();
                if (t.Length < 2) return "At least 2 characters.";
                if (PlayerRegistry.Sanitize(t) != t) return "Only letters, digits, - and _ (no spaces), up to 20.";
                return null;
            },
            text =>
            {
                GameSettings.Current.PlayerName = text.Trim();
                GameSettings.Current.Save();
                ShowName();
                _address.CallDeferred(Control.MethodName.GrabFocus);
            },
            mandatory: mandatory, okText: mandatory ? "Continue" : "Save", maxLength: 20);
    }

    // ---- lists ----------------------------------------------------------------------------------

    private void RebuildSaved()
    {
        foreach (var c in _saved.GetChildren()) c.QueueFree();
        _savedRows.Clear();
        _firstJoin = null!;
        var servers = Shell.Book.Servers;
        if (servers.Count == 0)
            _saved.AddChild(UiKit.Text("Servers you join or add appear here.", UiTheme.FontSmall, UiTheme.TextFaint));
        foreach (var s in servers)
        {
            var row = new ServerRow(s.Name, s.Endpoint, ServerBook.Ago(s.LastPlayed));
            row.Joined += () => Join(s.Endpoint);
            var star = UiKit.IconButton(s.Favorite ? Icons.StarFilled : Icons.Star, s.Favorite ? "Unpin" : "Pin to the top", 30);
            if (s.Favorite) star.AddThemeColorOverride("icon_normal_color", UiTheme.Amber);
            star.Pressed += () => { Shell.Book.SetFavorite(s.Endpoint, !s.Favorite); RebuildSaved(); };
            var edit = UiKit.IconButton(Icons.Pencil, "Edit", 30);
            edit.Pressed += () => EditServer(s);
            var remove = UiKit.IconButton(Icons.Trash, "Remove", 30);
            remove.Pressed += () => Modal.Confirm(this, "Remove server?", $"{s.Name} ({s.Endpoint}) is taken off your list.", "Remove",
                () => { Shell.Book.Remove(s.Endpoint); RebuildSaved(); }, danger: true);
            row.AddActions(star, edit, remove);
            _saved.AddChild(row);
            _savedRows[s.Endpoint] = row;
            _firstJoin ??= row.JoinButton;
        }
        RefreshStatus(GameSettings.Current.LanDiscovery);
    }

    private void RefreshStatus(bool lanOn)
    {
        foreach (var (endpoint, row) in _savedRows)
        {
            var r = _query.Probed(endpoint);
            if (r == null && _query.Lan.FirstOrDefault(l => ServerBook.Same(l.Endpoint, endpoint)) is { } lanHit) r = lanHit;
            row.SetStatus(r);
        }

        // the LAN list: UDP answers, then mDNS-only servers; saved ones are not listed twice
        var found = new List<(string Name, string Endpoint, QueryResult? Status)>();
        if (lanOn)
        {
            foreach (var r in _query.Lan) found.Add((r.Status.Name, r.Endpoint, r));
            foreach (var m in _mdns.Servers)
                if (!found.Any(f => ServerBook.Same(f.Endpoint, m.Endpoint))) found.Add((m.Name, m.Endpoint, null));
        }
        var keys = found.Select(f => f.Endpoint).ToHashSet();
        foreach (var gone in _lanRows.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _lanRows[gone].QueueFree();
            _lanRows.Remove(gone);
        }
        foreach (var f in found)
        {
            if (!_lanRows.TryGetValue(f.Endpoint, out var row))
            {
                row = new ServerRow(f.Name, f.Endpoint, f.Status?.Local == true ? "this computer" : "LAN");
                string endpoint = f.Endpoint, name = f.Name;
                row.Joined += () => Join(endpoint, name);
                var save = UiKit.IconButton(Icons.Star, "Save to your list", 30);
                save.Pressed += () =>
                {
                    Shell.Book.AddOrUpdate(new SavedServer { Name = name, Endpoint = endpoint, Favorite = true });
                    RebuildSaved();
                };
                row.AddActions(save);
                _lan.AddChild(row);
                _lanRows[f.Endpoint] = row;
            }
            row.SetStatus(f.Status);
        }
        _lanStatus.Text = !lanOn ? "off (Settings › Gameplay)" : found.Count == 0 ? "searching…" : $"{found.Count} found";
    }

    // ---- actions --------------------------------------------------------------------------------

    private void JoinTyped()
    {
        string a = _address.Text.Trim();
        if (a.Length == 0) { _address.GrabFocus(); return; }
        Join(a);
    }

    private void Join(string endpoint, string? name = null)
    {
        if (Shell.PlayerName.Length == 0) { AskName(mandatory: true); return; }
        Shell.Join(endpoint, name);
    }

    private void EditServer(SavedServer? existing)
    {
        var form = UiKit.VBox(8);
        form.AddChild(UiKit.Text("Name", UiTheme.FontSmall, UiTheme.TextDim));
        var name = new LineEdit { Text = existing?.Name ?? "", PlaceholderText = "Friday night", MaxLength = 40 };
        form.AddChild(name);
        form.AddChild(UiKit.Text("Address", UiTheme.FontSmall, UiTheme.TextDim));
        var address = new LineEdit { Text = existing?.Endpoint ?? "", PlaceholderText = "host or host:port" };
        form.AddChild(address);
        var (_, recheck) = Modal.Form(this, existing == null ? "Add a server" : "Edit server", null, form, "Save",
            () => address.Text.Trim().Length == 0 ? "An address is needed." : null,
            () =>
            {
                string ep = address.Text.Trim();
                Shell.Book.AddOrUpdate(new SavedServer
                {
                    Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : ep,
                    Endpoint = ep,
                    Favorite = existing?.Favorite ?? true,
                    LastPlayed = existing?.LastPlayed,
                }, existing?.Endpoint);
                RebuildSaved();
            }, existing == null ? name : address);
        address.TextChanged += _ => recheck();
    }

    private void HostDialog()
    {
        if (Shell.PlayerName.Length == 0) { AskName(mandatory: true); return; }
        var form = UiKit.VBox(8);
        form.AddChild(UiKit.Text("Server name", UiTheme.FontSmall, UiTheme.TextDim));
        var name = new LineEdit { Text = $"{Shell.PlayerName}'s game", MaxLength = 40 };
        form.AddChild(name);
        form.AddChild(UiKit.Text("Port (UDP, and the next one for the server list)", UiTheme.FontSmall, UiTheme.TextDim));
        var port = new LineEdit { Text = NetworkManager.DefaultPort.ToString(), MaxLength = 5 };
        form.AddChild(port);
        var lan = new CheckBox { Text = "Visible to players on your network", ButtonPressed = true };
        form.AddChild(lan);
        var (_, recheck) = Modal.Form(this, "Host a game",
            "Your machine runs the server while you play. Leaving the game stops it for everyone.", form, "Start server",
            () => int.TryParse(port.Text, out int p) && p is >= 1024 and <= 65534 ? null : "Pick a port between 1024 and 65534.",
            () => Shell.Host(name.Text.Trim().Length > 0 ? name.Text.Trim() : $"{Shell.PlayerName}'s game", int.Parse(port.Text), lan.ButtonPressed));
        port.TextChanged += _ => recheck();
    }

    private PanelContainer Banner(string message)
    {
        var line = UiKit.HBox(10);
        var text = UiKit.Text(message, UiTheme.FontSmall, new Color(1, 0.86f, 0.84f), wrap: true);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        line.AddChild(text);
        var close = UiKit.IconButton(Icons.Close, "Dismiss", 28);
        line.AddChild(close);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(UiTheme.Bad, 0.18f), 8, 14, 8, new Color(UiTheme.Bad, 0.5f), 1));
        panel.AddChild(line);
        close.Pressed += () => panel.QueueFree();
        return panel;
    }
}

/// <summary>One server in a list: status dot, name, address, players, ping; the whole row joins.</summary>
public partial class ServerRow : HBoxContainer
{
    public event Action? Joined;
    public Button JoinButton { get; }
    private readonly TextureRect _dot;
    private readonly Label _players, _ping, _detail;
    private readonly string _endpoint, _note;

    public ServerRow(string name, string endpoint, string note)
    {
        _endpoint = endpoint;
        _note = note;
        AddThemeConstantOverride("separation", 2);
        JoinButton = new Button { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 54), FocusMode = FocusModeEnum.All, TooltipText = $"Join {endpoint}" };
        JoinButton.Pressed += () => Joined?.Invoke();
        AddChild(JoinButton);

        var inner = UiKit.HBox(12);
        inner.SetAnchorsPreset(LayoutPreset.FullRect);
        inner.OffsetLeft = 14; inner.OffsetRight = -14;
        inner.MouseFilter = MouseFilterEnum.Ignore;
        JoinButton.AddChild(inner);

        _dot = UiKit.StatusDot(UiTheme.TextFaint);
        inner.AddChild(_dot);
        var names = UiKit.VBox(0);
        names.Alignment = BoxContainer.AlignmentMode.Center;
        names.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        names.MouseFilter = MouseFilterEnum.Ignore;
        var title = UiKit.Text(name, UiTheme.FontBody, UiTheme.Text, bold: true);
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        names.AddChild(title);
        _detail = UiKit.Text($"{endpoint}  ·  {note}", UiTheme.FontTiny, UiTheme.TextFaint);
        names.AddChild(_detail);
        inner.AddChild(names);
        _players = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, align: HorizontalAlignment.Right);
        _players.CustomMinimumSize = new Vector2(64, 0);
        _players.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        inner.AddChild(_players);
        _ping = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, align: HorizontalAlignment.Right);
        _ping.CustomMinimumSize = new Vector2(64, 0);
        _ping.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        inner.AddChild(_ping);
    }

    public void AddActions(params Button[] buttons)
    {
        foreach (var b in buttons)
        {
            b.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            AddChild(b);
        }
    }

    public void SetStatus(QueryResult? r)
    {
        if (r == null)
        {
            _dot.Texture = UiTheme.Dot(UiTheme.TextFaint, 10);
            _players.Text = "";
            _ping.Text = "no reply";
            _ping.AddThemeColorOverride("font_color", UiTheme.TextFaint);
            return;
        }
        var colour = r.PingMs < 60 ? UiTheme.Good : r.PingMs < 150 ? UiTheme.Warn : UiTheme.Bad;
        bool full = r.Status.Players >= r.Status.Max;
        _dot.Texture = UiTheme.Dot(full ? UiTheme.Warn : UiTheme.Good, 10);
        _players.Text = $"{r.Status.Players}/{r.Status.Max}";
        _ping.Text = $"{r.PingMs} ms";
        _ping.AddThemeColorOverride("font_color", colour);
        string world = r.Status.World == "generated" ? "  ·  generated world" : "";
        string version = r.Status.Version.Length > 0 ? $"  ·  v{r.Status.Version}" : "";
        // another wire protocol: the server would refuse this client (Net/Handshake)
        if (r.Status.Wire != Net.Handshake.Protocol)
        {
            version += "  ·  other game version";
            _dot.Texture = UiTheme.Dot(UiTheme.Bad, 10);
        }
        _detail.Text = $"{_endpoint}  ·  {_note}{version}{world}";
    }
}
