// A cinema screen lit by its projector (#1250, InteriorNode.Cinema at High detail; first the POC,
// Interiors/CinemaProjectionPoc): a surface lit only by positional lights, the projector a spot light whose
// projector texture is the picture. No ambient, no sun: what the beam does not reach
// stays dark, so a figure in it wears the picture and leaves its shadow on the screen.
shader_type spatial;
render_mode ambient_light_disabled, specular_disabled;

uniform vec3 albedo : source_color = vec3(0.9);
// a faint floor of light, so the room's shapes read where the beam is not
uniform float dim = 0.03;

void fragment() {
	ALBEDO = albedo;
	EMISSION = albedo * dim;
}

void light() {
	if (!LIGHT_IS_DIRECTIONAL) {
		DIFFUSE_LIGHT += clamp(dot(NORMAL, LIGHT), 0.0, 1.0) * ATTENUATION * LIGHT_COLOR / PI;
	}
}
