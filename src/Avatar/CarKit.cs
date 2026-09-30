using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// A car preset's off-road look (#40, <c>Player/CarSetup</c>), added over a built <see cref="CarRig"/>:
/// tread blocks round every tyre, a roof rack with a spare wheel and jerrycans, a tubular bumper
/// bar. Authored like <see cref="CarMeshBuilder"/> (+Z forward, ground frame, turned by
/// <see cref="MeshScratch.Build()"/>) from the same proportions, so it sits on any body shape.
/// All of it is drawn mesh, so the hull and hurtbox (measured from the mesh) grow with it.
/// </summary>
public static class CarKit
{
    private static readonly Color Rubber = new(0.07f, 0.07f, 0.08f);
    /// <summary>Tread blocks: a shade lighter than the tyre, or a knobbly tyre reads as a smooth one.</summary>
    private static readonly Color Lug = new(0.17f, 0.16f, 0.15f);
    private static readonly Color Bar = new(0.1f, 0.1f, 0.11f);
    // olive drab, lying flat: upright and red they read as police lights on the roof
    private static readonly Color Can = new(0.29f, 0.33f, 0.17f);
    private static readonly Color Lamp = new(1f, 0.96f, 0.8f);

    /// <param name="body">The rig's body node (pitched, raised by the lift): the rack and the bar go on it.</param>
    /// <param name="spins">The four wheel spin nodes: the tread turns with them.</param>
    public static void Fit(Node3D body, CarBody b, float wheelbase, Node3D[] spins)
    {
        if (b.Tread == 0 && !b.RoofRack && !b.BullBar) return;
        var d = CarMeshBuilder.For(b, wheelbase);
        var material = HumanMeshBuilder.Material();
        // the shell hangs 0.5 m below the body pivot (CarRig.Assemble): so does the kit
        var offset = new Vector3(0, -0.5f, 0);

        if (b.Tread > 0)
        {
            var tread = Tread(d.WheelR, d.TyreW, b.Tread);
            foreach (var spin in spins)
                spin.AddChild(new MeshInstance3D { Name = "Tread", Mesh = tread, MaterialOverride = material });
        }

        var s = new MeshScratch();
        float hl = d.Length * 0.5f, hw = d.Width * 0.5f;
        if (b.RoofRack)
        {
            // feet on the roof, two rails, cross bars; a spare lying flat and two cans on it
            float z0 = d.RgTop + 0.08f, z1 = d.WsTop - 0.08f, y = d.Roof + 0.1f;
            float rx = Mathf.Min(hw - 0.12f, 0.7f);
            foreach (float sx in new[] { -1f, 1f })
            {
                s.Box(new Vector3(sx * rx, y, (z0 + z1) * 0.5f), new Vector3(0.04f, 0.04f, z1 - z0), Bar);
                foreach (float z in new[] { z0 + 0.05f, z1 - 0.05f })
                    s.Box(new Vector3(sx * rx, d.Roof + 0.05f, z), new Vector3(0.05f, 0.1f, 0.05f), Bar);
            }
            for (int i = 0; i < 4; i++)
                s.Box(new Vector3(0, y, Mathf.Lerp(z0, z1, i / 3f)), new Vector3(2f * rx, 0.03f, 0.04f), Bar);
            float spare = Mathf.Min(d.WheelR, (z1 - z0) * 0.3f);
            float sz = z0 + spare + 0.05f;
            s.Ring(new Vector3(0, y + 0.02f + d.TyreW * 0.5f, sz), Vector3.Up, spare - 0.12f, spare, d.TyreW, Rubber, 14);
            foreach (float sx in new[] { -1f, 1f })
                s.Box(new Vector3(sx * 0.2f, y + 0.09f, z1 - 0.25f), new Vector3(0.3f, 0.14f, 0.42f), Can);
        }
        if (b.BullBar)
        {
            // two uprights out front, a hoop over the lamps, a lower bar; two spot lamps on top
            float z = hl + 0.1f, top = d.Hood + 0.08f;
            foreach (float sx in new[] { -1f, 1f })
            {
                float x = sx * (hw - 0.35f);
                s.Tube(new Vector3(x, 0.25f, z), new Vector3(x, top, z), 0.035f, Bar, 6);
                s.Tube(new Vector3(x, 0.3f, z), new Vector3(x, 0.3f, hl - 0.1f), 0.03f, Bar, 6);
                s.Box(new Vector3(x, top + 0.07f, z), new Vector3(0.14f, 0.12f, 0.1f), Bar);
                s.Box(new Vector3(x, top + 0.07f, z + 0.055f), new Vector3(0.1f, 0.08f, 0.02f), Lamp);
            }
            s.Tube(new Vector3(-(hw - 0.35f), top, z), new Vector3(hw - 0.35f, top, z), 0.035f, Bar, 6);
            s.Tube(new Vector3(-(hw - 0.2f), 0.32f, z), new Vector3(hw - 0.2f, 0.32f, z), 0.035f, Bar, 6);
        }
        if (b.RoofRack || b.BullBar)
            body.AddChild(new MeshInstance3D { Name = "Kit", Mesh = s.Build(), MaterialOverride = material, Position = offset });
    }

    /// <summary>Blocks round a tyre of radius <paramref name="r"/> and width <paramref name="w"/>, about X at the origin; bigger and fewer for mud.</summary>
    private static ArrayMesh Tread(float r, float w, int tread)
    {
        var s = new MeshScratch();
        int n = tread >= 2 ? 12 : 18;
        float depth = tread >= 2 ? 0.045f : 0.025f;
        float arc = Mathf.Tau * r / n * (tread >= 2 ? 0.5f : 0.55f);
        for (int i = 0; i < n; i++)
        {
            float a = Mathf.Tau * i / n;
            var basis = new Basis(Vector3.Right, a);
            // staggered across the tread, and wrapping onto the shoulders
            float x = (i % 2 == 0 ? 1f : -1f) * w * 0.18f;
            s.Box(basis * new Vector3(x, r - depth * 0.3f, 0), new Vector3(w * 0.62f, depth, arc), Lug, basis);
            foreach (float sx in new[] { -1f, 1f })
                s.Box(basis * new Vector3(sx * (w * 0.5f + 0.005f), r - 0.04f, 0), new Vector3(0.02f, 0.06f, arc * 0.8f), Lug, basis);
        }
        return s.Build();
    }
}
