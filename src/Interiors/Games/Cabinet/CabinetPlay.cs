using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Cabinet;

/// <summary>
/// Playing an arcade machine (#1326): the screen fills the view. Jump (Space, pad A) puts a Batzen in; the server says go,
/// and this client runs the machine's small game (<see cref="MiniGame"/>) from the stick and Jump (a rhythm game's four
/// lanes are the four directions: A / S / W / D, the arrows, the stick or the pad's d-pad), drawn into the cabinet's screen
/// every frame; at its end the score goes to the server. VR: the same through the pad (<c>XrPad</c>).
/// </summary>
public sealed class CabinetPlay : TableGamePlay.Mode
{
    private static readonly StringName Left = PlayerInput.MoveLeft, Right = PlayerInput.MoveRight, Fwd = PlayerInput.MoveForward,
        Back = PlayerInput.MoveBack, Button = PlayerInput.Jump;

    private MiniGame? _game;
    private int _seed;

    private CabinetRig Cab => (CabinetRig)Rig;

    public override float Fov => 46f;

    public override Vector3 Stand(int seat)
    {
        var (centre, _) = CabinetRig.Screen(Rig.Piece);
        return new Vector3(centre.X, 0, centre.Z + (Rig.Piece.Type == FurnitureType.DanceMachine ? 1.1f : 0.55f));
    }

    public override Transform3D Camera(TableGame? game, int seat, bool alt)
    {
        var (centre, size) = CabinetRig.Screen(Rig.Piece);
        float back = alt ? 1.6f : size.X * 1.15f + 0.05f;
        return new Transform3D(Basis.Identity, centre + new Vector3(0, alt ? 0.25f : 0.02f, back)).LookingAt(centre, Vector3.Up);
    }

    public override bool EatsEnter => _game != null;

    public override void Process(TableGame? game, int seat, double delta)
    {
        if (game is not CabinetGame g || seat < 0) return;
        if (_game == null)
        {
            // the server said go (our coin): the machine's game starts here
            if (g.Playing && g.Seats[0] == Games.MyId && g.Seed != _seed)
            {
                _seed = g.Seed;
                _game = MiniGame.Make(g.Plays, g.Seed);
                Cab.Live = true;
            }
            else if (!g.Playing && Input.IsActionJustPressed(Button) && Games.PayCoin()) Games.AskAct(Key, Index, CabinetGame.ActCoin);
            return;
        }
        var input = new MiniInput(
            Input.GetActionStrength(Right) - Input.GetActionStrength(Left), Input.GetActionStrength(Fwd) - Input.GetActionStrength(Back),
            Input.IsActionPressed(Button), Input.IsActionPressed(Left), Input.IsActionPressed(Back), Input.IsActionPressed(Fwd), Input.IsActionPressed(Right));
        _game.Step((float)Math.Min(delta, 0.1), input);
        Cab.DrawGame(_game);
        if (_game.Over) Finish();
    }

    /// <summary>The game is over (or abandoned): its score to the server, the attract screen back.</summary>
    private void Finish()
    {
        if (_game == null) return;
        Games.AskAct(Key, Index, CabinetGame.ActEnd, _game.Score);
        _game = null;
        Cab.Live = false;
    }

    public override IEnumerable<(string Action, string Text)> Prompts(TableGame? game, int seat)
    {
        if (game is not CabinetGame g || seat < 0) yield break;
        if (_game == null) { if (!g.Playing) yield return (Button, "Insert a Batzen"); yield break; }
        switch (g.Plays)
        {
            case MiniGame.Kind.Shooter:
                yield return (Left, "Move");
                yield return (Button, "Fire");
                break;
            case MiniGame.Kind.Racer:
                yield return (Left, "Steer");
                yield return (Fwd, "Faster");
                break;
            default:
                yield return (Left, "The four lanes: left, down, up, right");
                break;
        }
    }

    public override void Exit()
    {
        // stepping back mid-game ends it with what it scored
        Finish();
    }

    // ---- the catalog's rows: every machine with a screen of its own ---------------------------------

    public static IEnumerable<GameSpec> Specs()
    {
        foreach (var type in new[] { FurnitureType.ArcadeCabinet, FurnitureType.Racer, FurnitureType.DanceMachine, FurnitureType.DrumGame })
            yield return new GameSpec(
                type, GameKind.Cabinet, "Arcade machine",
                f => new CabinetGame(CabinetRig.PlaysOf(f)),
                (local, f) => GameCatalog.Front(local, f, type == FurnitureType.ArcadeCabinet ? 1.0f : 1.6f) || type != FurnitureType.ArcadeCabinet && GameCatalog.Round(local, f, 0.3f),
                f => (new Vector3(0, f.H / 2, 0), new Vector3(f.W, f.H, f.D)),
                CabinetRig.Create,
                () => new CabinetPlay());
    }
}
