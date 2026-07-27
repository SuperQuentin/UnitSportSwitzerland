using Godot;

namespace UnitSport.Net;

/// <summary>
/// Reads commands from the dedicated server's own stdin.
///
/// Without this a headless server has no voice at all: nobody can be granted admin on a fresh
/// server, nothing can be announced, and nobody can be kicked unless an operator already
/// exists. The console is the bootstrap — it is the process that owns the game, so it runs
/// commands with operator rights by definition.
///
/// <para>
/// Reading is done on a background thread because <see cref="Console.ReadLine"/> blocks, and
/// blocking Godot's main loop would freeze the whole server. Lines are handed back with
/// <c>CallDeferred</c>, so the command itself still runs on the main thread where the scene
/// tree can be touched safely.
/// </para>
/// </summary>
public partial class ServerConsole : Node
{
    private readonly ChatManager _chat;
    private Thread? _reader;
    private volatile bool _running;

    public ServerConsole(ChatManager chat)
    {
        _chat = chat;
        Name = "ServerConsole";
    }

    public override void _Ready()
    {
        // A server with no console attached (a service, a redirected pipe that closes) must
        // not spin: the reader exits as soon as stdin reports end of stream.
        _running = true;
        _reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "ServerConsole",
        };
        _reader.Start();

        GD.Print("[console] ready — type /help, or a command without the slash");
    }

    private void ReadLoop()
    {
        while (_running)
        {
            string? line;
            try
            {
                line = Console.ReadLine();
            }
            catch (Exception e)
            {
                GD.PushWarning($"[console] stdin closed: {e.Message}");
                return;
            }

            if (line is null)
            {
                GD.Print("[console] stdin ended; console commands are no longer available");
                return;
            }

            if (line.Trim().Length == 0) continue;

            // Hop back to the main thread: the command touches the scene tree and the
            // multiplayer peer, neither of which is safe from here.
            string command = line;
            Callable.From(() => _chat.RunConsoleCommand(command)).CallDeferred();
        }
    }

    public override void _ExitTree() => _running = false;
}
