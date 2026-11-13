// The globe from far out (#1430 H4, World/Globe/Globe.cs): the coarse cube tiles in real geometry,
// so no curve_offset (the flat tiles are bent onto a sphere; these already are one). Drawn a little
// behind whatever stands at the same place, so the flat world's tiles and horizon blocks win where
// they are, and the globe shows round and past them. Hazed as they are: the style's haze is all but
// gone by the time the flat world gives way (Styles/CameraGlobal.HazeAt), and an unhazed globe
// switching on at 15 km beside the hazed fill was a jump in the climb.
shader_type spatial;
render_mode unshaded, cull_back;

// the true sun, the style's ambient and sun colour, and the 20 km handover's band (#1802)
#include "res://shaders/common/globe_light.gdshaderinc"
// facet_normal, style_lake: what the terrain body draws with at the handover
#include "res://shaders/common/style.gdshaderinc"

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

// Cartoon's grade (shaders/common/style.gdshaderinc's style_grade, which only a Cartoon wrapper defines):
// greens toward yellow, a touch lighter and less saturated
vec3 cartoon_grade(vec3 col) {
	float g = max(col.g - max(col.r, col.b), 0.0);
	col.r += g * 0.45;
	col.b -= g * 0.15;
	float l = dot(col, vec3(0.2126, 0.7152, 0.0722));
	col = mix(vec3(l), col, 0.92);
	return pow(max(col, vec3(0.0)), vec3(0.92)) * 0.95;
}

// Blue Marble (linear) brought to the world's palette (Terrain/CoverPalette): the photo is real albedo,
// far darker than the palette (forest 0.04 against 0.25), so it is lifted by a square root (forest 0.2,
// meadow 0.35, snow 0.9), a little more saturated, then given the style's grade. The photo's own coasts,
// forests and fields stay.
vec3 cartoon_earth(vec3 photo) {
	vec3 col = sqrt(max(photo, vec3(0.0)));
	float l = dot(col, vec3(0.2126, 0.7152, 0.0722));
	col = max(mix(vec3(l), col, 1.25), vec3(0.0));
	return cartoon_grade(col);
}

void fragment() {
	vec3 n = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	vec3 col = texture(albedo_tex, uv_rect.xy + UV * uv_rect.zw).rgb;
	if (cartoon) col = cartoon_earth(col);
	// just over the handover (20..25 km, #1802): what the terrain body drew at 20 km, its palette per vertex
	// (GlobeGround) with its lakes, bare rock and grade, on its facets, eased into the photo and smooth relief
	float p = world_globe_palette;
	if (p > 0.0) {
		vec3 f = facet_normal(VERTEX, INV_VIEW_MATRIX);
		if (dot(f, n) < 0.0) f = -f;
		// steep ground by the smooth normal (#883), as the terrain body
		f = normalize(mix(f, n, smoothstep(0.8, 0.55, n.y)));
		vec3 ground = style_lake(COLOR.rgb, length(VERTEX));
		ground = mix(ground, vec3(0.30, 0.28, 0.26), smoothstep(0.62, 0.38, f.y) * 0.75);
		if (cartoon) ground = cartoon_grade(ground);
		col = mix(col, ground, p);
		n = normalize(mix(n, f, p));
	}
	ALBEDO = globe_lit(col, n);
}
