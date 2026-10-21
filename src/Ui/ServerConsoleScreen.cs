using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Ui;

/// <summary>
/// Joining a server for its chat and commands only (#1088): as if fully joined, under this player's name and with
/// the rights that name has there, but with no body and no world. It talks through <see cref="ConsoleLink"/>,
/// which skips the version check, so it reaches a server of any version since the link exists: the way to
/// <c>/update</c> a server this game is too new for.
///
/// <para>
/// The link lives on its own branch of the tree with its own <see cref="SceneMultiplayer"/>
/// (<c>/root/RemoteConsole&lt;n&gt;/Main/World/ConsoleLink</c>, the swarm bots' trick), so the paths match the
/// server's <c>/root/Main/World</c> and nothing of the game's world is needed.
/// </para>
/// </summary>
public partial class ServerConsoleScreen : Screen
{
    private const string BranchName = "RemoteConsole";
    private const double AnswerSeconds = 6;

    private readonly string _endpoint;
    private LineEdit _command = null!;
    private Button _connect = null!, _send = null!;
    private RichTextLabel _output = null!;
    private Label _state = null!;

    private Node? _branch;
    private int _links;
    private SceneMultiplayer? _api;
    private ConsoleLink? _link;
    private string _name = "";
    private bool _open, _started;
    private double _askedAt = -1;
    private readonly List<string> _history = new();
    private int _historyAt;

    private ServerConsoleScreen(string endpoint)
    {
        _endpoint = endpoint;
        Name = "ServerConsole";
    }

    public static ServerConsoleScreen Create(string endpoint) => new(endpoint);

