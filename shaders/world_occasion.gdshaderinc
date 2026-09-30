// Occasion globals (src/Occasions, written every frame by World/DayNight). All zero on an ordinary
// day, and every helper returns its input unchanged at zero — so a world with no occasion running
// renders exactly as it did before these existed.

// 0..1: how white upward-facing surfaces turn
global uniform float world_snow;
// low-lying mist: its colour (linear, already lit), extinction per metre, and the world height
// it thins out to nothing at (the valley floor near the camera plus a margin)
global uniform vec3 world_mist_color;
global uniform float world_mist_density;
global uniform float world_mist_top;
// 0..1: festive lights; they still only shine at night (world_night)
global uniform float world_lights;

// Snow lies on what faces up. `hold` is how much of it this surface keeps (a road is cleared,
// a crown holds less than a roof). Linear colour, applied before lighting.
vec3 occasion_snow_amount(vec3 col, float amount) {
    if (world_snow <= 0.0) {
        return col;
    }
    return mix(col, vec3(0.80, 0.84, 0.90), clamp(amount, 0.0, 1.0) * world_snow);
}

vec3 occasion_snow(vec3 col, vec3 n, float hold) {
    return occasion_snow_amount(col, smoothstep(0.55, 0.85, n.y) * hold);
}

// Mist that pools in the valley: thickens with distance, only below world_mist_top. Applied
// after world_tint and before posterizing, so it is dithered like every other colour.
vec3 occasion_mist(vec3 col, vec3 wpos, float dist) {
    if (world_mist_density <= 0.0) {
        return col;
    }
    float below = smoothstep(world_mist_top, world_mist_top - 70.0, wpos.y);
    float amount = (1.0 - exp(-dist * world_mist_density)) * below;
    return mix(col, world_mist_color, amount);
}
