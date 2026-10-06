// The procedural pixel face (#657, docs/notes/avatar/procedural-faces.md), included by
// avatar.gdshaderinc and drawn on the face band (finish 11). Nothing is a texture: every pixel of
// the face's grid is tested against a few shapes (eyes, irises, lids, brows, mouth, nose, blush,
// freckles, stubble), so any of them can move.
//   - The face itself (Avatar.Face.FaceGenome) rides the band's second UV, two whole numbers below
//     2^24 (MeshScratch.FaceBand); the bit layout is FaceGenome.Code's: change both together.
//   - What it does (Avatar.Face.FaceState) comes in three instance uniforms, set by
//     Avatar.Face.FaceAnimator only when they change; a mesh with several figures in it (a bus, a
//     plane's cabin) leaves them at rest, and every face in it still blinks and glances by itself
//     on its genome's seed, so idle life costs no CPU and nothing on the network.
// Face space: 16 units across and down, the old atlas's pixels (x to the viewer's right, y down),
// sampled at the centres of the genome's grid (16..40 pixels), so edges stay pixel-hard in every
// style. Colours are the old atlas's, in linear.

instance uniform vec4 face_eyes = vec4(1.0, 1.0, 0.0, 0.0);  // open L, open R (times the blink), gaze x, y (-1..1)
instance uniform vec4 face_mouth = vec4(0.0);                  // smile -1..1, jaw 0..1, wide -1..1, brow -1..1
instance uniform vec4 face_mood = vec4(0.0);                   // blush 0..1, squint 0..1, special eyes, seed offset
// 0 freezes the blink and the glances (stills: the model viewer, the face probe)
uniform float face_idle = 1.0;

varying flat vec2 v_face;

const vec3 FACE_LINE = vec3(0.0100, 0.0061, 0.0100);    // sRGB 0.10 0.07 0.10
const vec3 FACE_WHITE = vec3(0.9310, 0.9131, 0.8796);   // 0.97 0.96 0.94
const vec3 FACE_MOUTH = vec3(0.2140, 0.0175, 0.0273);   // 0.50 0.14 0.18
const vec3 FACE_INSIDE = vec3(0.0742, 0.0080, 0.0161);  // 0.30 0.08 0.12
const vec3 FACE_TONGUE = vec3(0.6795, 0.1329, 0.1714);  // 0.85 0.40 0.45
const vec3 FACE_BLUSH = vec3(0.9131, 0.2747, 0.3434);   // 0.96 0.56 0.62
const vec3 FACE_NOSE = vec3(0.4851, 0.1845, 0.1329);    // 0.72 0.46 0.40
const vec3 FACE_FRECKLE = vec3(0.3916, 0.1470, 0.0742); // 0.66 0.42 0.30
const vec3 FACE_STUBBLE = vec3(0.1065, 0.0742, 0.0638); // 0.36 0.30 0.28
const vec3 FACE_HEART = vec3(0.7913, 0.0185, 0.0513);   // 0.90 0.15 0.25

// per EyeShape: half width, half height, tilt (+ the outer corner down), the upper lid's rest (0 open)
const vec4 FACE_EYES[8] = {
    vec4(1.45, 1.75, 0.0, 0.0),   // round
    vec4(1.85, 1.20, -0.10, 0.12),// almond
    vec4(1.70, 1.40, 0.30, 0.30), // droopy
    vec4(1.80, 1.15, -0.35, 0.15),// sharp
    vec4(0.95, 0.95, 0.0, 0.0),   // button: a dark dot
    vec4(1.55, 2.30, 0.0, 0.0),   // anime
    vec4(1.70, 1.45, 0.05, 0.50), // sleepy
    vec4(1.85, 1.95, 0.0, 0.0)    // wide
};
// per MouthShape: rest smile, rest jaw, width factor, teeth
const vec4 FACE_MOUTHS[8] = {
    vec4(0.0, 0.0, 1.0, 0.0),   // line
    vec4(0.6, 0.0, 0.9, 0.0),   // smile
    vec4(0.0, 0.0, 1.0, 0.0),   // cat
    vec4(0.8, 0.35, 1.1, 1.0),  // grin
    vec4(0.0, 0.15, 0.5, 0.0),  // oh
    vec4(0.3, 0.0, 1.0, 0.0),   // smirk
    vec4(-0.55, 0.0, 0.9, 0.0), // frown
    vec4(0.9, 0.55, 1.3, 1.0)   // beam
};

float face_hash(float n) {
    return fract(sin(n * 12.9898 + 4.1414) * 43758.5453);
}

