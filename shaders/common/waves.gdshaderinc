// The wave field (#299): a sum of six Gerstner waves, the one src/World/WaveSpectrum.cs evaluates
// for the physics, term for term. No number lives here: World/WaterField.PushGlobals writes each
// wave's wave vector, angular frequency and phase, the amplitudes of the current sea state and the
// chop; World/WaterSurface writes the wave clock (the server's, modulo 20 min) every frame. With
// the globals at their project.godot defaults (all zero) the water is flat.
//
// `p` is pattern XZ (world_origin.gdshaderinc's pattern_xz): every wave vector is a whole number
// of cycles over its 9.6 km period, so the floating origin moving does not move the waves.
// `scale` (0..1) is the water layer's share of the sea state there (fetch x depth), from the mesh.
#ifndef COMMON_WAVES
#define COMMON_WAVES
global uniform vec4 water_wave_0;
global uniform vec4 water_wave_1;
global uniform vec4 water_wave_2;
global uniform vec4 water_wave_3;
global uniform vec4 water_wave_4;
global uniform vec4 water_wave_5;
// amplitudes of waves 0-3; waves 4, 5, the chop (horizontal over vertical) and a spare
global uniform vec4 water_amp_a;
global uniform vec4 water_amp_b;
global uniform float water_time;

// One wave: adds its displacement to d and its derivatives to the two tangents' offsets.
void water_wave_term(vec4 w, float a, float chop, vec2 p, inout vec3 d, inout vec3 tx, inout vec3 tz) {
    if (a == 0.0) {
        return;
    }
    vec2 dir = w.xy / length(w.xy);
    float theta = dot(w.xy, p) - w.z * water_time + w.w;
    float s = sin(theta);
    float c = cos(theta);
    float h = chop * a;
    d += vec3(-h * dir.x * s, a * c, -h * dir.y * s);
    tx += vec3(-h * dir.x * c, -a * s, -h * dir.y * c) * w.x;
    tz += vec3(-h * dir.x * c, -a * s, -h * dir.y * c) * w.y;
}

// The displacement of the surface point at rest at pattern XZ p (x, z horizontal, y up from the
// still level), and the displaced surface's unit normal.
vec3 water_wave_displace(vec2 p, float scale, out vec3 normal) {
    vec3 d = vec3(0.0);
    vec3 tx = vec3(0.0);
    vec3 tz = vec3(0.0);
    if (scale > 0.0) {
        float chop = water_amp_b.z;
        water_wave_term(water_wave_0, water_amp_a.x * scale, chop, p, d, tx, tz);
        water_wave_term(water_wave_1, water_amp_a.y * scale, chop, p, d, tx, tz);
        water_wave_term(water_wave_2, water_amp_a.z * scale, chop, p, d, tx, tz);
        water_wave_term(water_wave_3, water_amp_a.w * scale, chop, p, d, tx, tz);
        water_wave_term(water_wave_4, water_amp_b.x * scale, chop, p, d, tx, tz);
        water_wave_term(water_wave_5, water_amp_b.y * scale, chop, p, d, tx, tz);
    }
    // the tangents d/dx0 = (1, 0, 0) + tx and d/dz0 = (0, 0, 1) + tz; normal = Tz x Tx
    normal = normalize(cross(vec3(0.0, 0.0, 1.0) + tz, vec3(1.0, 0.0, 0.0) + tx));
    return d;
}
#endif
