using Godot;
using UnitSport.Avatar;

namespace UnitSport.Occasions;

/// <summary>
/// The sightings drawn (client): a saucer rig (<see cref="UfoRig"/>, #861) for each craft in sight,
/// kept while it flies and posed every frame from its plan (<see cref="SightingPath"/>), and the
/// mothership's rig when one hangs in the sky. Placed from LV95 each frame, so a floating-origin shift
/// moves nothing by hand. Nothing is built per frame but a rig when a craft first comes into sight.
/// </summary>
public partial class UfoSightings
{
    /// <summary>Saucers further than this are not drawn, m (their lamps carry a long way at night).</summary>
    private const float SaucerRange = 3500f, MothershipRange = 12000f;

    private readonly Dictionary<(int, int), UfoRig> _rigs = new();
    private readonly List<(int, int)> _unused = new();
    private readonly HashSet<(int, int)> _seen = new();
    private MothershipRig? _mother;
    private int _motherOf = -1;

    private void ReadyDraw() { }

    public override void _Process(double delta)
    {
        if (_dedicated) return;
        float dt = (float)delta;
        var cam = GetViewport()?.GetCamera3D();
        _seen.Clear();
        bool shown = false;
        if (cam != null && SeesSightings)
        {
            var eye = cam.GlobalPosition;
            double now = Now;
            foreach (var s in _plans.Values)
            {
                if (now < s.Start || now > s.End) continue;
                for (int c = 0; c < s.Count; c++)
                {
                    var pose = SightingPath.Pose(s, c, now);
                    if (!pose.Shown) continue;
                    var at = WorldOf(s, pose);
                    if (s.Kind == SightingKind.Mothership)
                    {
                        if (at.DistanceTo(eye) > MothershipRange) continue;
                        ShowMother(s.Id, at, pose);
                        shown = true;
                        continue;
                    }
                    if (Hidden(s, c, now) || at.DistanceSquaredTo(eye) > SaucerRange * SaucerRange) continue;
                    var key = (s.Id, c);
                    _seen.Add(key);
                    if (!_rigs.TryGetValue(key, out var rig))
                    {
                        rig = UfoRig.Create(null);
                        rig.Name = $"Sighting{s.Id}_{c}";
                        AddChild(rig);
                        _rigs[key] = rig;
                    }
                    rig.Position = at;
                    rig.Rotation = new Vector3(0f, pose.Yaw, 0f);
                    float tau = (float)(now - s.Start);
                    bool landed = s.Kind == SightingKind.Landing && tau >= SightingPath.LandTouch && tau < SightingPath.LandLift;
                    rig.Show(new UfoRig.Look(pose.Hum, pose.Legs, pose.Ramp, pose.Beam, pose.Lights, landed), dt);
                }
            }
        }

        // rigs whose craft left the sky (or this viewer's sight) go
        _unused.Clear();
        foreach (var key in _rigs.Keys) if (!_seen.Contains(key)) _unused.Add(key);
        foreach (var key in _unused)
        {
            _rigs[key].QueueFree();
            _rigs.Remove(key);
        }
        if (!shown && _mother != null)
        {
            _mother.QueueFree();
            _mother = null;
            _motherOf = -1;
        }
    }

    private void ShowMother(int id, Vector3 at, in CraftPose pose)
    {
        if (_mother == null || _motherOf != id)
        {
            _mother?.QueueFree();
            _mother = MothershipRig.Create();
            _mother.Name = $"Mothership{id}";
            AddChild(_mother);
            _motherOf = id;
        }
        _mother.Position = at;
        _mother.Rotation = new Vector3(0f, pose.Yaw, 0f);
        if (_mother.Beam != pose.Beam) _mother.Beam = pose.Beam;
    }

    /// <summary>Saucer rigs drawn now (probes).</summary>
    public int DrawnSaucers => _rigs.Count;
}
