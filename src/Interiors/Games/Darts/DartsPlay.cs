using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Darts;

/// <summary>
/// Playing darts (#1324): at the oche, the board in view. The stick (A / D / W / S, left stick) moves the aim, which
/// sways as a hand does; Sprint held moves it finely. Holding Jump (Space, pad A) steadies the hand for a moment, then
/// it tires and sways more; let go to throw. V / R3 a close look at the board. VR: the same through the pad
/// (<c>XrPad</c>); throwing the dart by hand is the row's target.
/// </summary>
public sealed class DartsPlay : TableGamePlay.Mode
{
    private static readonly StringName Left = PlayerInput.MoveLeft, Right = PlayerInput.MoveRight, Up = PlayerInput.MoveForward,
        Down = PlayerInput.MoveBack, Fine = PlayerInput.Sprint, Throw = PlayerInput.Jump;

    /// <summary>The aim moves this fast [mm/s], finely with Sprint; the hand's sway at rest and steadied [mm].</summary>
    private const float Speed = 160f, FineSpeed = 35f, Sway = 14f, Steady = 3.5f;

    private Vector2 _aim = DartBoard.Aim(20, 3);
    private float _clock, _held;
    private bool _holding;

    private DartsRig Board => (DartsRig)Rig;

    public override float Fov => 38f;

    public override Vector3 Stand(int seat) => new(-0.5f + seat * 0.35f, 0, Board.Bull.Z + DartBoard.Oche + 0.15f);

    public override Transform3D Camera(TableGame? game, int seat, bool alt)
    {
        var bull = Board.Bull;
        if (alt) return new Transform3D(Basis.Identity, bull + new Vector3(0, 0, 0.9f)).LookingAt(bull, Vector3.Up);
        return new Transform3D(Basis.Identity, new Vector3(0, 1.62f, bull.Z + DartBoard.Oche)).LookingAt(bull + Vector3.Down * 0.03f, Vector3.Up);
    }

    /// <summary>Where the hand points now: the aim and its sway, smaller while steadied (for about 1.5 s), larger after.</summary>
    private Vector2 Swayed()
    {
        float steady = _holding ? Mathf.Clamp(_held / 0.5f, 0, 1) * (1 - Mathf.Clamp((_held - 2f) / 2f, 0, 1)) : 0;
        float amp = Mathf.Lerp(Sway, Steady, steady) * (_held > 2f ? 1 + (_held - 2f) * 0.6f : 1);
        return _aim + new Vector2(Mathf.Sin(_clock * 1.7f) + 0.5f * Mathf.Sin(_clock * 3.1f + 1), Mathf.Sin(_clock * 2.3f + 0.6f) + 0.4f * Mathf.Sin(_clock * 4.1f)) * amp * 0.7f;
    }

    public override void Process(TableGame? game, int seat, double delta)
    {
        var g = game as DartsGame;
        float dt = (float)delta;
        _clock += dt;
        bool mine = g != null && seat >= 0 && (g.Up == seat || g.Winner >= 0) && !g.Busy(Games.ClockNow);
        if (!mine)
        {
            Board.Aim = null;
            _holding = false;
            return;
        }
        if (g!.Winner >= 0)
        {
            Board.Aim = null;
            if (Input.IsActionJustPressed(Throw)) Games.AskAct(Key, Index, DartsGame.ActThrow, 0, 0);
            return;
        }
        var stick = new Vector2(Input.GetActionStrength(Right) - Input.GetActionStrength(Left), Input.GetActionStrength(Up) - Input.GetActionStrength(Down));
        _aim += stick * (Input.IsActionPressed(Fine) ? FineSpeed : Speed) * dt;
        if (_aim.Length() > DartBoard.Radius) _aim = _aim.Normalized() * DartBoard.Radius;
        if (Input.IsActionJustPressed(Throw)) { _holding = true; _held = 0; }
        if (_holding) _held += dt;
        var at = Swayed();
        if (_holding && !Input.IsActionPressed(Throw))
        {
            _holding = false;
            Games.AskAct(Key, Index, DartsGame.ActThrow, (int)(at.X * 10), (int)(at.Y * 10));
        }
        Board.Aim = at;
    }

    public override IEnumerable<(string Action, string Text)> Prompts(TableGame? game, int seat)
    {
        if (game is not DartsGame g || seat < 0) yield break;
        if (g.Winner >= 0) { yield return (Throw, "Play again"); yield break; }
        if (g.Up != seat) yield break;
        yield return (Up, "Aim");
        yield return (Fine, "Fine aim");
        yield return (Throw, "Hold to steady, let go to throw");
    }

    public override void Exit()
    {
        if (GodotObject.IsInstanceValid(Rig)) Board.Aim = null;
    }

    // ---- the catalog's row ---------------------------------------------------------------------

    public static GameSpec Spec => new(
        FurnitureType.DartBoard, GameKind.Darts, "Dartboard",
        _ => new DartsGame(),
        // from the oche or nearer, in front of the board
        (local, f) => Mathf.Abs(local.X) < 1.3f && local.Z > f.D / 2 - 0.1f && local.Z < f.D / 2 + DartBoard.Oche + 0.8f && Mathf.Abs(local.Y) < 2.5f,
        f => (new Vector3(0, 1.73f, -f.D / 2 + 0.08f), new Vector3(0.52f, 0.52f, 0.1f)),
        DartsRig.Create,
        () => new DartsPlay(),
        DartBoard.Oche + 0.8f);
}
