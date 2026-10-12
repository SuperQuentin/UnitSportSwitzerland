#ifndef MUSIC_GDSHADERINC
#define MUSIC_GDSHADERINC
// Music near the camera (#734, src/Items/BeatField): where the radio the camera hears most plays,
// how far its volume reaches, and its beat from the CD's analysis on the shared clock. Trees sway
// and lights pulse with it; nothing here touches physics. w = 0: no music.
global uniform vec4 world_music;       // xyz the radio (world space), w its reach in m
global uniform vec4 world_music_beat;  // kick 0..1, loudness 0..1, the bar's one 0..1, swing 0..1 over two beats

// How strongly the music reaches p, 0..1: full in the near third of its reach, gone at the edge
// (the same curve as RadioLoudness.Reach).
float music_reach(vec3 p) {
    float r = world_music.w;
    if (r <= 0.0) {
        return 0.0;
    }
    float d = distance(p.xz, world_music.xz);
    if (d >= r) {
        return 0.0;
    }
    float x = clamp((d - r * 0.33) / (r * 0.67), 0.0, 1.0);
    return (1.0 - x) * (1.0 - x);
}
#endif
