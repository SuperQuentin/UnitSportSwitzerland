#!/usr/bin/env python3
"""Judge rendered sounds by their numbers, for when nobody can listen (#380, docs/notes/audio/audio.md).

    <godot> --headless --path . -- --soundcheck test_output/sound --water-sounds
    python tools/soundstats.py test_output/sound [test_output/sound/png] [prefix]

Needs numpy, scipy and matplotlib (a scratch venv: python -m venv v && v/Scripts/pip install numpy scipy matplotlib).
For every <prefix>*.wav (default water_): peak, RMS, crest factor, clipped samples, DC, spectral centroid,
energy per band, the biggest sample step against the usual ones (a click shows as >> 3), and for
tonal or rhythmic sounds the spectral peaks and the envelope's beat rate and depth (10th to 90th
percentile of a 20 ms RMS envelope over 3-5 s and 15-17 s). With an output folder: a waveform and
spectrogram PNG per sound family.
"""
import glob
import os
import sys
import wave

import numpy as np
from scipy import signal

BANDS = [0, 100, 300, 1000, 3000, 6000]


def load(path):
    with wave.open(path) as w:
        rate = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float64) / 32767.0
    return rate, x


def spectrum(rate, x):
    f, p = signal.welch(x, rate, nperseg=4096)
    total = p.sum() + 1e-20
    edges = BANDS + [rate / 2]
    shares = [100 * p[(f >= a) & (f < b)].sum() / total for a, b in zip(edges[:-1], edges[1:])]
    return f, p, shares, (f * p).sum() / total


def peaks(f, p, n=6):
    db = 10 * np.log10(p + 1e-20)
    idx, _ = signal.find_peaks(db, prominence=10)
    idx = idx[np.argsort(p[idx])[::-1][:n]]
    return sorted((int(round(f[i])), round(float(db[i] - db.max()), 1)) for i in idx)


def beat(rate, x, lo=0.5, hi=20.0):
    env = np.abs(signal.hilbert(x))
    env = signal.resample_poly(env, 1, rate // 200)
    env -= env.mean()
    ac = np.correlate(env, env, "full")[len(env) - 1:]
    lags = np.arange(len(ac)) / 200.0
    m = (lags > 1 / hi) & (lags < 1 / lo)
    if not m.any() or ac[0] <= 0:
        return None
    i = int(np.argmax(ac[m]))
    return round(1 / lags[m][i], 2), round(float(ac[m][i] / ac[0]), 2)


def depth(rate, x, a, b):
    seg = x[int(a * rate):int(b * rate)]
    if len(seg) < rate // 10:
        return None
    n = int(0.02 * rate)
    env = np.sqrt(signal.convolve(seg ** 2, np.ones(n) / n, "same"))
    lo, hi = np.percentile(env, 10), np.percentile(env, 90)
    return round(20 * np.log10(hi / (lo + 1e-9)), 1)


def main():
    src = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else None
    prefix = sys.argv[3] if len(sys.argv) > 3 else "water_"
    files = sorted(glob.glob(os.path.join(src, prefix + "*.wav")))
    if not files:
        sys.exit(f"no {prefix}*.wav in {src}")
    print("bands % = " + " / ".join(f"{a}-" for a in BANDS) + " Hz; maxjump: biggest sample step over the 99.9th percentile one")
    families = {}
    for path in files:
        name = os.path.basename(path)[len(prefix):-4]
        rate, x = load(path)
        pk, rms = np.max(np.abs(x)), np.sqrt(np.mean(x ** 2))
        f, p, shares, centroid = spectrum(rate, x)
        d = np.abs(np.diff(x))
        print(f"{name:26s} {len(x) / rate:5.2f}s peak {pk:.2f} rms {20 * np.log10(rms + 1e-9):6.1f} dBFS "
              f"crest {20 * np.log10(pk / (rms + 1e-9)):4.1f} dB clip {int(np.sum(np.abs(x) >= 0.999))} dc {x.mean():+.4f} "
              f"centroid {centroid:6.0f} Hz bands% " + "/".join(f"{v:.0f}" for v in shares)
              + f" maxjump {d.max() / (np.percentile(d, 99.9) + 1e-9):.1f}")
        if any(k in name for k in ("whistle", "steam", "hull", "wade", "paddles")):
            print(f"{'':26s} peaks (Hz, dB) {peaks(f, p)}  beat (Hz, corr) {beat(rate, x)}"
                  f"  depth 3-5 s {depth(rate, x, 3, 5)} dB, 15-17 s {depth(rate, x, 15, 17)} dB")
        family = name.rstrip("0123456789").rstrip("_") if not name.startswith("steam") else "steam_engine"
        families.setdefault(family, []).append((name, rate, x))
    if out:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        os.makedirs(out, exist_ok=True)
        for family, rows in families.items():
            rows = rows[:6]
            fig, ax = plt.subplots(len(rows), 2, figsize=(14, 2.3 * len(rows)), squeeze=False)
            for k, (name, rate, x) in enumerate(rows):
                t = np.arange(len(x)) / rate
                ax[k][0].plot(t, x, lw=0.4, color="#1f5f8b")
                ax[k][0].set_ylim(-1, 1)
                ax[k][0].axhline(0.999, color="r", lw=0.5)
                ax[k][0].axhline(-0.999, color="r", lw=0.5)
                ax[k][0].set_title(name, fontsize=8)
                fs, ts, s = signal.spectrogram(x, rate, nperseg=1024, noverlap=768)
                sd = 10 * np.log10(s + 1e-14)
                ax[k][1].pcolormesh(ts, fs, sd, vmin=sd.max() - 75, vmax=sd.max(), shading="auto", cmap="magma")
                ax[k][1].set_ylim(0, 6000)
                ax[k][1].set_title(name + " (spectrogram, 0-6 kHz, 75 dB)", fontsize=8)
            fig.tight_layout()
            fig.savefig(os.path.join(out, f"{prefix}{family}.png"), dpi=80)
            plt.close(fig)


if __name__ == "__main__":
    main()
