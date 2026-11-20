// The globe's geomorphing (#1656, World/Globe/Globe.cs Build): each vertex of a globe tile carries in CUSTOM0.xyz the
// offset to where its parent level draws that point, and in CUSTOM0.w its level's drawn cell, metres (0: no morph). A
// tile splits nearer than cell * world_globe_morph.x (focal over the split pixels, Globe.Select's rule), so its parent
// splits nearer than twice that: a vertex eases into the parent's shape over the last part of that range and is the
// parent's at its end. The split and the merge then change nothing on screen. Within world_globe_morph.y of the camera
// (the far leaves' band, where tiles split for the leaves, not for their size) nothing morphs, eased over 30 % more.
global uniform vec2 world_globe_morph;
// a split's child that came after the split was due (it was loading or streaming) starts as its parent drew that ground
// and grows into its own shape: 1 when it replaces its parent, 0 0.4 s later (World/Globe/Globe.cs, Young)
instance uniform float globe_young = 0.0;

vec3 globe_geomorph(vec3 v, vec4 custom, mat4 modelview) {
	if (custom.w <= 0.0 || world_globe_morph.x <= 0.0) return v;
	float d = length((modelview * vec4(v, 1.0)).xyz);
	float split = custom.w * world_globe_morph.x;
	float k = smoothstep(1.3 * split, 1.9 * split, d);
	if (world_globe_morph.y > 0.0) k *= smoothstep(world_globe_morph.y, world_globe_morph.y * 1.3, d);
	return v + custom.xyz * max(k, globe_young);
}
