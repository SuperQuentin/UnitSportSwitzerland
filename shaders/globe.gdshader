// The globe from far out (#1430 H4, World/Globe/Globe.cs): the coarse cube tiles in real geometry,
// so no curve_offset (the flat tiles are bent onto a sphere; these already are one). Drawn a little
// behind whatever stands at the same place, so the flat world's tiles and horizon blocks win where
// they are, and the globe shows round and past them. Hazed as they are: the style's haze is all but
// gone by the time the flat world gives way (Styles/CameraGlobal.HazeAt), and an unhazed globe
// switching on at 15 km beside the hazed fill was a jump in the climb.
shader_type spatial;
render_mode unshaded, cull_back;

// toward the real sun, world axes (World/Globe/Atmosphere): the scene's light is the moon at night,
// and one light cannot put the terminator across the whole Earth anyway
global uniform vec3 world_true_sun;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_disable;
// the part of albedo_tex over this tile: a tile with no colour of its own (the leaves' level) takes its
// ancestor's (World/Globe/Globe.cs, Inherit)
uniform vec4 uv_rect = vec4(0.0, 0.0, 1.0, 1.0);
// how far behind, as a fraction of the distance from the eye
uniform float push = 0.004;
// while the generated fill is drawn (terrain.gdshaderinc, 0 until 50 km) the globe goes a quarter of
// the distance further behind, at most 50 km: the fill's made-up heights stand kilometres off
// ETOPO's, and the Alps poked through it from 15 km. Capped well inside the far plane (horizon +
// 200 km) and gone with the fill, so the air's ray (atmosphere.gdshader) ends near the ground again
global uniform float world_fill_hide;
const float FILL_PUSH = 0.25, FILL_PUSH_MAX = 50000.0;
// off in the earth world (--frame earth): it has no generated fill (World/Globe/Globe.cs)
uniform bool fill_push = true;
// lit as the style lights the world (World/Globe/Globe.cs keeps these the environment's): its
// ambient on the day side, the sun's colour; the night side dark
uniform vec3 ambient = vec3(0.40, 0.48, 0.74);
uniform vec3 sun_color = vec3(1.0);
// Cartoon: the photo's colours moved onto the world's own palette (Terrain/CoverPalette) by what
// they show, and the style's grade, so the Earth reads as the same world as the flat tiles
uniform bool cartoon = false;

void vertex() {
	vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	float d = max(length(view.xyz), 1.0);
	float fill = fill_push ? min(d * FILL_PUSH, FILL_PUSH_MAX) * (1.0 - world_fill_hide) : 0.0;
	view.xyz *= 1.0 + push + fill / d;
	POSITION = PROJECTION_MATRIX * view;
}

// Blue Marble (linear) brought to the world's palette (Terrain/CoverPalette): the photo is real albedo,
// far darker than the palette (forest 0.04 against 0.25), so it is lifted by a square root (forest 0.2,
// meadow 0.35, snow 0.9), a little more saturated, then given the style's grade
// (shaders/common/style.gdshaderinc). The photo's own coasts, forests and fields stay.
vec3 cartoon_earth(vec3 photo) {
	vec3 col = sqrt(max(photo, vec3(0.0)));
	float l = dot(col, vec3(0.2126, 0.7152, 0.0722));
	col = max(mix(vec3(l), col, 1.25), vec3(0.0));
	float g = max(col.g - max(col.r, col.b), 0.0);
	col.r += g * 0.45;
	col.b -= g * 0.15;
	l = dot(col, vec3(0.2126, 0.7152, 0.0722));
	col = mix(vec3(l), col, 0.92);
	return pow(max(col, vec3(0.0)), vec3(0.92)) * 0.95;
}

void fragment() {
	vec3 n = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	float ndl = dot(n, world_true_sun);
	// a soft terminator (twilight): the ambient and the sun on the day side, the night side all but black
	float day = smoothstep(-0.08, 0.25, ndl);
	vec3 col = texture(albedo_tex, uv_rect.xy + UV * uv_rect.zw).rgb;
	if (cartoon) col = cartoon_earth(col);
	ALBEDO = col * max(ambient * day + sun_color * max(ndl, 0.0), vec3(0.015));
}
