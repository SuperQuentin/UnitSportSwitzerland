using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Foosball;

/// <summary>
/// Playing table football (#1324): at your team's side of the table, looking across it. W / S (the left stick
/// forward and back) push and pull your rods, sliding the figures across the pitch; Jump (Space, pad A) kicks; Sprint
/// (Shift, L3) held lifts the rods so the ball passes under. Two players run four rods each; four, the back seats the
/// goalie and defence, the front ones the midfield and attack. Your controls go to the server 30 times a second.
/// V / R3 looks down from above. VR: the same through the pad (<c>XrPad</c>); gripping the handles is the target.
/// </summary>
public sealed class FoosballPlay : TableGamePlay.Mode
{
    private static readonly StringName Push = PlayerInput.MoveForward, Pull = PlayerInput.MoveBack, Kick = PlayerInput.Jump, Lift = PlayerInput.Sprint;

    private double _sendAt;
    private bool _kicked;

    private FoosballRig Table => (FoosballRig)Rig;

    /// <summary>A point of the game's frame (x along, z across) in the piece's.</summary>
    private Vector3 P(float x, float y, float z) => Table.TableFrame * new Vector3(x, y, z);

    public override Vector3 Stand(int seat)
    {
        int team = FoosballGame.TeamOf(seat);
        bool front = seat >= 2;
        // a team's back player by its own goal, its front one towards the other
        float x = (team == 0 ? -1 : 1) * (front ? -0.15f : 0.3f);
        return P(x, 0, (team == 0 ? -1 : 1) * (FoosballGame.HalfZ + 0.45f));
    }

    public override Transform3D Camera(TableGame? game, int seat, bool alt)
    {
        var centre = P(0, FoosballRig.PitchY, 0);
        if (alt) return new Transform3D(Basis.Identity, centre + Vector3.Up * 1.25f).LookingAt(centre, Table.TableFrame.Basis * Vector3.Back);
        int team = seat < 0 ? 0 : FoosballGame.TeamOf(seat);
        float side = team == 0 ? -1 : 1;
        return new Transform3D(Basis.Identity, P(0, 1.5f, side * 0.95f)).LookingAt(centre, Vector3.Up);
    }

    public override float Fov => 55f;

    public override void Process(TableGame? game, int seat, double delta)
    {
        if (game is not FoosballGame g || seat < 0) return;
        if (g.Winner >= 0)
        {
            if (Input.IsActionJustPressed(Kick)) Games.AskAct(Key, Index, FoosballGame.ActRestart);
            return;
        }
        if (Input.IsActionJustPressed(Kick)) _kicked = true;
        _sendAt -= delta;
        if (_sendAt > 0) return;
        _sendAt = TableGames.InputEvery;
        float push = Input.GetActionStrength(Push) - Input.GetActionStrength(Pull);
        // pushing moves the figures away from you: +z for red (who stands at −z), −z for blue
        float slide = FoosballGame.TeamOf(seat) == 0 ? push : -push;
        Games.SendInput(Key, Index, FoosballGame.Pack(slide, _kicked || Input.IsActionPressed(Kick), Input.IsActionPressed(Lift)));
        _kicked = false;
    }

    public override IEnumerable<(string Action, string Text)> Prompts(TableGame? game, int seat)
    {
        if (game is not FoosballGame g || seat < 0) yield break;
        if (g.Winner >= 0) { yield return (Kick, "New game"); yield break; }
        yield return (Push, "Push / pull the rods");
        yield return (Kick, "Kick");
        yield return (Lift, "Hold to lift the rods");
    }

    // ---- the catalog's row ---------------------------------------------------------------------

    public static GameSpec Spec => new(
        FurnitureType.TableFootball, GameKind.Foosball, "Table football",
        _ => new FoosballGame(),
        (local, f) => GameCatalog.Round(local, f, 0.9f),
        f => (new Vector3(0, 0.45f, 0), new Vector3(f.W, 0.95f, f.D)),
        FoosballRig.Create,
        () => new FoosballPlay());
}
