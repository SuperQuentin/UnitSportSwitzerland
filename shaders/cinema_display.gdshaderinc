// A hall's projector display (#1298), shared by the screen (cinema_screen), the beam (projector_beam) and the picture in
// the port (cinema_port): where a point of the screen (0..1 across, 0..1 down) samples the picture, or nothing (black,
// no light) outside it. The picture fitted into a frame of the chosen ratio, centred at the screen's full height:
// contained (black bars), covering (cropped) or stretched. `content` is the picture's measured rectangle in the decoded
// frame. CinemaVideo.MapScreen is the same in C#, for the tests. The defaults are the slides': the whole frame.

// the picture's rectangle in the frame (x0, y0, width, height, 0..1) and its shape (width over height)
uniform vec4 content = vec4(0.0, 0.0, 1.0, 1.0);
uniform float picture_ratio = 2.39;
// the frame's share of the screen's width, its shape, and the fit: 0 contain, 1 cover, 2 stretch
uniform float frame = 1.0;
uniform float frame_ratio = 2.39;
uniform int fit = 0;

// false outside what the screen shows; else `at` is where the picture is sampled, in the frame's 0..1
bool display_at(vec2 screen, out vec2 at) {
	float x0 = 0.5 - frame * 0.5;
	vec2 st = vec2((screen.x - x0) / frame, screen.y);
	bool shown = screen.x >= x0 && screen.x <= 1.0 - x0;
	vec2 c = st;
	bool wider = picture_ratio > frame_ratio;
	if (fit == 0) {
		if (wider) {
			float h = frame_ratio / picture_ratio;
			shown = shown && abs(st.y - 0.5) <= h * 0.5;
			c = vec2(st.x, (st.y - 0.5) / h + 0.5);
		} else {
			float w = picture_ratio / frame_ratio;
			shown = shown && abs(st.x - 0.5) <= w * 0.5;
			c = vec2((st.x - 0.5) / w + 0.5, st.y);
		}
	} else if (fit == 1) {
		c = wider ? vec2((st.x - 0.5) * frame_ratio / picture_ratio + 0.5, st.y)
			: vec2(st.x, (st.y - 0.5) * picture_ratio / frame_ratio + 0.5);
	}
	at = content.xy + c * content.zw;
	return shown;
}
