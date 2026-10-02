using Godot;
using UnitSport.Net;
using UnitSport.Ui;

namespace UnitSport.Core;

/// <summary>
/// The client application around the world (<c>docs/notes/ui/screens.md</c>): the title screen
/// and its pages, the loading screen, the in-game pause menu, hosting a server from the menu,
/// and leaving a world to come back here.
///
/// <para>
/// The world is not built until a mode is picked: until then only the menus and a small 3D
/// backdrop (<see cref="TitleDiorama"/>) exist. Picking a mode adds a <see cref="ClientWorld"/>
/// next to this node as <c>/root/Main/World</c> — the path every RPC routes by — and leaving
/// frees it again. A run that names a session on its command line (<c>--connect</c>,
/// <c>--gpx</c>, any probe) boots straight into the world with the shell in
/// <see cref="Direct"/> mode: no title, no loading screen, the pause menu as usual.
/// </para>
///
/// <para>
/// Owns what must outlive worlds: window and render-scale settings
/// (<see cref="DisplaySettings"/>), the input map, the controls overlay, the server book.
/// </para>
/// </summary>
public partial class GameShell : Node
{
    /// <summary>The world was booted from the command line; there was no title screen.</summary>
    public bool Direct { get; init; }

    public static GameShell? Instance { get; private set; }

    public ServerBook Book { get; private set; } = null!;

    private enum State { Title, Starting, Loading, InWorld, Leaving }

    private State _state;
    private CanvasLayer _menuLayer = null!;
    private Control _menuRoot = null!;
    private LoadingScreen _loading = null!;
    private ControlsHelp _help = null!;
    private TitleDiorama? _diorama;
    private readonly List<Screen> _stack = new();
    private ClientWorld? _world;
    private WorldLaunch? _launch;
    private HostedServer? _hosted;
    private ServerQuery? _hostProbe;
    private double _hostWait, _sinceHostProbe;
    private string? _error;
    private string _returnTo = "title";

    /// <summary>The process id of the server hosted from the menu, or -1.</summary>
    public int HostedPid => _hosted?.Pid ?? -1;

    /// <summary>Hosting a server from this client, and it is still running.</summary>
    public bool Hosting => _hosted?.Running == true;

    /// <summary>The name joined with: <c>--name</c> for this run, else the saved one.</summary>
    public string PlayerName
    {
        get
        {
            string cli = PlayerRegistry.ParseRequestedName();
            return cli.Length > 0 ? cli : GameSettings.Current.PlayerName;
        }
    }

    /// <summary>A menu, the loading screen or a modal owns the screen: the world must not act on input.</summary>
    public bool MenuOpen => _stack.Count > 0 || _state is State.Starting or State.Loading or State.Leaving || Modal.Current != null;

    /// <summary>
    /// True for a run that should open on the title screen: no flag that names a session, a
    /// probe or a tool. The list is of the harmless ones, so a new probe flag never lands on the
    /// title by accident — anything unknown boots straight into the world, as before.
    /// </summary>
    public static bool UseTitle(string[] args)
    {
        string[] harmless =
        {
            "--name", "--chunks", "--cache", "--title", "--nocapture", "--rings", "--horizon", "--fog", "--detail",
            "--generated", "--builds", "--commit", "--profile", "--vsync", "--perf", "--view", "--voice", "--time",
            "--traffic", "--at", "--mirrors", "--tyrewear", "--brakewear", "--gearbox", "--perflog",
            "--origin", "--style", "--tree-lod", "--tree-near", "--systems", "--world",
            "--menu", "--settings", "--licenses", "--controls", "--multiplayer", "--solo", "--uishot", "--menucheck", "--leavecheck",
            "--leave-restart", "--autostart", "--wheellock", "--fakewheel", "--ffblog", "--vr", "--xrsim", "--vrmonitor", "--xrheadshot",
        };
        foreach (string a in args)
            if (a.StartsWith("--") && Array.IndexOf(harmless, a) < 0) return false;
        return true;
    }

