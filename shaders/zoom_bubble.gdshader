shader_type canvas_item;

// The zoom bubble's inset: the close-up camera's picture cut to a disc, with a soft inner
// shadow round the edge so it reads like looking through a rounded window, and a gentle
// saturation/warmth lift for the toon look. The outline, accent ring and tail are drawn
// underneath by ZoomBubble itself (they have to merge into one shape with the tail).

void fragment() {
	float r = length(UV - vec2(0.5)) * 2.0;
	float edge = fwidth(r);
	vec3 c = COLOR.rgb;

	// punchier, slightly warm colour
	float grey = dot(c, vec3(0.299, 0.587, 0.114));
	c = mix(vec3(grey), c, 1.25);
	c *= vec3(1.04, 1.0, 0.95);

	// inner shadow hugging the rim
	c *= mix(1.0, 0.72, smoothstep(0.78, 1.0, r));

	COLOR.rgb = clamp(c, 0.0, 1.0);
	COLOR.a *= 1.0 - smoothstep(1.0 - edge, 1.0, r);
}
