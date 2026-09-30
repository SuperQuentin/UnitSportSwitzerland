# Vertex colours are raw linear; shader uniforms marked `: source_color` are not

- **Vertex colours are raw linear; shader uniforms marked `: source_color` are not.**
  Godot converts sRGB→linear for `source_color` uniforms automatically but never for
  baked vertex colours, so authored colours must go through `Color.SrgbToLinear()` in C#
  or dark tones render washed out (asphalt 0.30 displayed as 0.58 grey).