    public override void _Ready()
    {
        Instance = this;
        Audio.SfxBus.Ensure();
        PlayerInput.Install(GetParent());
        // VR (#186) before any menu or camera exists, so the title is in the headset too
        bool vr = XR.XrSession.TryStart(GetParent());
        AddChild(new DisplaySettings { Name = "Display" });

        // F1 over everything, menus included (layer 42)
        _help = ControlsHelp.Create();
        AddChild(_help);

        _menuLayer = new CanvasLayer { Name = "Menus", Layer = 40, Visible = false };
        AddChild(_menuLayer);
        _menuRoot = new Control { Name = "Root", Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        _menuRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _menuLayer.AddChild(_menuRoot);

        _loading = new LoadingScreen();
        _loading.CancelRequested += CancelLoading;
        AddChild(_loading);

        Book = ServerBook.Load();

        var args = OS.GetCmdlineUserArgs();
        bool Has(string flag) => Array.IndexOf(args, flag) >= 0;
        // "VR mode" saved on, launched from the desktop: start again with OpenXR (once: the
        // relaunch carries --vr, and a run with --vr never relaunches)
        if (!Direct && !vr && GameSettings.Current.VrMode && !Has("--vr") && !Has("--xrsim")
            && DisplayServer.GetName() != "headless" && XR.XrSession.Relaunch(true))
        {
            Quit();
            return;
        }
        if (!Direct)
        {
            ShowTitle();
            // started for VR, but no headset answered: back on the screen, VR mode saved off. Said
            // only when the player just chose VR (#244); a launch that followed the saved setting
            // with the headset unplugged stays quiet
            if (Has("--vr") && !vr)
            {
                GameSettings.Current.VrMode = false;
                GameSettings.Current.Commit();
                if (Has(XR.XrSession.AskedFlag))
                    Callable.From(() => Modal.Inform(_menuRoot, "No VR headset",
                        "OpenXR did not start. Connect the headset with Quest Link (Meta set as the OpenXR runtime), "
                        + "then turn VR mode on again in Settings. Playing on the screen for now.", null)).CallDeferred();
            }
            if (Has("--settings")) Push(SettingsScreen.Create());
            // "--licenses": Settings on its About tab, the licenses and data sources (#118), for screenshotting it
            else if (Has("--licenses"))
            {
                var settings = SettingsScreen.Create();
                Push(settings);
                Callable.From(settings.ShowLicenses).CallDeferred();
            }
            else if (Has("--multiplayer")) Push(MultiplayerScreen.Create());
            else if (Has("--solo")) Push(SoloScreen.Create());
            // "--autostart": straight into Explore through the loading screen, for screenshotting
            // it (and, with --menu, the pause menu once in)
            if (Has("--autostart")) Callable.From(() => Launch(new WorldLaunch { Mode = GameMode.Explore })).CallDeferred();
        }
        if (Has("--controls")) GetTree().CreateTimer(1.5).Timeout += () => _help.Open();
        if (UiShot() is { } shot) GetTree().CreateTimer(shot.Seconds).Timeout += () => SaveShot(shot.Path);
        if (MenuCheck.Requested()) AddChild(new MenuCheck(this));
        if (LeaveCheck.Requested()) AddChild(new LeaveCheck(this));
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        _hosted?.Stop();
        _hostProbe?.Dispose();
    }

    public override void _Notification(int what)
    {
        // closing the window while hosting must not leave the server behind
        if (what == NotificationWMCloseRequest) _hosted?.Stop();
    }

    /// <summary>
    /// A world the command line built (<see cref="Direct"/>), or one this shell is about to add:
    /// wires its menu requests and failures to the menus here.
    /// </summary>
    public void Attach(ClientWorld world)
    {
        _world = world;
        _launch = world.Launch;
        world.MenuOpen = () => MenuOpen;
        world.PauseRequested += OpenPause;
        world.Disconnected += OnDisconnected;
        if (Direct)
        {
            _state = State.InWorld;
            var args = OS.GetCmdlineUserArgs();
            // "--menu" / "--settings" open the pause menu over the world, for screenshotting it
            if (Array.IndexOf(args, "--menu") >= 0 || Array.IndexOf(args, "--settings") >= 0)
                Callable.From(() =>
                {
                    OpenPause();
                    if (Array.IndexOf(args, "--settings") >= 0) Push(SettingsScreen.Create());
                }).CallDeferred();
        }
    }

    // ---- screens --------------------------------------------------------------------------------

    public Screen? Top => _stack.Count > 0 ? _stack[^1] : null;
    public bool InWorld => _state == State.InWorld;
    public ClientWorld? World => _world;

    public void Push(Screen screen)
    {
        if (Top is { } top)
        {
            top.OnHidden();
            top.Visible = false;
        }
        screen.Shell = this;
        _menuRoot.AddChild(screen);
        _stack.Add(screen);
        Reveal(screen);
        screen.OnShown();
        UpdateMenuState();
    }

    /// <summary>Esc / B / the back arrow: pops the top page, if it lets itself be popped.</summary>
    public void Back()
    {
        if (Modal.Current != null || Top is not { } top || !top.OnBack()) return;
        _stack.RemoveAt(_stack.Count - 1);
        top.OnHidden();
        top.QueueFree();
        if (Top is { } under)
        {
            under.Visible = true;
            Reveal(under);
            under.OnShown();
        }
        UpdateMenuState();
    }

    private void ClearStack()
    {
        Modal.Current?.CloseModal();
        foreach (var s in _stack) { s.OnHidden(); s.QueueFree(); }
        _stack.Clear();
        UpdateMenuState();
    }

    /// <summary>Fade in and settle up from a few pixels lower.</summary>
    private void Reveal(Control c)
    {
        c.Modulate = new Color(1, 1, 1, 0);
        c.Position = new Vector2(0, 12);
        var tw = c.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(c, "modulate:a", 1f, 0.18f);
        tw.TweenProperty(c, "position:y", 0f, 0.22f);
    }

    /// <summary>The menus own the pointer and the movement keys while any page is up.</summary>
    private void UpdateMenuState()
    {
        bool open = _stack.Count > 0;
        _menuLayer.Visible = open;
        UiFocus.Set(this, open);
        if (open) Input.MouseMode = Input.MouseModeEnum.Visible;
        else if (_state == State.InWorld) _world?.ResumeControl();
    }

    public void ShowControls() => _help.Open();

    public void Quit()
    {
        _hosted?.Stop();
        GetTree().Quit();
    }

    /// <summary>The last failure (unreachable, kicked, server closed), handed to the Multiplayer screen once.</summary>
    public string? TakeError()
    {
        string? e = _error;
        _error = null;
        return e;
    }

    private void ShowTitle()
    {
        _state = State.Title;
        if (_diorama == null)
        {
            _diorama = TitleDiorama.Create();
            AddChild(_diorama);
        }
        ClearStack();
        Push(TitleScreen.Create());
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_stack.Count == 0 || !e.IsPressed() || e.IsEcho()) return;
        if (!e.IsActionPressed(PlayerInput.Menu) && !e.IsActionPressed("ui_cancel")) return;
        // consumed even when the page refuses to close (the title): see menu-refuses-close-still-consume
        GetViewport().SetInputAsHandled();
        Back();
    }