    public override void _Ready()
    {
        _name = Shell.PlayerName;
        var (body, header) = Framed("Server console", $"{_endpoint}  ·  the chat and its commands, as {(_name.Length > 0 ? _name : "?")}, whatever the server's version",
            new Vector2(900, 580));
        _state = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        header.AddChild(_state);
        _connect = UiKit.Button("Connect", primary: true);
        _connect.Pressed += Connect;
        header.AddChild(_connect);

        _output = new RichTextLabel
        {
            ScrollFollowing = true, SelectionEnabled = true, BbcodeEnabled = false, FocusMode = FocusModeEnum.None,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _output.AddThemeFontOverride("normal_font", new SystemFont { FontNames = ["Consolas", "Courier New", "monospace"] });
        _output.AddThemeFontSizeOverride("normal_font_size", UiTheme.FontSmall);
        var outputPanel = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        outputPanel.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0, 0, 0, 0.45f), 8, 10, 8));
        outputPanel.AddChild(_output);
        body.AddChild(outputPanel);

        var line = UiKit.HBox(8);
        _command = new LineEdit { PlaceholderText = "/update, /who, /login <password>, /help, or a message to everyone…", SizeFlagsHorizontal = SizeFlags.ExpandFill, Editable = false };
        Focusable(_command);
        _command.TextSubmitted += _ => SendLine();
        _command.GuiInput += OnCommandKey;
        line.AddChild(_command);
        _send = UiKit.Button("Send");
        _send.Disabled = true;
        _send.Pressed += SendLine;
        line.AddChild(_send);
        body.AddChild(line);

        Print("You join the server's chat with no body: commands and messages as in game, with your rights there "
            + "(an admin by name, or after /login). A server older than this never answers.");
        SetState("not connected", UiTheme.TextDim);
    }

    private static void Focusable(LineEdit edit)
    {
        edit.FocusEntered += () => UiFocus.Set(edit, true);
        edit.FocusExited += () => UiFocus.Set(edit, false);
    }

    public override void OnShown()
    {
        _connect.GrabFocus();
        // opening the console is joining it, the first time
        if (!_started)
        {
            _started = true;
            Connect();
        }
    }

    public override void OnHidden() => Close();

    public override void _ExitTree() => Close();

    public override void _Process(double delta)
    {
        // nobody answered the first line: a server from before the link, or not there at all
        if (_askedAt >= 0 && !_open && RealClock.Now - _askedAt > AnswerSeconds)
        {
            _askedAt = -1;
            Print("No answer. The server is either older than the server console (update it once over SSH: "
                + "tmux attach -t unitsport, then update), or not reachable.");
            Close();
            SetState("no answer", UiTheme.Bad);
        }
    }

    private void Connect()
    {
        if (_name.Length == 0)
        {
            Print("Choose your name on the Multiplayer screen first: the server knows you by it.");
            return;
        }
        Close();
        string name = _name;

        var (host, port) = NetworkManager.ParseEndpoint(_endpoint);
        var api = _api = new SceneMultiplayer();
        // RPC paths are relative to the multiplayer's root, so each link has a branch of its own name: the last
        // one may still be in the tree, freed a frame later (Close)
        string root = $"{BranchName}{++_links}";
        // before the branch enters the tree: its nodes register with the multiplayer that owns their path
        GetTree().SetMultiplayer(api, "/root/" + root);
        _branch = new Node { Name = root };
        var main = new Node { Name = "Main" };
        var world = new Node { Name = "World" };
        _branch.AddChild(main);
        main.AddChild(world);
        _link = ConsoleLink.CreateClient();
        _link.LineReceived += OnLine;
        world.AddChild(_link);
        GetTree().Root.AddChild(_branch);

        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(host, port);
        if (err != Error.Ok)
        {
            Print($"Cannot connect to {host}:{port}: {err}.");
            Close();
            return;
        }
        api.MultiplayerPeer = peer;
        api.ConnectedToServer += () =>
        {
            if (api != _api) return;
            _link?.Submit(name, "");   // joins; the server answers with who we are there
            _askedAt = RealClock.Now;
        };
        api.ConnectionFailed += () =>
        {
            if (api != _api) return;
            Print($"Could not reach {host}:{port}.");
            Close();
            SetState("unreachable", UiTheme.Bad);
        };
        api.ServerDisconnected += () =>
        {
            if (api != _api) return;
            Print("Disconnected.");
            Close();
            SetState("disconnected", UiTheme.TextDim);
        };
        Print($"Connecting to {host}:{port}…");
        SetState("connecting…", UiTheme.Warn);
    }

    private void OnLine(string line)
    {
        if (!_open)
        {
            _open = true;
            _askedAt = -1;
            _command.Editable = true;
            _send.Disabled = false;
            _command.GrabFocus();
            SetState("connected", UiTheme.Good);
        }
        Print(line);
    }

    private void SendLine()
    {
        string line = _command.Text.Trim();
        if (line.Length == 0 || !_open || _link == null) return;
        _command.Text = "";
        _history.Remove(line);
        _history.Add(line);
        _historyAt = _history.Count;
        Print("> " + line);
        _link.Submit(_name, line);
    }

    /// <summary>Up and Down walk the lines sent this session.</summary>
    private void OnCommandKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key || _history.Count == 0) return;
        if (key.Keycode == Key.Up) _historyAt = Math.Max(0, _historyAt - 1);
        else if (key.Keycode == Key.Down) _historyAt = Math.Min(_history.Count, _historyAt + 1);
        else return;
        _command.Text = _historyAt < _history.Count ? _history[_historyAt] : "";
        _command.CaretColumn = _command.Text.Length;
        _command.AcceptEvent();
    }

    private void Print(string line) => _output.AddText(line + "\n");

    // ---- for ConsoleLinkCheck --------------------------------------------------------------------

    internal bool IsOpen => _open;
    internal string Output => _output.GetParsedText();


    internal void Submit(string line)
    {
        _command.Text = line;
        SendLine();
    }

    private void SetState(string text, Color colour)
    {
        _state.Text = text;
        _state.AddThemeColorOverride("font_color", colour);
    }

    /// <summary>Closes the link and frees its branch; the screen stays, ready to connect again.</summary>
    private void Close()
    {
        _open = false;
        _askedAt = -1;
        if (_command != null) { _command.Editable = false; _send.Disabled = true; }
        // Torn down a frame later: Close runs inside the multiplayer's own poll (ServerDisconnected), and closing its
        // peer or unregistering it there crashed the windowed game (signal 11). Its callbacks check _api first.
        var api = _api;
        var branch = _branch;
        var tree = IsInsideTree() ? GetTree() : null;
        _api = null;
        _link = null;
        _branch = null;
        if (api != null || branch != null)
            Callable.From(() =>
            {
                if (api?.MultiplayerPeer is { } peer && peer is not OfflineMultiplayerPeer) peer.Close();
                if (branch != null && IsInstanceValid(branch))
                {
                    tree?.SetMultiplayer(null, branch.GetPath());
                    branch.QueueFree();
                }
            }).CallDeferred();
        if (_state != null) SetState("not connected", UiTheme.TextDim);
    }
}