float face_hash2(vec2 c) {
    return fract(sin(dot(c, vec2(12.9898, 78.233))) * 43758.5453);
}

// 1 open, 0 shut: a blink every 3-6 s at a random moment of its own period, now and then a double
float face_blink(float seed, float t) {
    float period = 3.0 + 2.8 * face_hash(seed * 17.0);
    float k = floor(t / period + seed);
    float local = (t / period + seed - k) * period;
    float at = 0.3 + (period - 1.0) * face_hash(k + seed * 31.0);
    float d = local - at;
    float open = clamp(abs(d - 0.07) / 0.07, 0.0, 1.0);
    if (face_hash(k * 1.7 + seed) < 0.2) {
        open = min(open, clamp(abs(d - 0.33) / 0.07, 0.0, 1.0));
    }
    return open;
}

// a glance: mostly ahead, now and then held somewhere else for a second or two
vec2 face_glance(float seed, float t) {
    float k = floor(t / 1.6 + seed * 3.0);
    if (face_hash(k + seed * 5.0) < 0.55) {
        return vec2(0.0);
    }
    return vec2(face_hash(k * 3.1 + seed) * 2.0 - 1.0, face_hash(k * 5.7 + seed) * 2.0 - 1.0) * vec2(0.6, 0.3);
}

// the heart's inside, p in -1..1 (y down)
bool face_heart(vec2 p) {
    p.y = -p.y * 1.1 + 0.25;
    p.x = abs(p.x);
    float a = p.y - sqrt(p.x) * 0.75;
    return p.x * p.x + a * a * 1.6 < 0.62;
}

