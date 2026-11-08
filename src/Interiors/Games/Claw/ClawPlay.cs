using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Claw;

/// <summary>
/// Playing the claw machine (#1326): in front of it, looking in through the glass. Jump (Space, pad A) puts a Batzen in;
/// then the stick (W / A / S / D, the left stick) steers the claw as you see it, and Jump drops it (or the time runs
/// out). V / R3 looks from the side, to judge how deep it is. VR: the same through the pad (<c>XrPad</c>); gripping the
/// joystick is the target.
/// </summary>
public sealed class ClawPlay : TableGamePlay.Mode
{
    private static readonly StringName Left = PlayerInput.MoveLeft, Right = PlayerInput.MoveRight, Fwd = PlayerInput.MoveForward,
        Back = PlayerInput.MoveBack, Button = PlayerInput.Jump;

    private double _sendAt;

    public override Vector3 Stand(int seat) => new(0, 0, Rig.Piece.D / 2 + 0.45f);

    public override float Fov => 50f;

    public override Transform3D Camera(TableGame? game, int seat, bool alt)
    {
        var centre = new Vector3(0, ClawRig.FloorY + 0.25f, 0);
        if (alt) return new Transform3D(Basis.Identity, new Vector3(Rig.Piece.W / 2 + 0.9f, 1.55f, 0.1f)).LookingAt(centre, Vector3.Up);
        return new Transform3D(Basis.Identity, new Vector3(0, 1.62f, Rig.Piece.D / 2 + 0.55f)).LookingAt(centre, Vector3.Up);
    }

    public override void Process(TableGame? game, int seat, double delta)
    {
        if (game is not ClawGame g || seat < 0) return;
        if (g.State == ClawGame.Phase.Idle)
        {
            if (Input.IsActionJustPressed(Button) && Games.PayCoin()) Games.AskAct(Key, Index, ClawGame.ActCoin);
            return;
        }
        if (g.State != ClawGame.Phase.Steer) return;
        if (Input.IsActionJustPressed(Button)) { Games.AskAct(Key, Index, ClawGame.ActDrop); return; }
        _sendAt -= delta;
        if (_sendAt > 0) return;
        _sendAt = TableGames.InputEvery;
        // from the front: right is +x, forward (into the box) is −z
        var stick = new Vector2(Input.GetActionStrength(Right) - Input.GetActionStrength(Left), -(Input.GetActionStrength(Fwd) - Input.GetActionStrength(Back)));
        Games.SendInput(Key, Index, ClawGame.Pack(stick));
    }

    public override IEnumerable<(string Action, string Text)> Prompts(TableGame? game, int seat)
    {
        if (game is not ClawGame g || seat < 0) yield break;
        if (g.State == ClawGame.Phase.Idle) { yield return (Button, "Insert a Batzen"); yield break; }
        if (g.State != ClawGame.Phase.Steer) yield break;
        yield return (Fwd, "Steer the claw");
        yield return (Button, "Drop");
    }

    // ---- the catalog's row ---------------------------------------------------------------------

    public static GameSpec Spec => new(
        FurnitureType.ClawMachine, GameKind.Claw, "Claw machine",
        f => new ClawGame(f.W, f.D, System.Random.Shared.Next()),
        (local, f) => GameCatalog.Front(local, f, 1.2f),
        f => (new Vector3(0, f.H / 2, 0), new Vector3(f.W, f.H, f.D)),
        ClawRig.Create,
        () => new ClawPlay());
}
