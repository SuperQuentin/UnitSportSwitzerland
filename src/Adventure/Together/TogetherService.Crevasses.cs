using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// Hidden crevasses (#1266 part 4, #1372), the owner's: walking on glacier cover (the cover raster, kept near the player, or a
/// check's <c>--together-glacier</c> box), a step onto a crevasse's strip breaks its snow bridge and a <see cref="CrevasseNode"/>
/// opens under it, on this client only. The body is let go of the ground (<see cref="FootPlayer.EnterCrevasse"/>: the terrain
/// layer leaves its mask, the slot's walls and floor take its place) and falls the 10 m. Roped to a partner who stands within the
/// rope's length, the rope catches it 3 m under the lip (it hangs there, the partner hauls with a held E, as for any hanging
/// partner). Unroped it lands on the ice floor and climbs the walls out (they are climbable); after a minute of failing it is set
/// on the lip. Out, the ground is solid again. While inside, the body's replicated pose is <see cref="FootPlayer.PoseCrevasse"/>:
/// the server sees a body 10 m under the ground and its <c>StaminaWatch</c> skips it. Behind <c>Settings › Gameplay › Crevasses</c>
/// and <c>--crevasses off|on</c> (<see cref="Permissions.CrevassesOn"/>). Nothing is sent; one hash per 12 m cell entered.
/// </summary>
public partial class TogetherService
{
    private long _crevasseCell = long.MinValue;
    private readonly HashSet<long> _broken = new();
    private CrevasseNode? _slot;
    private bool _slotLanded;
    private double _slotInside, _slotDeepest;
    private float _slotSurface;

    /// <summary>The crevasses this client broke, and how it went (checks).</summary>
    public int CrevassesBroken { get; private set; }
    public int CrevassesCaught { get; private set; }
    /// <summary>Got out on its own or hauled (the ground is solid again at the lip).</summary>
    public int CrevassesOut { get; private set; }
    public int CrevassesRescued { get; private set; }

    /// <summary>Seconds in a slot before a body that cannot get out is set on the lip (a check shortens it).</summary>
    public static double RescueAfter { get; set; } = Crevasses.RescueSeconds;

    /// <summary>The own body is down a crevasse now (checks).</summary>
    public bool InSlot => _slot != null;

    /// <summary>The open slot of the own body's fall, or null (checks).</summary>
    public CrevasseNode? Slot => _slot;