// rgb, and alpha 0 where the face leaves the skin showing
vec4 face_px(vec2 uv, vec2 code, vec3 iris_col, float t) {
    // whole numbers up to 2^24: round, never + 0.5 (at 8 million a float steps by 1, so x + 0.5 rounds up a whole one)
    int lo = int(round(code.x));
    int hi = int(round(code.y));
    int shape = lo & 7;
    float size = 0.85 + 0.12 * float((lo >> 3) & 3);
    float spacing = 3.3 + 0.35 * float((lo >> 5) & 3);
    float eye_y = 6.9 + 0.45 * float((lo >> 7) & 3);
    float iris_k = 0.5 + 0.1 * float((lo >> 9) & 3);
    bool lashes = ((lo >> 11) & 1) == 1;
    bool shine = ((lo >> 12) & 1) == 1;
    int brow = (lo >> 13) & 3;
    float brow_w = ((lo >> 15) & 1) == 1 ? 2.0 : 1.0;
    int mouth = (lo >> 16) & 7;
    float mouth_w = 1.1 + 0.45 * float((lo >> 19) & 3);
    int nose = (lo >> 21) & 3;
    bool blush = ((lo >> 23) & 1) == 1;
    bool freckles = (hi & 1) == 1;
    bool stubble = ((hi >> 1) & 1) == 1;
    float res = 16.0 + 8.0 * float((hi >> 2) & 3);
    float seed = float((hi >> 4) & 255) / 255.0 + face_mood.w;
    float brow_h = 0.45 * float((hi >> 12) & 3);
    float mouth_y = 11.7 + 0.4 * float((hi >> 14) & 3);

    vec2 cell = floor(clamp(uv, 0.0, 0.9999) * res);
    vec2 p = (cell + 0.5) / res * 16.0;
    float px = 16.0 / res;
    float half_px = px * 0.5 + 0.001;

    // ---- eyes: mirrored onto the left half, each with its own lid and the gaze unmirrored
    bool right = p.x >= 8.0;
    float lx = 8.0 - abs(p.x - 8.0);
    vec2 ec = vec2(8.0 - spacing, eye_y);
    vec4 sh = FACE_EYES[shape];
    float rx = sh.x * size;
    float ry = sh.y * size;
    vec2 d = vec2(lx - ec.x, p.y - ec.y);
    vec2 e = vec2(d.x, d.y + sh.z * d.x);
    int special = int(face_mood.z + 0.5);
    if (special == 5) {
        special = right ? 0 : 4;   // a wink: the viewer's left eye shut happy, the other open
    }
    float idle = face_idle;
    float open = (right ? face_eyes.y : face_eyes.x) * mix(1.0, face_blink(seed, t), idle);
    open *= 1.0 - 0.45 * face_mood.y;
    open = clamp(open * (1.0 - sh.w), 0.0, 1.0);
    vec2 gaze = clamp(face_eyes.zw + idle * face_glance(seed, t), -1.0, 1.0);
    vec2 q = e / vec2(rx, ry);
    float r = length(q);
    float ring = px * 1.1 / ry;

    if (special == 1) {
        // crosses: knocked out
        if (max(abs(d.x), abs(d.y)) < ry * 0.85 && abs(abs(d.x) - abs(d.y)) < px * 0.75) {
            return vec4(FACE_LINE, 1.0);
        }
    } else if (special == 2) {
        if (max(abs(d.x), abs(d.y)) < ry * 1.1 && face_heart(d / (ry * 1.05))) {
            return vec4(FACE_HEART, 1.0);
        }
    } else if (special == 3) {
        // spirals: dizzy, turning
        float sr = length(d) / (ry * 1.05);
        float sa = atan(d.y, right ? -d.x : d.x) / 6.2832;
        if (sr < 1.0 && fract(sr * 2.6 - sa - t * 0.8) < 0.32) {
            return vec4(FACE_LINE, 1.0);
        }
    } else if (special == 4 || open < 0.16) {
        // shut: a line, arched up when happy, sagging when merely closed
        float f = 1.0 - clamp((e.x * e.x) / (rx * rx), 0.0, 1.0);
        float y = special == 4 ? ry * 0.15 - ry * 0.65 * f : ry * 0.15 + ry * 0.3 * f;
        if (abs(e.x) < rx && abs(e.y - y) <= half_px * 1.2) {
            return vec4(FACE_LINE, 1.0);
        }
        if (lashes && special != 4 && e.x < -rx * 0.75 && e.x > -rx - px * 1.2 && abs(e.y - y + px) <= half_px) {
            return vec4(FACE_LINE, 1.0);
        }
    } else {
        float lid = -ry + (1.0 - open) * ry * 1.9;
        if (r <= 1.0) {
            if (e.y < lid - half_px) {
                // above the lid: skin
            } else if (abs(e.y - lid) <= half_px * (lid > -ry * 0.9 ? 1.6 : 1.0) || (r > 1.0 - ring && e.y < ry * 0.25)) {
                return vec4(FACE_LINE, 1.0);   // the lid's edge and the upper outline
            } else if (shape == 4) {
                // a button eye: dark, its shine
                vec2 hl = vec2(-0.3 * rx, -0.35 * ry) + gaze * 0.2;
                if (shine && length(e - hl) <= px * 0.75) {
                    return vec4(1.0);
                }
                return vec4(FACE_LINE, 1.0);
            } else {
                float ir = min(rx, ry * 0.8) * iris_k;
                float side = right ? -1.0 : 1.0;   // +gaze.x is +lx for the left eye, -lx for the right
                vec2 ic = vec2(side * gaze.x * max(rx - ir * 0.8, 0.0), gaze.y * max(ry - ir, 0.0) * 0.6 + ry * 0.12);
                float dl = length(e - ic);
                if (dl <= ir) {
                    vec2 hl = ic + vec2(-0.38 * ir * side, -0.42 * ir);   // lit from the viewer's upper left
                    if (shine && length(e - hl) <= max(px * 0.75, ir * 0.22)) {
                        return vec4(1.0);
                    }
                    if (ir > px * 2.2 && dl <= ir * 0.4) {
                        return vec4(iris_col * 0.3, 1.0);   // the pupil, once the iris is big enough to hold one
                    }
                    // the iris in the figure's eye colour, a shade darker round its rim
                    return vec4(iris_col * (dl > ir - px ? 0.7 : 1.0), 1.0);
                }
                return vec4(FACE_WHITE, 1.0);
            }
        } else if (lashes && r < 1.0 + ring * 1.4 && e.x < -rx * 0.55 && e.y < -ry * 0.05 && e.y >= lid - px) {
            return vec4(FACE_LINE, 1.0);   // lashes off the outer corner
        }
    }

    // ---- brows: raised, knitted, tilted
    if (brow != 0) {
        float b = face_mouth.w;
        float raise = max(b, 0.0) * 1.1 - max(-b, 0.0) * 0.45;
        float tilt = (brow == 3 ? 0.32 : 0.0) + max(-b, 0.0) * 0.45 - max(b, 0.0) * 0.12;
        float bl = 1.55 * size + 0.2;
        float bx = ec.x + 0.15;
        float db = lx - bx;
        if (abs(db) < bl) {
            float curve = brow == 1 ? -0.5 * (1.0 - (db / bl) * (db / bl)) : 0.0;
            float by = ec.y - ry - 1.0 - brow_h - raise + curve + tilt * db;
            float half_w = brow_w * px * 0.5 + 0.001;
            if (p.y >= by - half_w && p.y < by + half_w) {
                return vec4(FACE_LINE, 1.0);
            }
        }
    }

    // ---- mouth: bent by the smile, opened by the jaw, widened or pursed
    vec4 ms = FACE_MOUTHS[mouth];
    float smile = clamp(face_mouth.x + ms.x, -1.0, 1.0);
    float jaw = clamp(face_mouth.y + ms.y, 0.0, 1.0);
    float wide = face_mouth.z;
    float dx = p.x - 8.0;
    vec3 lip = (mouth == 0 || mouth == 6) ? FACE_LINE : FACE_MOUTH;
    if (mouth == 4) {
        // an "oh": a ring, wider as the jaw drops
        vec2 o = vec2(dx, p.y - mouth_y - 0.3);
        float rr = (0.55 + 1.1 * jaw) * (1.0 + 0.25 * wide);
        float dl = length(o / vec2(1.0, 1.0 + 0.4 * jaw));
        if (abs(dl - rr) <= half_px) {
            return vec4(lip, 1.0);
        }
        if (dl < rr) {
            return vec4(FACE_INSIDE, 1.0);
        }
    } else {
        float w = mouth_w * ms.z * (1.0 + 0.35 * wide) * (1.0 - 0.25 * max(-wide, 0.0));
        if (abs(dx) <= w) {
            float f = 1.0 - (dx / w) * (dx / w);
            float yc = mouth_y + smile * 0.9 * (f - 0.5);
            if (mouth == 2) {
                yc = mouth_y + 0.55 * sin(abs(dx) / w * 3.1416) - 0.1;   // a cat's ω
            } else if (mouth == 5) {
                yc -= 0.6 * smile * dx / w;   // a smirk: one corner up
            }
            float top = yc - jaw * 0.45 * f;
            float bottom = yc + jaw * 2.6 * f * (1.0 - 0.3 * max(-wide, 0.0));
            if (bottom - top > px * 1.5) {
                if (abs(p.y - top) <= half_px || abs(p.y - bottom) <= half_px) {
                    return vec4(lip, 1.0);
                }
                if (p.y > top && p.y < bottom) {
                    if (ms.w > 0.5 && p.y < top + 0.75) {
                        return vec4(FACE_WHITE, 1.0);   // teeth
                    }
                    if (jaw > 0.35 && p.y > bottom - 0.9 && abs(dx) < w * 0.6) {
                        return vec4(FACE_TONGUE, 1.0);
                    }
                    return vec4(FACE_INSIDE, 1.0);
                }
            } else if (abs(p.y - yc) <= half_px) {
                return vec4(lip, 1.0);
            }
        }
    }

    // ---- nose
    if (nose == 1 && abs(p.x - 8.0) < px && abs(p.y - 10.2) <= half_px) {
        return vec4(FACE_NOSE, 1.0);
    } else if (nose == 2 && ((p.x >= 8.0 && p.x < 8.0 + px && p.y > 8.8 && p.y < 10.5)
            || (p.x >= 8.0 - px && p.x < 8.0 + px && abs(p.y - 10.4) <= half_px))) {
        return vec4(FACE_NOSE, 1.0);
    } else if (nose == 3 && abs(abs(p.x - 8.0) - px * 1.5) < half_px && abs(p.y - 10.3) <= half_px) {
        return vec4(FACE_NOSE, 1.0);
    }

    // ---- cheeks: blush (dithered when light), freckles, stubble
    float flush = (blush ? 0.55 : 0.0) + face_mood.x;
    if (flush > 0.01) {
        vec2 bc = vec2(lx - ec.x + 0.3, p.y - (ec.y + ry + 1.1));
        if (length(bc / vec2(1.5, 0.6)) < 1.0 && (flush > 0.75 || mod(cell.x + cell.y, 2.0) < 0.5)) {
            return vec4(FACE_BLUSH, 1.0);
        }
    }
    if (freckles && abs(lx - ec.x) < 1.6 && p.y > ec.y + ry + 0.4 && p.y < ec.y + ry + 2.2
            && face_hash2(cell) < 0.16) {
        return vec4(FACE_FRECKLE, 1.0);
    }
    if (stubble && p.y > mouth_y - 1.1 && lx > 2.6 && mod(cell.x + cell.y, 2.0) < 0.5 && face_hash2(cell) > 0.55) {
        return vec4(FACE_STUBBLE, 1.0);
    }
    return vec4(0.0);
}
