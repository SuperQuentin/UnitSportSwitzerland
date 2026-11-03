using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.AirHockey;

/// <summary>
/// Playing air hockey (#1326): at your end of the table, looking down it. The stick (W / A / S / D, the left stick)
/// drives your mallet as you see it, Sprint held fast; it stays in your half. Your mallet's speed goes to the server 30
/// times a second. V / R3 from above. VR: the same through the pad (<c>XrPad</c>); holding the mallet is the target.
/// </summary>
public sealed class AirHockeyPlay : TableGamePlay.Mode
{
    private static readonly StringName Left = PlayerInput.MoveLeft, Right = PlayerInput.MoveRight, Fwd = PlayerInput.MoveForward,
        Back = PlayerInput.MoveBack, Fast = PlayerInput.Sprint, Again = PlayerInput.Jump;

    private double _sendAt;

    private AirHockeyRig Table => (AirHockeyRig)Rig;

    public override Vector3 Stand(int seat) => new(0, 0, (seat == 0 ? -1 : 1) * (Rig.Piece.D / 2 + 0.35f));

    public override float Fov => 60f;

    public override Transform3D Camera(TableGame? game, int seat, bool alt)
    {
        var centre = new Vector3(0, AirHockeyRig.FieldY, 0);
        if (alt) return new Transform3D(Basis.Identity, centre + Vector3.Up * 2.2f).LookingAt(centre, Vector3.Back);
        float side = seat == 1 ? 1 : -1;
        return new Transform3D(Basis.Identity, new Vector3(0, 1.55f, side * (Rig.Piece.D / 2 + 0.3f))).LookingAt(centre + new Vector3(0, 0, -side * 0.15f), Vector3.Up);
    }

    public override void Process(TableGame? game, int seat, double delta)
    {
        if (game is not AirHockeyGame g || seat < 0) return;
        if (g.Winner >= 0)
        {
            if (Input.IsActionJustPressed(Again)) Games.AskAct(Key, Index, AirHockeyGame.ActRestart);
            return;
        }
        _sendAt -= delta;
        if (_sendAt > 0) return;
        _sendAt = TableGames.InputEvery;
        var stick = new Vector2(Input.GetActionStrength(Right) - Input.GetActionStrength(Left), Input.GetActionStrength(Fwd) - Input.GetActionStrength(Back));
        if (!Input.IsActionPressed(Fast)) stick *= 0.6f;
        // as the player sees it: from −z looking +z, right is −x; from +z, right is +x and forward −z
        var v = seat == 0 ? new Vector2(-stick.X, stick.Y) : new Vector2(stick.X, -stick.Y);
        Games.SendInput(Key, Index, AirHockeyGame.Pack(v));
    }

    public override IEnumerable<(string Action, string Text)> Prompts(TableGame? game, int seat)
    {
        if (game is not AirHockeyGame g || seat < 0) yield break;
        if (g.Winner >= 0) { yield return (Again, "New game"); yield break; }
        yield return (Fwd, "Move your mallet");
        yield return (Fast, "Hold for full speed");
    }

    // ---- the catalog's row ---------------------------------------------------------------------

    public static GameSpec Spec => new(
        FurnitureType.AirHockey, GameKind.AirHockey, "Air hockey",
        f => { var (hx, hz) = AirHockeyGame.Field(f.W, f.D); return new AirHockeyGame(hx, hz); },
        (local, f) => GameCatalog.Round(local, f, 0.9f),
        f => (new Vector3(0, 0.45f, 0), new Vector3(f.W, 0.9f, f.D)),
        AirHockeyRig.Create,
        () => new AirHockeyPlay());
}
