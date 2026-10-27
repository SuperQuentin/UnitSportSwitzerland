using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// Hidden crevasses (#1266 part 4), the owner's: walking on glacier cover (the cover raster, kept near the player, or a
/// check's <c>--together-glacier</c> box), a step onto a crevasse's strip breaks its snow bridge. Roped to a partner who
/// stands, the rope catches it at the lip: a stumble and the zip, no harm. Unroped: the slot opens (a
/// <see cref="CrevasseNode"/>), the body stumbles back from its lip, winded. Behind <c>Settings › Gameplay › Crevasses</c>.
/// The server never needs it (nothing new is sent). One hash per 12 m cell entered.
/// </summary>
public partial class TogetherService
{
    private long _crevasseCell = long.MinValue;
    private readonly HashSet<long> _broken = new();

    /// <summary>The crevasses this client broke, and how it went (checks).</summary>
    public int CrevassesBroken { get; private set; }
    public int CrevassesCaught { get; private set; }

    /// <summary>Per physics step, the own body on foot on a glacier: a crevasse's bridge under it breaks.</summary>
    private void StepCrevasses(float dt)
    {
        if (!GameSettings.Current.Crevasses || Permissions.InMatch || Me?.Invoke() is not { } me || !me.IsOnFloor() || me.RideKindId != 0) return;
        var g = me.Global;
        long cell = Crevasses.CellKey(g.E, g.N);
        bool sameCell = cell == _crevasseCell;
        _crevasseCell = cell;
        // the cell's crevasse at all (one hash), then the glacier under the body (the cover lookup)
        if (sameCell && !Crevasses.At(g.E, g.N)) return;
        if (Crevasses.InCell(g.E, g.N) is not { } c || !Crevasses.At(g.E, g.N) || _broken.Contains(cell) || !OnGlacier(g)) return;
        _broken.Add(cell);
        Break(me, c);
    }

    private void Break(FootPlayer me, (double E, double N, double Angle) c)
    {
        long self = Self;
        bool held = Links.ModeOf(self) == LinkMode.Rope && Body(Links.PartnerOf(self)) is { } pb
            && pb.PoseKind == FootPlayer.PoseStride && pb.RideKindId == 0;
        var origin = me.Origin;
        if (origin != null && !Net.NetworkManager.DedicatedServer)
        {
            var slot = new CrevasseNode { Name = "Crevasse" };
            AddChild(slot);
            var at = origin.ToWorld(c.E, c.N, me.Global.Alt);
            slot.GlobalPosition = new Vector3(at.X, me.GlobalPosition.Y, at.Z);
            // the strip's heading: along E-N (x, -z in the world)
            slot.Rotation = new Vector3(0, (float)c.Angle - Mathf.Pi / 2, 0);
        }
        // pushed back off the lip, across the strip, toward where the body came from
        var away = new Vector3((float)-Math.Sin(c.Angle), 0, (float)-Math.Cos(c.Angle));
        var to = me.GlobalPosition - (origin?.ToWorld(c.E, c.N, me.Global.Alt) ?? me.GlobalPosition);
        if (away.Dot(to with { Y = 0 }) < 0) away = -away;
        me.Velocity = away * (held ? 2.5f : 4f) + Vector3.Up * 2f;
        PlayAt(self, TogetherSounds.Zip);
        if (held)
        {
            CrevassesCaught++;
            GD.Print("[together] crevasse: the bridge broke, caught by the rope");
            Toast?.Invoke("A hidden crevasse! Your rope partner held you.");
        }
        else
        {
            CrevassesBroken++;
            me.DebugSetStamina(0f);
            GD.Print("[together] crevasse: the bridge broke, unroped: winded at the lip");
            Toast?.Invoke("A hidden crevasse! Rope up on a glacier.");
        }
    }
}
