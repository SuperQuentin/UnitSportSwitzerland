using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Ui;

/// <summary>
/// A server's console from the menus (#1088): the admin password, then a command line and what the server's
/// console shows. It talks through <see cref="ConsoleLink"/>, which skips the version check, so it reaches a
/// server of any version since the link exists: the way to <c>update</c> a server this game is too new for.
///
/// <para>
/// The link lives on its own branch of the tree with its own <see cref="SceneMultiplayer"/>
/// (<c>/root/RemoteConsole/Main/World/ConsoleLink</c>, the swarm bots' trick), so the paths match the
/// server's <c>/root/Main/World</c> and nothing of the game's world is needed.
/// </para>
/// </summary>
public partial class ServerConsoleScreen : Screen
{
    private const string BranchName = "RemoteConsole";
    private const double AnswerSeconds = 6;

    /// <summary>The passwords typed this run, per server: asked once per game start, never saved.</summary>
    private static readonly Dictionary<string, string> Passwords = new();

    private readonly string _endpoint;
    private LineEdit _password = null!, _command = null!;
    private Button _connect = null!, _send = null!;
    private RichTextLabel _output = null!;
    private Label _state = null!;

    private Node? _branch;
    private SceneMultiplayer? _api;
    private ConsoleLink? _link;
    private bool _open;
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
        var (body, header) = Framed("Server console", $"{_endpoint}  ·  admins: the server's own console, whatever its version", new Vector2(900, 580));
        _state = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        header.AddChild(_state);

        var login = UiKit.HBox(8);
        login.AddChild(UiKit.Text("Admin password", UiTheme.FontSmall, UiTheme.TextDim));
        _password = new LineEdit { Secret = true, PlaceholderText = "the server's --admin-password", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = Passwords.GetValueOrDefault(_endpoint, "") };
        Focusable(_password);
        _password.TextSubmitted += _ => Connect();
        login.AddChild(_password);
        _connect = UiKit.Button("Connect", primary: true);
        _connect.Pressed += Connect;
        login.AddChild(_connect);
        body.AddChild(login);

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
        _command = new LineEdit { PlaceholderText = "update, who, say <text>, kick <name>, help…", SizeFlagsHorizontal = SizeFlags.ExpandFill, Editable = false };
        Focusable(_command);
        _command.TextSubmitted += _ => SendLine();
        _command.GuiInput += OnCommandKey;
        line.AddChild(_command);
        _send = UiKit.Button("Send");
        _send.Disabled = true;
        _send.Pressed += SendLine;
        line.AddChild(_send);
        body.AddChild(line);

        Print("Commands run as the server's own console, with operator rights. A server older than this console never answers.");
        SetState("not connected", UiTheme.TextDim);
    }

    private static void Focusable(LineEdit edit)
    {
        edit.FocusEntered += () => UiFocus.Set(edit, true);
        edit.FocusExited += () => UiFocus.Set(edit, false);
    }

    public override void OnShown()
    {
        if (_password.Text.Length == 0) _password.GrabFocus();
        else _connect.GrabFocus();
    }

    public override void OnHidden() => Close();

    public override void _ExitTree() => Close();

    public override void _Process(double delta)
    {
        // nobody answered the first line: a server from before the link, or not there at all
        if (_askedAt >= 0 && !_open && Time.GetTicksMsec() / 1000.0 - _askedAt > AnswerSeconds)
        {
            _askedAt = -1;
            Print("No answer. The server is either older than the remote console (update it once over SSH: "
                + "tmux attach -t unitsport, then update), or not reachable.");
            Close();
            SetState("no answer", UiTheme.Bad);
        }
    }

    private void Connect()
    {
        string password = _password.Text;
        if (password.Length == 0) { _password.GrabFocus(); return; }
        Close();
        Passwords[_endpoint] = password;

        var (host, port) = NetworkManager.ParseEndpoint(_endpoint);
        _api = new SceneMultiplayer();
        // before the branch enters the tree: its nodes register with the multiplayer that owns their path
        GetTree().SetMultiplayer(_api, "/root/" + BranchName);
        _branch = new Node { Name = BranchName };
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
        _api.MultiplayerPeer = peer;
        _api.ConnectedToServer += () =>
        {
            _link?.Send(password, "");   // opens the link; the server answers with who it is
            _askedAt = Time.GetTicksMsec() / 1000.0;
        };
        _api.ConnectionFailed += () =>
        {
            Print($"Could not reach {host}:{port}.");
            Close();
            SetState("unreachable", UiTheme.Bad);
        };
        _api.ServerDisconnected += () =>
        {
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
        _link.Send(Passwords.GetValueOrDefault(_endpoint, ""), line);
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

    internal void ConnectWith(string password)
    {
        _password.Text = password;
        Connect();
    }

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
        if (_api?.MultiplayerPeer is { } peer && peer is not OfflineMultiplayerPeer) peer.Close();
        _api = null;
        _link = null;
        if (_branch != null && IsInstanceValid(_branch))
        {
            // out of the tree now, so the next connect's branch gets the same name (the RPC paths)
            _branch.GetParent()?.RemoveChild(_branch);
            _branch.QueueFree();
            if (IsInsideTree()) GetTree().SetMultiplayer(null, "/root/" + BranchName);
        }
        _branch = null;
        if (_state != null) SetState("not connected", UiTheme.TextDim);
    }
}
