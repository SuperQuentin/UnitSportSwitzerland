using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Pool;

/// <summary>
/// Playing pool (#1324): down behind the cue ball looking along the shot; the stick (A / D, left stick) turns the
/// aim, Sprint held turns it finely, the right stick or the arrows nudge it; forward and back set follow or draw;
/// Jump (Space, pad A) held swings the power up and down, let go strikes. Ball in hand: from above, the stick moves
/// the cue ball, Jump puts it down. V / R3 looks at the whole table from above. In VR the same through the pad
/// (<c>XrPad</c>); holding the cue in both hands is the row's target.
/// </summary>
public sealed class PoolPlay : TableGamePlay.Mode
{
    private static readonly StringName Left = PlayerInput.MoveLeft, Right = PlayerInput.MoveRight, Fwd = PlayerInput.MoveForward,
        Back = PlayerInput.MoveBack, Fine = PlayerInput.Sprint, Strike = PlayerInput.Jump,
        NudgeLeft = PlayerInput.BowlAimLeft, NudgeRight = PlayerInput.BowlAimRight;

    /// <summary>The aim turns this fast [rad/s], and finely with Sprint.</summary>
    private const float TurnRate = 0.9f, FineRate = 0.08f;
    /// <summary>A full swing of the power, up and down [s].</summary>
    private const float SwingTime = 1.8f;

    private float _angle, _follow, _swing;
    private bool _holding, _aimSet;
    private int _aimedShot = -1;
    private Vector2 _hand;
    private bool _handSet;

    private PoolRig PoolRig => (PoolRig)Rig;

    public override Vector3 Stand(int seat) => new(0, 0, -(Rig.Piece.D / 2 + 0.45f) * (seat == 1 ? -1 : 1));

    private bool MyShot(PoolGame g, int seat) => seat >= 0 && g.Up == seat && !g.Busy(TableGames.Instance!.ClockNow);

    public override Transform3D Camera(TableGame? game, int seat, bool alt)
    {
        var g = game as PoolGame;
        var f = Rig.Piece;
        if (g != null && MyShot(g, seat) && g.State != PoolGame.Phase.Over && !alt)
        {
            if (g.InHand)
                return new Transform3D(Basis.Identity, new Vector3(0, PoolRig.ClothY + 2.3f, -0.01f)).LookingAt(new Vector3(0, PoolRig.ClothY, 0), Vector3.Back);
            var cue = g.Table.P[0];
            var dir = new Vector3(Mathf.Sin(_angle), 0, Mathf.Cos(_angle));
            var ball = PoolRig.On(cue);
            return new Transform3D(Basis.Identity, ball - dir * 0.7f + Vector3.Up * 0.24f).LookingAt(ball + dir * 0.5f, Vector3.Up);
        }
        if (alt)
            return new Transform3D(Basis.Identity, new Vector3(0, PoolRig.ClothY + 2.3f, -0.01f)).LookingAt(new Vector3(0, PoolRig.ClothY, 0), Vector3.Back);
        // watching: from the head end, high
        return new Transform3D(Basis.Identity, new Vector3(0.4f, 2.1f, -(f.D / 2 + 1.1f))).LookingAt(new Vector3(0, PoolRig.ClothY, 0.2f), Vector3.Up);
    }

