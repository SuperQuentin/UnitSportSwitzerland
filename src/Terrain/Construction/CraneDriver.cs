using Godot;
using UnitSport.Core;
using UnitSport.XR;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// For checks and probes (#618): works a crane the local player holds on the real levers
/// (<see cref="XrPad.Press"/>, so the same bindings as a player's keys), as a person would — hook up
/// clear, round, out or in, then down until it stops on whatever is under it.
/// </summary>
public static class CraneDriver
{
    private static async Task Frames(Node host, int n)
    {
        for (int i = 0; i < n; i++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    /// <summary>
    /// The hook over <paramref name="target"/> (world) and lowered onto what is under it. Returns
    /// how far off the target the hook ended, flat, m.
    /// </summary>
    public static async Task<float> SteerTo(Node host, SiteCranes.Live crane, Vector3 target)
    {
        var (yaw, reach) = CraneMotion.Aim(crane.Job, target - crane.Owner.GlobalPosition);
        await Drive(host, PlayerInput.ArmBoomUp, PlayerInput.ArmBoomDown, () => CraneControl.Top(crane.Job) - 1f - crane.Last.HookY, 1.2f);
        await Drive(host, PlayerInput.ArmSlewLeft, PlayerInput.ArmSlewRight, () => Mathf.AngleDifference(crane.Last.Yaw, yaw),
            0.5f * CraneControl.SlewRate * CraneControl.Ramp + 0.003f);
        await Drive(host, PlayerInput.ArmStickOut, PlayerInput.ArmStickIn, () => reach - crane.Last.Trolley,
            0.5f * CraneControl.TrolleyRate * CraneControl.Ramp + 0.02f);
        var hook = crane.HookWorld(crane.Last);
        float off = new Vector2(hook.X - target.X, hook.Z - target.Z).Length();
        await Lower(host, crane);
        return off;
    }

    /// <summary>The hook down until it stops on what is under it (or its pallet does).</summary>
    public static async Task Lower(Node host, SiteCranes.Live crane)
    {
        XrPad.Press(PlayerInput.ArmBoomDown, true);
        float last = float.MaxValue;
        double t0 = GameClock.Now;
        while (GameClock.Now - t0 < 60)
        {
            await Frames(host, 30);
            if (Mathf.Abs(crane.Last.HookY - last) < 0.002f) break;
            last = crane.Last.HookY;
        }
        XrPad.Press(PlayerInput.ArmBoomDown, false);
        await Frames(host, 70);
    }

    /// <summary>
    /// Holds one lever of a pair until <paramref name="error"/> (how far to go, signed) is within
    /// <paramref name="coast"/>, which the drive covers as it ramps down; then short taps trim what
    /// is left over.
    /// </summary>
    public static async Task Drive(Node host, string plus, string minus, Func<float> error, float coast)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            float e = error();
            if (Mathf.Abs(e) <= coast * 0.25f) return;
            string lever = e > 0 ? plus : minus;
            XrPad.Press(lever, true);
            double t0 = GameClock.Now;
            float limit = pass == 0 ? coast : coast * 0.2f;
            while (Mathf.Sign(error()) == Mathf.Sign(e) && Mathf.Abs(error()) > limit && GameClock.Now - t0 < 60)
            {
                await Frames(host, 1);
                // a trimming pass is a tap, which barely ramps up
                if (pass > 0 && GameClock.Now - t0 > 0.12) break;
            }
            XrPad.Press(lever, false);
            await Frames(host, 80);
        }
    }

    /// <summary>One of the shell's own drops whose top is the slab, not the stair well or a mast's opening: tile-local.</summary>
    public static Vector3? DropOnSlab(SiteCranes.Live crane)
    {
        foreach (var d in crane.Job.Drops)
        {
            var w = crane.ToWorld(d);
            if (Mathf.Abs(crane.FloorUnder(w + Vector3.Up * 5f) - w.Y) < 0.2f) return d;
        }
        return null;
    }
}
