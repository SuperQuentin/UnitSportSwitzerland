// The fun world's neon grid floor (#1396, World/Fun/FunBackdrop): a dark violet plane under the course reaching to the
// horizon, glowing lines every `spacing` metres. World-anchored (the floating origin moves by whole kilometres, a
// multiple of the spacing), the lines fading out with distance before they shimmer and the plane into the horizon's
// pink. The plane follows the camera; nothing is set per frame.
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;

uniform vec3 base : source_color = vec3(0.05, 0.01, 0.1);
uniform vec3 line_near : source_color = vec3(1.0, 0.2, 0.85);
uniform vec3 line_far : source_color = vec3(0.2, 0.85, 1.0);
uniform vec3 haze : source_color = vec3(0.45, 0.1, 0.42);
uniform float spacing = 20.0;
uniform float energy = 1.8;
uniform float fade_lines = 2600.0;
uniform float fade_floor = 16000.0;
// the course's tiles, world x/z (min x, min z, max x, max z): their own ground is there, the lake under the floor's height
uniform vec4 hole = vec4(0.0);

varying vec3 world;

void vertex() {
	world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (world.x > hole.x && world.x < hole.z && world.z > hole.y && world.z < hole.w) discard;
	vec2 g = world.xz / spacing;
	vec2 w = fwidth(g);
	vec2 a = abs(fract(g - 0.5) - 0.5) / max(w, vec2(1e-4));
	float line = 1.0 - min(min(a.x, a.y) / 1.5, 1.0);
	float dist = length(world.xz - CAMERA_POSITION_WORLD.xz);
	line *= 1.0 - smoothstep(fade_lines * 0.3, fade_lines, dist);
	vec3 lc = mix(line_near, line_far, smoothstep(500.0, fade_lines, dist)) * energy;
	vec3 c = mix(base, lc, line);
	ALBEDO = mix(c, haze * 0.8, smoothstep(fade_floor * 0.25, fade_floor, dist));
}