    // ---- sessions -------------------------------------------------------------------------------

    /// <summary>Builds a world for <paramref name="launch"/> behind the loading screen.</summary>
    public void Launch(WorldLaunch launch)
    {
        if (_state is State.Loading or State.InWorld or State.Leaving) return;
        _launch = launch;
        if (!launch.Hosted)
            _returnTo = launch.Mode == GameMode.Multiplayer ? "multiplayer" : "solo";

        ClearStack();
        if (_state != State.Starting) _loading.Begin(Where(launch));
        _state = State.Loading;
        _diorama?.QueueFree();
        _diorama = null;

        var world = new ClientWorld { Name = "World", Launch = launch };
        Attach(world);
        // a frame later, so the loading screen is drawn before the first heavy step
        Callable.From(() => GetParent().AddChild(world)).CallDeferred();
    }

    public void Join(string endpoint, string? serverName = null)
    {
        GameSettings.Current.LastHost = endpoint;
        GameSettings.Current.Save();
        Launch(new WorldLaunch
        {
            Mode = GameMode.Multiplayer,
            Endpoint = endpoint,
            PlayerName = PlayerName,
            ServerName = serverName,
        });
    }

    /// <summary>"Host a game": starts the server process, then joins it once it answers.</summary>
    public void Host(string name, int port, bool lanVisible)
    {
        if (_state != State.Title) return;
        _hosted = new HostedServer(name, port);
        if (_hosted.Start(lanVisible) is { } err)
        {
            _hosted = null;
            Modal.Inform(_menuRoot, "Could not host", err);
            return;
        }
        _returnTo = "multiplayer";
        _state = State.Starting;
        ClearStack();
        _loading.Begin($"Hosting {name}");
        _loading.Report("Starting your server", 0.02f, "");
        _hostProbe = new ServerQuery();
        _hostProbe.Start();
        _hostWait = 0;
        _sinceHostProbe = 1;
    }

