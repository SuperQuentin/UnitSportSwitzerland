using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// At night an open front door lets the room's light out: a warm pool on the step, the ground in
/// front and anyone standing in it, fading over a few metres into the dark street, so walking out
/// of a lit room is not a step off a cliff. Client only.
///
/// <para>
/// Two halves, because the world is lit two ways. The <c>ps1_*</c> world shaders are unshaded and
/// light themselves: they add <c>door_light()</c> (<c>door_light.gdshaderinc</c>) to the tint,
/// from four global uniforms written here. Avatars and vehicles are standard materials: each of
/// those four doors also gets a real <see cref="OmniLight3D"/>, the same colour and reach. Both go
/// to the open doors nearest the camera, scaled by how far open each swings and by the night.
/// </para>
/// </summary>
public partial class DoorLights : Node3D, Core.IOriginContainer
{
    /// <summary>Doors lit at once; one global uniform each (<c>world_door_light_N</c>).</summary>
    public const int Slots = 4;

    /// <summary>How far the light carries, in metres; <c>DOOR_REACH</c> in the shader include.</summary>
    public const float Reach = 7f;

    /// <summary>
    /// Where the light seems to come from, in the doorway's frame (Z out): just inside the room, a
    /// little over head height. Behind the facade's plane, so the wall beside the door, facing
    /// out, gets none of it (<c>door_light</c> lights only what faces the lamp), while the step,
    /// the ground in front, the jambs and whoever stands there do.
    /// </summary>
    private static readonly Vector3 Lamp = new(0, 1.7f, -0.6f);

    /// <summary>The room's lamps, as seen; <c>DOOR_LAMP</c> in the shader include is its linear value.</summary>
    private static readonly Color Warm = new(1.0f, 0.84f, 0.64f);

    private const float Energy = 1.6f;

    private static readonly StringName[] Globals =
        Enumerable.Range(0, Slots).Select(i => new StringName($"world_door_light_{i}")).ToArray();

    private readonly Func<IEnumerable<DoorLink>> _links;
    private readonly OmniLight3D[] _lights = new OmniLight3D[Slots];
    private int _lit;

    public DoorLights(Func<IEnumerable<DoorLink>> links) => _links = links;

    public override void _Ready()
    {
        for (int i = 0; i < Slots; i++)
        {
            _lights[i] = new OmniLight3D
            {
                Name = $"Door{i}",
                LightColor = Warm,
                OmniRange = Reach,
                OmniAttenuation = 1.6f,
                ShadowEnabled = false,
                Visible = false,
            };
            AddChild(_lights[i]);
        }
    }

    public override void _ExitTree()
    {
        for (int i = 0; i < Slots; i++) RenderingServer.GlobalShaderParameterSet(Globals[i], Vector4.Zero);
        _lit = 0;
    }

    // reused every frame (#221): the nearest open doors, and what each slot last sent
    private readonly List<(float D, Vector3 At, float Strength)> _lit2 = new();
    private readonly Vector4[] _sent = new Vector4[Slots];

    public override void _Process(double delta)
    {
        float night = World.DayNight.Instance?.Night ?? 0f;
        var cam = GetViewport()?.GetCamera3D();
        var lit = _lit2;
        lit.Clear();
        if (night > 0f && cam != null)
        {
            var eye = cam.GlobalPosition;
            // from inside, the camera is 3 km down: measure to the doorway on its own side
            foreach (var l in _links())
                if (l.Swing > 0f)
                    lit.Add((Mathf.Min(eye.DistanceSquaredTo(l.Outside.Origin), eye.DistanceSquaredTo(l.Inside.Origin)),
                        l.Outside * Lamp, night * Mathf.SmoothStep(0f, 1f, l.Swing)));
            if (lit.Count > 1) lit.Sort(static (a, b) => a.D.CompareTo(b.D));
        }
        int count = Math.Min(lit.Count, Slots);

        for (int i = 0; i < Slots; i++)
        {
            var light = _lights[i];
            if (i < count)
            {
                var (_, at, strength) = lit[i];
                var v = new Vector4(at.X, at.Y, at.Z, strength);
                if (i >= _lit || v != _sent[i]) RenderingServer.GlobalShaderParameterSet(Globals[i], _sent[i] = v);
                light.GlobalPosition = at;
                light.LightEnergy = strength * Energy;
                light.Visible = true;
            }
            else if (i < _lit)
            {
                RenderingServer.GlobalShaderParameterSet(Globals[i], Vector4.Zero);
                light.Visible = false;
            }
        }
        _lit = count;
    }
}
