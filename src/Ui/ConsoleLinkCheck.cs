using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// <c>godot --headless --path . -- --consolelinkcheck &lt;host:port&gt; --fakeprotocol 999 --world fixture</c>
/// (driven by <c>tools/consolelinknetcheck.sh</c>, #1088): joining for the chat only, from the title, against a
/// server started with <c>--admin-password test</c>.
/// <list type="bullet">
/// <item>Joining with another protocol is refused, and the Multiplayer screen's banner offers that server's console.</item>
/// <item>Opening it joins under this player's name with that name's rights: not an admin, so <c>/update</c> is
/// refused; <c>/login</c> makes it one, and <c>/update</c> then runs (this server cannot update itself).</item>
/// <item><c>/who</c> lists it as on the console; it outlives the handshake's hello timeout; a message is said to
/// everyone under its name; <c>/tp</c> needs a body.</item>
/// </list>
/// Wall clock throughout: a server answers in real time, whatever <c>--fixed-fps</c> does.
/// </summary>
public partial class ConsoleLinkCheck : Node
{
    private readonly GameShell _shell;
    private readonly string _endpoint;
    private int _failures;

    public static bool Requested() => CmdArgs.Has("--consolelinkcheck");

    public ConsoleLinkCheck(GameShell shell)
    {
        _shell = shell;
        _endpoint = CmdArgs.Value("--consolelinkcheck", notFlag: true) ?? "127.0.0.1:7887";
        Name = "ConsoleLinkCheck";
    }

    public override async void _Ready()
    {
        await Wait(() => _shell.Top is TitleScreen, 30);
        _shell.Join(_endpoint, "Console check");

        // refused for its version: back on the Multiplayer screen, whose banner offers the console
        Button? button = null;
        bool offered = await Wait(() => _shell.Top is MultiplayerScreen m && (button = Find(m, "Server console…")) != null, 90);
        Expect(offered, "the refusal banner offers the server's console");
        if (!offered) { Done(); return; }
        await Shot("banner");
        button!.EmitSignal(BaseButton.SignalName.Pressed);
        ServerConsoleScreen? console = null;
        Expect(await Wait(() => (console = _shell.Top as ServerConsoleScreen) != null, 5), "the console screen opens");
        if (console == null) { Done(); return; }

        Expect(await Wait(() => console.IsOpen && console.Output.Contains("as ConsoleCheck."), 15), "joined as ConsoleCheck, not an admin");
        if (!console.IsOpen) { Done(); return; }

        Expect(await Answer(console, "/update", "'/update' is an admin command."), "/update refused without the rights");
        Expect(await Answer(console, "/login wrong", "Wrong password."), "a wrong /login refused");
        Expect(await Answer(console, "/login test", "You are now an operator."), "/login makes it an admin");
        Expect(await Answer(console, "/update", "This server cannot update itself"), "/update runs for an admin");
        Expect(await Answer(console, "/who", "ConsoleCheck (admin) (console)"), "/who lists it, on the console");

        // the handshake kicks a peer that never says hello after 10 s: not this one
        await Wait(() => false, 13);
        Expect(console.IsOpen, "still connected after the hello timeout");

        Expect(await Answer(console, "hello-from-console", "ConsoleCheck: hello-from-console"), "a message is said to everyone");
        Expect(await Answer(console, "/tp Nobody", "needs a body"), "/tp needs a body");
        await Shot("console");
        GD.Print("[consolelink] output:\n" + console.Output);
        Done();
    }

    /// <summary>Windowed (<c>WINDOWED=1</c>): the screen as it is, in <c>test_output/consolelink_&lt;name&gt;.png</c>.</summary>
    private async Task Shot(string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng($"res://test_output/consolelink_{name}.png");
        GD.Print($"[consolelink] shot test_output/consolelink_{name}.png");
    }

    /// <summary>Sends <paramref name="line"/> and waits for a reply line holding <paramref name="expected"/>.</summary>
    private async Task<bool> Answer(ServerConsoleScreen console, string line, string expected)
    {
        int from = console.Output.Length;
        console.Submit(line);
        return await Wait(() => console.Output.Length > from && console.Output[from..].Split('\n')
            .Any(l => !l.StartsWith('>') && l.Contains(expected)), 10);
    }

    private static Button? Find(Node node, string text)
    {
        if (node is Button { Text: var t } b && t == text) return b;
        foreach (var child in node.GetChildren())
            if (Find(child, text) is { } found) return found;
        return null;
    }

    private async Task<bool> Wait(Func<bool> condition, double seconds)
    {
        ulong start = Time.GetTicksMsec();
        while ((Time.GetTicksMsec() - start) / 1000.0 < seconds)
        {
            if (condition()) return true;
            await ToSignal(GetTree().CreateTimer(0.2, ignoreTimeScale: true), SceneTreeTimer.SignalName.Timeout);
        }
        return condition();
    }

    private void Expect(bool ok, string what)
    {
        GD.Print($"[consolelink] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Done()
    {
        GD.Print(_failures == 0 ? "[consolelink] RESULT: ok" : $"[consolelink] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
