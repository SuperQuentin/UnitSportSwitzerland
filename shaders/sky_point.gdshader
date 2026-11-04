shader_type spatial;

// Points of light in the sky (#1444): the stars (src/World/StarField.cs, one mesh built once, each
// star a quad whose VERTEX is its catalogue direction) and the planets seen by eye
// (src/World/SkyBodies.cs, one quad each, sky = their direction). A fixed size in pixels whatever
// the zoom, drawn just inside the far plane, so the ridges hide them. Fade in with the night and
// out towards the horizon.
render_mode unshaded, fog_disabled, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

#include "res://shaders/common/sky_depth.gdshaderinc"

global uniform float world_night;
// 0 in the air, 1 above it (#1430 H4, World/Globe/Atmosphere): no sky to hide the stars by day, and
// the horizon far below the horizontal (the Earth hides what is behind it, by depth)
global uniform float world_space;

// world axes: the celestial sphere's (stars), or a body's direction in its first column (a planet)
uniform mat3 sky = mat3(1.0);
uniform float distance = 1000.0;
// how much daylight lets through: 0 for the stars, Venus' brilliance by day
uniform float daylight = 0.0;
// a planet's point: its size (px) and colour from the uniforms, not the vertex
uniform bool single = false;
// satellites (src/World/Satellites.cs): a MultiMesh whose instances stand at their directions (unit
// vectors from the camera), their colour the instance's, their size its custom x
uniform bool instanced = false;
uniform vec4 point_color : source_color = vec4(1.0);
uniform float point_size = 2.0;
// every point this many times its size: a debugging aid (--shuttlefilm drive,bigstars: what crops the stars?)
uniform float size_scale = 1.0;

varying vec4 tint;

void vertex() {
	vec3 dir = instanced ? normalize(MODEL_MATRIX[3].xyz) : single ? normalize(sky[0]) : normalize(sky * VERTEX);
	vec3 cam = INV_VIEW_MATRIX[3].xyz;
	vec4 clip = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(cam + dir * distance, 1.0));
	vec2 corner = UV * 2.0 - 1.0;
	float px = (instanced ? INSTANCE_CUSTOM.x : single ? point_size : UV2.x) * size_scale;
	clip.xy += corner * px * 2.0 / VIEWPORT_SIZE * clip.w;
	POSITION = sky_depth(clip);
	float horizon = mix(smoothstep(-0.01, 0.1, dir.y), 1.0, world_space);
	float dark = max(max(smoothstep(0.35, 0.95, world_night), daylight), world_space);
	vec4 c = single ? point_color : COLOR;
	// a satellite shows only while it is sunlit over a dark sky: the node hides the rest
	if (instanced) dark = 1.0;
	tint = vec4(c.rgb, c.a * horizon * dark);
}

void fragment() {
	float r = length(UV * 2.0 - 1.0);
	ALBEDO = tint.rgb * tint.a * (1.0 - smoothstep(0.35, 1.0, r));
}