    public override void Process(TableGame? game, int seat, double delta)
    {
        var g = game as PoolGame;
        float dt = (float)delta;
        if (g == null || !MyShot(g, seat))
        {
            PoolRig.Aim = null;
            PoolRig.Holding = null;
            _holding = false;
            return;
        }
        if (g.State == PoolGame.Phase.Over)
        {
            PoolRig.Aim = null;
            if (Input.IsActionJustPressed(Strike)) Games.AskAct(Key, Index, PoolGame.ActShoot, 0, 0, 0);
            return;
        }
        var stick = new Vector2(Input.GetActionStrength(Right) - Input.GetActionStrength(Left), Input.GetActionStrength(Fwd) - Input.GetActionStrength(Back));
        if (g.InHand)
        {
            PoolRig.Aim = null;
            if (!_handSet) { _hand = g.Table.P[0]; _handSet = true; }
            // from above with +z up the screen: the stick moves the ball as it looks
            _hand += new Vector2(-stick.X, stick.Y) * 0.6f * dt * (Input.IsActionPressed(Fine) ? 0.2f : 1f);
            _hand = new Vector2(Mathf.Clamp(_hand.X, -PoolRig.Cloth.Hx + PoolSolver.R, PoolRig.Cloth.Hx - PoolSolver.R),
                Mathf.Clamp(_hand.Y, -PoolRig.Cloth.Hz + PoolSolver.R, PoolRig.Cloth.Hz - PoolSolver.R));
            PoolRig.Holding = _hand;
            if (Input.IsActionJustPressed(Strike)) Games.AskAct(Key, Index, PoolGame.ActPlace, (int)(_hand.X * 1000), (int)(_hand.Y * 1000));
            _aimSet = false;
            return;
        }
        _handSet = false;
        PoolRig.Holding = null;
        if (!_aimSet || _aimedShot != g.ShotSeq)
        {
            // a fresh shot: aimed at the nearest legal ball
            _aimedShot = g.ShotSeq;
            _aimSet = true;
            _angle = StartAim(g, seat);
            _follow = 0;
        }
        float rate = Input.IsActionPressed(Fine) ? FineRate : TurnRate;
        _angle -= stick.X * rate * dt;
        _angle -= (Input.GetActionStrength(NudgeRight) - Input.GetActionStrength(NudgeLeft)) * FineRate * dt;
        if (!_holding) _follow = Mathf.Clamp(_follow + stick.Y * dt, -1, 1);
        if (Input.IsActionJustPressed(Strike)) { _holding = true; _swing = 0; }
        float power = 0;
        if (_holding)
        {
            _swing += dt;
            float phase = _swing / (SwingTime / 2) % 2;
            power = 1 - Mathf.Abs(phase - 1);
            if (!Input.IsActionPressed(Strike))
            {
                _holding = false;
                int deg = (int)Math.Round((Mathf.RadToDeg(_angle) % 360 + 360) % 360 * 1000);
                Games.AskAct(Key, Index, PoolGame.ActShoot, deg, (int)(power * 1000), (int)(_follow * 1000));
            }
        }
        PoolRig.Aim = (_angle, power);
    }

    /// <summary>The aim to start a shot at: the nearest ball the player may hit.</summary>
    private static float StartAim(PoolGame g, int seat)
    {
        var cue = g.Table.P[0];
        float best = float.MaxValue;
        var at = cue + Vector2.Down;
        for (int b = 1; b < PoolSolver.Balls; b++)
            if (g.Table.On[b] && g.Legal(seat, b) && cue.DistanceTo(g.Table.P[b]) < best)
            {
                best = cue.DistanceTo(g.Table.P[b]);
                at = g.Table.P[b];
            }
        return Mathf.DegToRad(PoolGame.AngleTo(cue, at) / 1000f);
    }

    public override IEnumerable<(string Action, string Text)> Prompts(TableGame? game, int seat)
    {
        if (game is not PoolGame g || !MyShot(g, seat)) yield break;
        if (g.State == PoolGame.Phase.Over) { yield return (Strike, "Rack again"); yield break; }
        if (g.InHand)
        {
            yield return (Fwd, "Move the cue ball");
            yield return (Strike, "Put it down");
            yield break;
        }
        yield return (Left, "Aim");
        yield return (Fine, "Fine aim");
        yield return (Fwd, _follow > 0.05f ? $"Follow {_follow * 100:F0}%" : _follow < -0.05f ? $"Draw {-_follow * 100:F0}%" : "Follow / draw");
        yield return (Strike, "Hold to strike, let go");
    }

    public override void Exit()
    {
        if (GodotObject.IsInstanceValid(Rig))
        {
            PoolRig.Aim = null;
            PoolRig.Holding = null;
        }
    }

    // ---- the catalog's row ---------------------------------------------------------------------

    public static GameSpec Spec => new(
        FurnitureType.PoolTable, GameKind.Pool, "Pool table",
        f => { var c = PoolGame.Cloth(f.W, f.D); return new PoolGame(c.Hx, c.Hz, System.Random.Shared.Next()); },
        (local, f) => GameCatalog.Round(local, f, 1.0f),
        f => (new Vector3(0, 0.45f, 0), new Vector3(f.W, 0.9f, f.D)),
        PoolRig.Create,
        () => new PoolPlay());
}
