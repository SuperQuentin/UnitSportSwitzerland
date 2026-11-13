// The globe's light and its 20 km handover (#1802, World/Globe/Globe.cs). Under --frame earth the globe's
// tiles take the style's terrain material below 20 km and globe.gdshader above it. Across the switch both
// draw the same picture, and each eases into its own look away from it:
//   15..20 km  the terrain body moves from the style's light (at night the moon and the night tint) to the
//              globe's: the true sun's terminator (world_globe_band 0..1)
//   20..25 km  globe.gdshader moves from the terrain's colours and facets (the palette per vertex) to the
//              photo and smooth relief (world_globe_palette 1..0)
// Both 0 outside the earth world, and below 15 km: the flat world and the ground near the player are untouched.
// Written by the globe on change only.
#ifndef COMMON_GLOBE_LIGHT
#define COMMON_GLOBE_LIGHT

// toward the real sun, world axes (World/Globe/Atmosphere): the scene's light is the moon at night,
// and one light cannot put the terminator across the whole Earth anyway
global uniform vec3 world_true_sun;
// the style's ambient (colour by energy) and its sun's colour by energy, kept by the globe (Relight)
global uniform vec3 world_globe_ambient;
global uniform vec3 world_globe_sun;
global uniform float world_globe_band;
global uniform float world_globe_palette;

// A colour lit as the globe lights the Earth: a soft terminator (twilight), the ambient and the sun on the
// day side, the night side all but black. n is a world-space normal.
vec3 globe_lit(vec3 col, vec3 n) {
	float ndl = dot(n, world_true_sun);
	float day = smoothstep(-0.08, 0.25, ndl);
	return col * max(world_globe_ambient * day + world_globe_sun * max(ndl, 0.0), vec3(0.015));
}
#endif
