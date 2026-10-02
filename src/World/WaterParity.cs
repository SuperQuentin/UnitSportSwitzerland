using System.Threading.Tasks;
using Godot;

namespace UnitSport.World;

/// <summary>
/// <c>--waterparity</c> (#299), windowed (it needs a GPU; headless has none to read back): the shader's
/// wave function against C#'s. A SubViewport draws <c>water_wave_displace</c> from
/// <c>shaders/common/waves.gdshaderinc</c> for 256 sample points at the gamey sea state and a fixed
/// wave time, from the very globals <see cref="WaterField.PushGlobals"/> writes, each value packed
/// into 16 bits over ±4 m (red and green of one row per value); the probe reads the image back and compares every displacement
/// component and the normal with <see cref="WaveSpectrum"/>. Builds no world.
/// Prints <c>[waterparity] RESULT: ok</c> (largest error under 5 mm) or <c>RESULT: FAILED</c>.
/// </summary>
public partial class WaterParity : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--waterparity") >= 0;

    private const int Samples = 256;
    private const float Range = 4f;
    private const double Time = 437.123;
    /// <summary>Sample i is at Base + i * Step (pattern XZ, large values on purpose: float precision).</summary>
    private static readonly Vector2 Base = new(1234.5f, 6789.25f), Step = new(37.3f, 11.9f);

    private const string ShaderCode = """
        shader_type canvas_item;
        render_mode unshaded, blend_disabled;
        #include "res://shaders/common/waves.gdshaderinc"
        uniform vec2 base;
        uniform vec2 step;
        uniform float range;
        vec2 pack(float v) {
            float u = floor(clamp((v + range) / (2.0 * range), 0.0, 1.0) * 65535.0 + 0.5);
            float hi = floor(u / 256.0);
            return vec2(hi, u - hi * 256.0) / 255.0;
        }
        void fragment() {
            int i = int(FRAGCOORD.x);
            int row = int(FRAGCOORD.y);
            vec2 p = base + float(i) * step;
            float s = 0.25 + 0.25 * float(i % 4);
            vec3 n;
            vec3 d = water_wave_displace(p, s, n);
            // one value per row, in red and green: the alpha of the target is not ours to use
            float v = row == 0 ? d.y : row == 1 ? d.x : row == 2 ? d.z : n.y;
            COLOR = vec4(pack(v), 0.0, 1.0);
        }
        """;

    public override void _Ready() => _ = Run();

    private async Task Run()
    {
        WaterField.SetSeaState(1f);
        WaterField.PushGlobals();
        WaterField.PushTime(Time);

        var viewport = new SubViewport
        {
            Size = new Vector2I(Samples, 4),
            Disable3D = true,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        material.SetShaderParameter("base", Base);
        material.SetShaderParameter("step", Step);
        material.SetShaderParameter("range", Range);
        viewport.AddChild(new ColorRect { Size = new Vector2(Samples, 4), Material = material });
        AddChild(viewport);

        for (int i = 0; i < 6; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = viewport.GetTexture().GetImage();
        if (image == null || image.GetWidth() < Samples)
        {
            Done("no image read back (headless? run it windowed)", false);
            return;
        }

        var amp = WaterField.Amplitudes.ToArray();
        double t = (float)Time;
        double worst = 0, worstNormal = 0;
        int worstAt = -1;
        for (int i = 0; i < Samples; i++)
        {
            // as the GPU sees it: float sample point and time
            float px = Base.X + i * Step.X, pz = Base.Y + i * Step.Y;
            float s = 0.25f + 0.25f * (i % 4);
            WaveSpectrum.Displace(px, pz, t, s, amp, out double dx, out double dy, out double dz);
            WaveSpectrum.Normal(px, pz, t, s, amp, out _, out double ny, out _);
            double Row(int r) { var c = image.GetPixel(i, r); return Unpack(c.R, c.G); }
            double gy = Row(0), gx = Row(1), gz = Row(2), gny = Row(3);
            double err = Math.Max(Math.Abs(gy - dy), Math.Max(Math.Abs(gx - dx), Math.Abs(gz - dz)));
            if (err > worst) { worst = err; worstAt = i; }
            worstNormal = Math.Max(worstNormal, Math.Abs(gny - ny));
            if (i < 4) GD.Print($"[waterparity] sample {i}: gpu ({gx:F4}, {gy:F4}, {gz:F4}) c# ({dx:F4}, {dy:F4}, {dz:F4})");
        }
        GD.Print($"[waterparity] {Samples} samples, gamey, t {t:F3}: largest displacement error {worst * 1000:F2} mm (sample {worstAt}), normal.y {worstNormal:F5}");
        Done($"largest error {worst * 1000:F2} mm, normal {worstNormal:F5}", worst < 0.005 && worstNormal < 0.002);
    }

    private static double Unpack(float hi, float lo)
    {
        double u = Math.Round(hi * 255.0) * 256.0 + Math.Round(lo * 255.0);
        return u / 65535.0 * 2 * Range - Range;
    }

    private void Done(string what, bool ok)
    {
        GD.Print(ok ? $"[waterparity] RESULT: ok, {what}" : $"[waterparity] RESULT: FAILED, {what}");
        GetTree().Quit(ok ? 0 : 1);
    }
}
