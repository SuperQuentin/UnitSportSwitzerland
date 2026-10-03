// Boat foam (#380): the wake's patches lying on the water and the spray thrown off a hull, drawn on
// the same waves as the surface (common/waves, the function the water body displaces by), so the
// foam rides the swell instead of lying flat where it was dropped. One material for every style.
//
// The particles keep where they were dropped (local_coords off). A patch is put on the surface over
// its own centre: the rest point that the waves carried there is found by three fixed-point steps,
// as WaterField.TryLevelAt does, and the patch is laid on the tangent plane there. A spray speck
// faces the camera and is gone once it falls under the surface.
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, skip_vertex_transform;
#include "res://shaders/world_origin.gdshaderinc"
#include "res://shaders/common/waves.gdshaderinc"

// the still level and the water layer's wave scale where the emitter is (BoatRig, per emitter, on change)
instance uniform float still_level = 0.0;
instance uniform float wave_scale = 0.0;
// true: patches lying on the surface (the wake); false: specks facing the camera (spray)
uniform bool lying = true;
// over the drawn surface: the water mesh is the waves sampled every 2 m and joined by straight lines
uniform float lift = 0.06;

void vertex() {
    vec3 centre = MODEL_MATRIX[3].xyz;
    float spacing = max(2.0, distance(centre, CAMERA_POSITION_WORLD) * 0.008);
    vec2 p = pattern_xz(centre);
    vec3 n;
    vec3 d = water_wave_displace(p, wave_scale, spacing, n);
    d = water_wave_displace(p - d.xz, wave_scale, spacing, n);
    d = water_wave_displace(p - d.xz, wave_scale, spacing, n);
    float surface = still_level + d.y;
    vec3 world;
    if (lying) {
        // the patch's corner as the particle placed it, laid on the tangent plane at the surface
        vec3 r = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz - centre;
        r.y = -(n.x * r.x + n.z * r.z) / max(n.y, 0.2);
        world = vec3(centre.x, surface + lift, centre.z) + r;
    } else {
        // a speck facing the camera, at its particle's scale; under the water it is gone
        float size = length(MODEL_MATRIX[0].xyz);
        bool under = wave_scale > 0.0 && centre.y < surface - 0.02;
        world = centre + (INV_VIEW_MATRIX[0].xyz * VERTEX.x + INV_VIEW_MATRIX[1].xyz * VERTEX.y) * (under ? 0.0 : size);
    }
    VERTEX = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
    // a few centimetres toward the eye: the PS1 surface snaps its vertices to the pixel grid
    VERTEX += normalize(-VERTEX) * 0.04;
    NORMAL = (VIEW_MATRIX * vec4(n, 0.0)).xyz;
}

void fragment() {
    ALBEDO = COLOR.rgb;
    ALPHA = COLOR.a;
}