    private void PollHost(double delta)
    {
        if (_hosted == null || _hostProbe == null) return;
        _hostWait += delta;
        string endpoint = $"127.0.0.1:{_hosted.Port}";
        if (!_hosted.Running)
        {
            ReturnWithError(_hosted.Why());
            return;
        }
        if ((_sinceHostProbe += delta) >= 0.25)
        {
            _sinceHostProbe = 0;
            _hostProbe.Probe(endpoint);
        }
        _hostProbe.Poll();
        if (_hostProbe.Probed(endpoint) != null)
        {
            _hostProbe.Dispose();
            _hostProbe = null;
            Launch(new WorldLaunch
            {
                Mode = GameMode.Multiplayer,
                Endpoint = endpoint,
                PlayerName = PlayerName,
                Hosted = true,
                ServerName = _hosted.Name,
            });
            return;
        }
        if (_hostWait > 90)
        {
            ReturnWithError("The server did not start within 90 seconds. See logs/hosted-server.log.");
            return;
        }
        _loading.Report("Starting your server", (float)Math.Min(0.12, _hostWait / 240), $"Reading the map · {(int)_hostWait} s");
    }

    public override void _Process(double delta)
    {
        switch (_state)
        {
            case State.Starting:
                PollHost(delta);
                break;
            case State.Loading when _world != null && IsInstanceValid(_world):
                _loading.Report(StageText(_world.Stage), Overall(_world), _world.LoadDetail);
                if (_world.Stage == LoadStage.Ready) FinishLoading();
                else if (_world.Stage == LoadStage.Failed) LeaveWorld(_world.Failure ?? "The world could not be started.");
                break;
        }
    }

    /// <summary>When hosting, the server's own start was the first stretch of the bar.</summary>
    private float Overall(ClientWorld w) => _launch?.Hosted == true ? 0.12f + 0.88f * w.LoadFraction : w.LoadFraction;

    private void FinishLoading()
    {
        _state = State.InWorld;
        _loading.Finish();
        UpdateMenuState();
        if (_launch is { Mode: GameMode.Multiplayer, Hosted: false } l)
            Book.NotePlayed(l.Endpoint, l.ServerName);
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--menu") >= 0) Callable.From(OpenPause).CallDeferred();
        GD.Print($"[shell] in world: {_launch?.Mode}");
    }

    private void CancelLoading()
    {
        if (_state == State.Starting) ReturnWithError(null);
        else if (_state == State.Loading) LeaveWorld(null);
    }

    private void OpenPause()
    {
        if (_state != State.InWorld || _stack.Count > 0) return;
        Push(PauseScreen.Create());
    }

    private void OnDisconnected(string reason)
    {
        // a command-line session keeps the old behaviour (a chat line, stay in the world): the
        // probes that drop a server under a client rely on it
        if (_launch?.FromCommandLine == true) return;
        if (_state is State.InWorld or State.Loading) LeaveWorld(reason);
    }

    private void ReturnWithError(string? error)
    {
        _hosted?.Stop();
        _hosted = null;
        _hostProbe?.Dispose();
        _hostProbe = null;
        _loading.Close();
        _error = error;
        ShowTitle();
        Push(MultiplayerScreen.Create());
    }

    /// <summary>
    /// Leaves the world for the title screen: disconnects, stops a hosted server, frees the
    /// world and resets the static state it left behind (<see cref="WorldStatics"/>). With an
    /// error, the screen the session was started from comes back with it shown.
    /// </summary>
    public void LeaveWorld(string? error)
    {
        if (_state is State.Leaving or State.Title) return;
        _state = State.Leaving;
        ClearStack();
        _loading.Begin("Back to the menu");
        _loading.Report("Leaving", 1, "");
        _error = error;
        Callable.From(Teardown).CallDeferred();
    }