    /// <summary>Per physics step, the own body: on foot on a glacier a crevasse's bridge under it breaks; down a slot it climbs out.</summary>
    private void StepCrevasses(float dt)
    {
        if (Me?.Invoke() is not { } me) return;
        if (_slot != null || me.InCrevasse) { StepSlot(me, dt); return; }
        if (!Permissions.CrevassesOn || !me.IsOnFloor() || me.RideKindId != 0) return;
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
        var origin = me.Origin;
        var g = me.Global;
        var partner = Links.ModeOf(self) == LinkMode.Rope ? Body(Links.PartnerOf(self)) : null;
        bool stands = partner != null && IsInstanceValid(partner) && partner.PoseKind == FootPlayer.PoseStride && partner.RideKindId == 0;
        bool held = stands && Crevasses.CaughtByRope(true, (partner!.GlobalPosition - me.GlobalPosition).Length(), LinkBook.RopeLength);
        GD.Print($"[together] crevasse: mode {Links.ModeOf(self)}, partner {Links.PartnerOf(self)}, body {partner?.PoseKind}, held {held}");
        if (origin == null) return;

        // the slot opens on the strip's line under the body, turned along it, its walls a little over the ground at both ends
        var (le, ln) = Crevasses.OnLine(c, g.E, g.N);
        var at = origin.ToWorld(le, ln, g.Alt);
        var (he, hn) = Crevasses.Heading(c.Angle);
        var along = origin.ToWorld(le + he, ln + hn, g.Alt) - at;
        along.Y = 0;
        along = along.Normalized();
        float feetY = me.GlobalPosition.Y;
        _slotSurface = feetY;
        var slot = new CrevasseNode { Name = "Crevasse" };
        if (me.Terrain is { } terrain)
        {
            float top = feetY;
            foreach (float k in new[] { -CrevasseNode.Length / 2, 0f, CrevasseNode.Length / 2 })
                if (terrain.TryGetHeight(at + along * k, out float h)) top = Mathf.Max(top, h);
            slot.Top = Mathf.Clamp(top - feetY + 0.3f, 0.3f, 4f);
        }
        AddChild(slot);
        slot.GlobalPosition = new Vector3(at.X, feetY, at.Z);
        slot.Rotation = new Vector3(0, Mathf.Atan2(along.X, along.Z), 0);
        _slot = slot;
        _slotLanded = false;
        _slotInside = 0;
        _slotDeepest = 0;

        // the snow gives way: slid onto the strip's line, the ground let go
        me.GlobalPosition = new Vector3(at.X, feetY, at.Z);
        me.Velocity = new Vector3(0, -1f, 0);
        me.SoftLanding = true;
        me.EnterCrevasse();
        PlayAt(self, TogetherSounds.Crack);
        if (held)
        {
            PinCrevasseRope(self, new Vector3(at.X, feetY + 0.3f, at.Z));   // a little over the snow, so the haul ends with the feet on it
            CrevassesCaught++;
            GD.Print("[together] crevasse: the bridge broke, the rope holds the fall");
            Toast?.Invoke("A hidden crevasse! Your rope partner holds you: they haul with a held E, or climb out.");
        }
        else
        {
            CrevassesBroken++;
            GD.Print("[together] crevasse: the bridge broke, unroped: down the slot");
            Toast?.Invoke("A hidden crevasse! Climb the ice walls out. Rope up on a glacier.");
        }
    }

    /// <summary>Down the slot: back at the top and out, or set on the lip after a minute, or let go when the body is somewhere else.</summary>
    private void StepSlot(FootPlayer me, float dt)
    {
        if (_slot == null || !IsInstanceValid(_slot)) { Leave(me); return; }
        var p = me.GlobalPosition;
        float ground = me.Terrain is { } t && t.TryGetHeight(p, out float h) ? h : _slotSurface;
        _slotDeepest = Math.Min(_slotDeepest, p.Y - ground);
        _slotInside += dt;
        if (!_slotLanded && me.IsOnFloor() && _slotDeepest < -CrevasseNode.Depth + 2f)
        {
            _slotLanded = true;
            PlayAt(Self, TogetherSounds.Thud);
        }
        // somewhere else (a respawn, a teleport): nothing to hold any more
        if (new Vector2(p.X - _slot.GlobalPosition.X, p.Z - _slot.GlobalPosition.Z).Length() > CrevasseNode.Length) { Leave(me); return; }
        if (Crevasses.Out(p.Y, ground, _slotDeepest))
        {
            CrevassesOut++;
            GD.Print("[together] crevasse: out");
            Leave(me);
            return;
        }
        // the floor never lets it fall further; the rescue is for a body that cannot climb, or that came through the floor
        if (_slotInside >= RescueAfter || p.Y < _slotSurface - CrevasseNode.Depth - 2f)
        {
            var lip = _slot.GlobalPosition + _slot.GlobalBasis.X.Normalized() * (float)(Crevasses.Width / 2 + 1.5);
            if (me.Terrain is { } terrain && terrain.TryGetHeight(lip, out float lh)) lip.Y = lh;
            me.GlobalPosition = lip + Vector3.Up * 0.3f;
            me.Velocity = Vector3.Zero;
            CrevassesRescued++;
            GD.Print("[together] crevasse: set on the lip (stuck)");
            Toast?.Invoke("You are back on the snow.");
            Leave(me);
        }
    }

    private void Leave(FootPlayer me)
    {
        me.LeaveCrevasse();
        me.SoftLanding = false;
        UnpinCrevasseRope(Self);
        _slot = null;
        // standing over the old strip: the bridge is broken, there is nothing left to break here
        _crevasseCell = long.MinValue;
    }
}
