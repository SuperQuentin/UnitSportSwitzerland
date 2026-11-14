using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// A staffed colony's colonists as decoration figures (#1945, the <c>VenuePatrons</c> pattern): each crew member of
/// <see cref="ColonyCrew"/> a <see cref="SkinnedFigure"/> in the silver suit (the helmet on outside), standing at their spot
/// or walking the colony's path between rooms, all from the shared clock (the simulation clock's seconds for the windows,
/// the sky's UTC for the sleep shifts), so every peer shows the same crew. A change that is not a walk (waking, going to
/// sleep, out on an EVA and back) happens only while neither place is in view. Nothing on the wire; a server bot would
/// play the same moment through its intents (#837: <c>bots/bot-capability-map</c>). Walkers are posed at 15 Hz near,
/// 5 Hz far; a standing one once.
/// </summary>
public partial class ColonyColonists : Node3D
{
    /// <summary>A figure this near the camera, in view, is "seen": its teleports wait.</summary>
    public const float SeenM = 60;

    private sealed class Member
    {
        public Colonist Who;
        public SkinnedFigure Figure = null!;
        public long Window = long.MinValue;
        public CrewMoment Moment;
        public double WalkFrom;
        public bool Shown, Outside, Helmet;
        public int Spot = -1;
        public double PosedAt = -1, Phase;
        public bool Walking;
    }

    private readonly ColonyCrew _crew;
    private readonly List<Member> _members = new();
    private static ShaderMaterial? _material;

    public ColonyCrew Crew => _crew;

    /// <summary>Seconds added to the crew's clock (the check's: to stand a few seconds into a window, walks under way).</summary>
    public static double ClockOffset;

    public ColonyColonists(MoonColony c, ColonyPlan plan)
    {
        Name = "Colonists";
        _crew = new ColonyCrew(c, plan);
        foreach (var who in _crew.Crew) _members.Add(new Member { Who = who });
    }

    public override void _Ready()
    {
        _material ??= HumanMeshBuilder.FigureMaterial();
        foreach (var m in _members)
        {
            m.Figure = new SkinnedFigure { Name = $"Colonist{m.Who.Index}", MaterialOverride = _material, Visible = false };
            AddChild(m.Figure);
        }
    }

    private static readonly Outfit Suit = Outfit.Of([ItemId.SilverSuit]);

    private void Dress(Member m, bool helmet)
    {
        var palette = HumanPalette.ForRider(m.Who.Look) with { Outfit = Suit };
        m.Figure.Show(new FigureLook(palette, Hat: helmet ? Headwear.SpaceHelmet : Headwear.None));
        m.Helmet = helmet;
        m.PosedAt = -1;
    }

    public override void _Process(double delta)
    {
        double t = SimClock.SimAt(ClockSync.ServerNow) + ClockOffset;
        var utc = SkyClock.Now.Utc;
        double hour = utc.Hour + utc.Minute / 60.0;
        var cam = GetViewport()?.GetCamera3D();
        foreach (var m in _members)
        {
            long w = (long)Math.Floor(t / ColonyCrew.Window);
            if (w != m.Window)
            {
                // the window's plan, read at its start (a walk's path comes with it)
                double t0 = w * ColonyCrew.Window;
                m.Moment = _crew.At(m.Who, t0 + 0.001, hour);
                m.WalkFrom = t0;
                m.Window = w;
            }
            var want = m.Moment;
            bool walking = want.Path != null && (t - m.WalkFrom) * ColonyCrew.WalkSpeed < ColonyCrew.Length(want.Path);
            // a teleport (asleep, awake, out, in) waits while the place left or the place reached is in view
            bool teleport = want.Shown != m.Shown || want.Outside != m.Outside || !walking && want.Spot != m.Spot && m.Spot >= 0 && !m.Walking;
            if (teleport && m.Figure.Visible && (Seen(cam, m.Figure.GlobalPosition) || want.Shown && Seen(cam, ToGlobal(SpotAt(want.Spot)))))
                continue;
            m.Shown = want.Shown;
            m.Outside = want.Outside;
            m.Figure.Visible = want.Shown;
            if (!want.Shown) { m.Spot = want.Spot; continue; }
            if (m.Helmet != want.Outside || m.Figure.Skin == null) Dress(m, want.Outside);
            if (walking)
            {
                var (p, heading) = ColonyCrew.Walk(want.Path!, (t - m.WalkFrom) * ColonyCrew.WalkSpeed);
                m.Figure.Transform = Facing(new Vector3((float)p.X, (float)p.Y, (float)p.Z), heading);
                m.Phase += delta * ColonyCrew.WalkSpeed / 1.4;
                bool near = cam != null && cam.GlobalPosition.DistanceTo(m.Figure.GlobalPosition) < 15;
                if (t - m.PosedAt > (near ? 1.0 / 15 : 1.0 / 5) || t < m.PosedAt)
                {
                    HumanMeshBuilder.PoseStride(m.Figure, (float)ColonyCrew.WalkSpeed, (float)(m.Phase % 1.0));
                    m.PosedAt = t;
                }
                m.Walking = true;
                m.Spot = want.Spot;
                continue;
            }
            if (m.Walking || m.Spot != want.Spot || m.PosedAt < 0)
            {
                var s = _crew.Spots[want.Spot];
                m.Figure.Transform = Facing(new Vector3((float)s.X, (float)s.Y, (float)s.Z), s.Facing);
                HumanMeshBuilder.PoseStride(m.Figure, 0f, 0f);
                m.PosedAt = t;
            }
            m.Walking = false;
            m.Spot = want.Spot;
        }
    }

    /// <summary>A figure at a point of the colony's frame facing a heading (radians from north): a figure faces its node's -Z.</summary>
    private static Transform3D Facing(Vector3 at, double heading) => new(new Basis(Vector3.Up, -(float)heading), at);

    private Vector3 SpotAt(int spot)
    {
        if (spot < 0) return Vector3.Zero;
        var s = _crew.Spots[spot];
        return new Vector3((float)s.X, (float)s.Y, (float)s.Z);
    }

    private static bool Seen(Camera3D? cam, Vector3 world) =>
        cam != null && cam.GlobalPosition.DistanceTo(world) < SeenM && !cam.IsPositionBehind(world) && cam.IsPositionInFrustum(world);

    // ---- for the checks ---------------------------------------------------------------------------------------------

    public static IEnumerable<ColonyColonists> In(Node root)
    {
        foreach (var n in root.FindChildren("Colonists", "", true, false))
            if (n is ColonyColonists c) yield return c;
    }

    /// <summary>The figures: where each stands (world), shown or not, walking, outside.</summary>
    public IEnumerable<(Vector3 At, bool Shown, bool Walking, bool Outside)> Figures()
    {
        foreach (var m in _members) yield return (m.Figure.GlobalPosition, m.Figure.Visible, m.Walking, m.Outside);
    }
}