    private async void Teardown()
    {
        string from = _returnTo;
        try
        {
            var peer = Multiplayer.MultiplayerPeer;
            if (peer != null && peer is not OfflineMultiplayerPeer) peer.Close();
            Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
            _hosted?.Stop();
            _hosted = null;

            if (_world != null && IsInstanceValid(_world))
            {
                // removed first so its _ExitTree runs now (the tile workers are waited for) and the
                // name "World" is free for the next session; a sibling renamed "World2" would break RPC paths
                _world.GetParent()?.RemoveChild(_world);
                _world.QueueFree();
            }
            _world = null;
            WorldStatics.Reset();
        }
        catch (Exception e)
        {
            GD.PushError($"[shell] leaving the world failed ({e.Message}); restarting instead");
            HardRestart();
            return;
        }
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--leave-restart") >= 0) { HardRestart(); return; }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _loading.Close();
        ShowTitle();
        // a deliberate leave lands on the title; a failure goes back to where the session was
        // started from, with the reason shown there
        if (_error != null)
        {
            if (from == "solo") Modal.Inform(_menuRoot, "Could not start", TakeError()!);
            else Push(MultiplayerScreen.Create());
        }
        GD.Print("[shell] back at the title");
    }

    /// <summary>The way out if a world cannot be torn down in place: start the game again.</summary>
    private void HardRestart()
    {
        var args = OS.GetCmdlineUserArgs().Where(a => a != "--leave-restart").ToArray();
        OS.SetRestartOnExit(true, OS.GetCmdlineArgs().TakeWhile(a => a != "--").Concat(args.Length > 0 ? new[] { "--" }.Concat(args) : Array.Empty<string>()).ToArray());
        GetTree().Quit();
    }

    /// <summary>What the pause menu says the session is.</summary>
    public string SessionLine()
    {
        if (_launch == null) return "";
        return _launch.Mode switch
        {
            GameMode.Explore => "Exploring offline",
            GameMode.GpxReplay => "GPX replay",
            _ => (Hosting ? "Hosting " : "On ") + (_launch.ServerName ?? _launch.Endpoint)
                 + (_world?.Players is int n && n > 0 ? $"  ·  {n} online" : ""),
        };
    }

    private static string Where(WorldLaunch l) => l.Mode switch
    {
        GameMode.Explore => "Explore",
        GameMode.GpxReplay => l.GpxPaths.Count > 1 ? $"GPX race · {l.GpxPaths.Count} tracks" : "GPX replay",
        _ => l.Hosted ? $"Hosting {l.ServerName}" : $"Joining {l.ServerName ?? l.Endpoint}",
    };

    private static string StageText(LoadStage s) => s switch
    {
        LoadStage.ReadingMap => "Reading the map",
        LoadStage.BuildingWorld => "Building the world",
        LoadStage.Connecting => "Connecting",
        LoadStage.SyncingTerrain => "Syncing the terrain",
        LoadStage.WaitingForPlayer => "Waiting for your player",
        LoadStage.PlacingYou => "Finding solid ground",
        LoadStage.BuildingTerrain => "Building the terrain around you",
        LoadStage.DrawingHorizon => "Raising the mountains",
        LoadStage.Ready => "Ready",
        _ => "Something went wrong",
    };

    // ---- screenshots of the menus -------------------------------------------------------------

    /// <summary>"--uishot &lt;png&gt; [seconds]": saves the screen after a few seconds and quits.</summary>
    private static (string Path, double Seconds)? UiShot()
    {
        var a = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(a, "--uishot");
        if (i < 0 || i + 1 >= a.Length) return null;
        double seconds = 3;
        if (i + 2 < a.Length) double.TryParse(a[i + 2], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out seconds);
        return (a[i + 1], seconds <= 0 ? 3 : seconds);
    }

    private void SaveShot(string path)
    {
        var img = GetViewport().GetTexture().GetImage();
        var err = img.SavePng(path);
        GD.Print($"[uishot] {path}: {err}");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }
}
