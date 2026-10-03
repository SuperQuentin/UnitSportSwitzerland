"""Writes src/Player/MotorbikeCatalog.Brands.cs from the motorbike datasets in docs/data (#410).

    python tools/motorbikes/gen_catalog.py

Every published number comes from a dataset object (one per variant, with a source URL per field);
everything else is an assumption, written into the entry's "Assumed:" comment. APPEND-ONLY: a bike's
RideKind is its position in the catalog, so new datasets go at the end of DATASETS and new variants
at the end of their file; never reorder or drop one.
"""
import json
import math
import os
import re

ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), '..', '..'))
DATASETS = [
    'docs/data/yamaha_fazer_r3_tmax_specs.json',
    'docs/data/yamaha_tenere_tracer_kawasaki_versys_specs.json',
    'docs/data/ktm_honda_cb500_specs.json',
]
OUT = 'src/Player/MotorbikeCatalog.Brands.cs'
# the hand-written entries before these (R1, Monster, 28 Africa Twins)
FIRST_INDEX = 30

# ---- assumptions per kind of bike: none of these is published -------------------------------
STYLE = {
    #            CG    rear  CdA   grip  lean brake fork  seatZ  grip (x, dy, z)      peg (x, y, z)        offroad
    'Sport':    (0.60, 0.49, 0.36, 1.10, 50, 9.8, 0.62, -0.24, (0.32, -0.02, 0.36), (0.17, 0.36, -0.18), 0.0),
    'Naked':    (0.64, 0.50, 0.42, 1.12, 50, 9.8, 0.64, -0.22, (0.38, 0.20, 0.30), (0.17, 0.36, -0.12), 0.0),
    'SportTouring': (0.66, 0.50, 0.46, 1.10, 48, 9.5, 0.68, -0.22, (0.38, 0.22, 0.28), (0.18, 0.37, -0.10), 0.0),
    'Adventure': (0.74, 0.50, 0.55, 1.00, 45, 9.0, 0.84, -0.20, (0.43, 0.30, 0.30), (0.19, 0.42, -0.05), 0.3),
    'Supermoto': (0.70, 0.48, 0.48, 1.10, 50, 9.5, 0.82, -0.15, (0.42, 0.20, 0.32), (0.17, 0.42, -0.05), 0.15),
    'Scooter':  (0.58, 0.55, 0.55, 1.00, 43, 8.5, 0.55, -0.25, (0.36, 0.25, 0.32), (0.17, 0.30, 0.22), 0.0),
}
SOUND_SPREAD = {'Inline4Bike': 1500, 'Triple': 1000, 'Single': 800, 'ParallelTwin360': 500}
IDLE = {'Inline4Bike': 1300, 'Triple': 1250, 'Single': 1500}


def kind_of(e):
    """(MotoStyle, EngineLayout, MotoEngineShape, V angle, spoked) for a dataset object."""
    i, fam, name = e['id'].upper(), e.get('family', ''), e['model_name'].upper()
    if fam == 'TMAX':
        return 'Scooter', 'ParallelTwin360', 'ParallelTwin', 90, False
    if fam == 'Fazer':
        return 'SportTouring', 'Inline4Bike', 'InlineFour', 90, False
    if fam == 'YZF-R3':
        return 'Sport', 'ParallelTwin180', 'ParallelTwin', 90, False
    if 'Ténéré' in fam or 'TENERE' in i or 'XT1200' in i:
        return 'Adventure', 'ParallelTwin270', 'ParallelTwin', 90, True
    if fam == 'Tracer':
        if '700' in i or 'TRACER7' in i:
            return 'SportTouring', 'ParallelTwin270', 'ParallelTwin', 90, False
        return 'SportTouring', 'Triple', 'Triple', 90, False
    if fam == 'Versys 650':
        return 'SportTouring', 'ParallelTwin180', 'ParallelTwin', 90, False
    if 'SMC' in i or 'SMC' in fam:
        return 'Supermoto', 'Single', 'Single', 90, True
    if fam == 'Duke':
        if '1290' in i or '1290' in name:
            return 'Naked', 'VTwin75', 'VTwin', 75, False
        if any(n in i or n in name for n in ('790', '890')):
            return 'Naked', 'VTwin75', 'ParallelTwin', 90, False
        return 'Naked', 'Single', 'Single', 90, False
    if fam == 'CB500':
        if 'X' in i.replace('CB500', ' ').split('-')[0] or 'CB500X' in name:
            return 'Adventure', 'ParallelTwin180', 'ParallelTwin', 90, False
        return 'Naked', 'ParallelTwin180', 'ParallelTwin', 90, False
    raise ValueError(f"no style rule for {e['id']} ({fam})")


# ---- gaps the datasets leave, filled by hand: each one is named in the entry's comment ----------
FILL = {
    'FZS1000-2001': dict(power_ps=143, power_rpm=10000, torque_nm=106, torque_rpm=7500,
                         why='143 PS at 10,000 and 106 N·m at 7,500, the figures usually quoted for the FZS1000 (not in the dataset)'),
}
# a box borrowed from a sibling when a variant's own was not found
GEARS_FROM = {
    'FZS600-1998': 'FZ6-2004', 'FZS600-2002': 'FZ6-2004', 'FZS1000-2001': 'FZ6-2004', 'FZ1-FAZER-2006': 'FZ6-2004',
    'KLE650-2007': 'KLE650-2015', 'KLE650-2010': 'KLE650-2015',
    'CB500HORNET-2024': 'CB500F-2019',
}
# single fields a variant's object leaves null: (value, why)
BORROW = {
    'KLE650-2010': {'kerb_weight_kg': (206, 'kerb 206 kg as the 2007-09 Versys (same frame and engine)')},
    'TMAX530-SX-DX-2017': {'wheelbase_mm': (1580, 'wheelbase 1,580 mm as the 2012 TMAX 530 (same frame)')},
    'TRACER900-2018': {'kerb_weight_kg': (210, 'kerb 210 kg as the MT-09 Tracer it replaced')},
    'TRACER9-2021': {'kerb_weight_kg': (213, "kerb 213 kg: the GT's 220 less its cases and semi-active parts (estimate)")},
}
PRIMARY = {'TRACER700-2016': (1.925, 'primary 77/40 = 1.925 as the MT-07 (same CP2 engine; not in the dataset)'),
           'TRACER7-2020': (1.925, 'primary 77/40 = 1.925 as the MT-07 (same CP2 engine; not in the dataset)')}


def num(v):
    return None if v in (None, '', []) else v


def ps_of(e):
    f = FILL.get(e['id'], {})
    if 'power_ps' in f:
        return f['power_ps']
    if num(e.get('power_ps')):
        return float(e['power_ps'])
    if num(e.get('power_hp')):
        return float(e['power_hp']) * 1.0139
    if num(e.get('power_kw')):
        return float(e['power_kw']) * 1.35962
    return None


def tyre(t):
    """"120/70 ZR17 (58W)", "110/70-17 M/C 54H", "110/80R19M/C 59V" -> "120/70ZR17", "110/70-17", "110/80R19"."""
    if not t:
        return None
    m = re.search(r'(\d{2,3})\s*/\s*(\d{2,3})\s*(ZR|R|-|\s)\s*(\d{2})', t.upper())
    if not m:
        return None
    c = m.group(3).strip() or '-'
    return f'{m.group(1)}/{m.group(2)}{c}{m.group(4)}'


def tyre_radius(t):
    m = re.match(r'(\d+)/(\d+)\D+(\d+)', t)
    w, ar, rim = map(float, m.groups())
    return (rim * 25.4 / 2 + w * ar / 100) / 1000


COLOURS = [  # first word found in the livery wins: (word, paint, trim)
    ('orange', (0.95, 0.42, 0.02), (0.08, 0.08, 0.09)), ('lime', (0.42, 0.8, 0.1), (0.08, 0.08, 0.09)),
    ('green', (0.3, 0.72, 0.12), (0.08, 0.08, 0.09)), ('red', (0.78, 0.06, 0.07), (0.9, 0.9, 0.92)),
    ('blue', (0.08, 0.22, 0.7), (0.9, 0.9, 0.92)), ('yellow', (0.95, 0.78, 0.1), (0.08, 0.08, 0.09)),
    ('white', (0.92, 0.92, 0.9), (0.08, 0.17, 0.55)), ('silver', (0.66, 0.67, 0.7), (0.1, 0.1, 0.11)),
    ('grey', (0.4, 0.41, 0.44), (0.1, 0.1, 0.11)), ('gray', (0.4, 0.41, 0.44), (0.1, 0.1, 0.11)),
    ('black', (0.08, 0.08, 0.09), (0.55, 0.56, 0.6)),
]


def livery(e):
    text = (e.get('livery_main_colour') or '').lower()
    best = None
    for word, paint, trim in COLOURS:
        at = text.find(word)
        if at >= 0 and (best is None or at < best[0]):
            best = (at, paint, trim)
    paint, trim = (best[1], best[2]) if best else ((0.08, 0.22, 0.7), (0.9, 0.9, 0.92))
    frame = (0.95, 0.42, 0.02) if 'orange frame' in text else (0.66, 0.67, 0.7) if 'silver frame' in text or 'aluminium' in text else (0.1, 0.1, 0.11)
    wheel = (0.95, 0.42, 0.02) if 'orange wheel' in text or 'orange rim' in text else (0.08, 0.22, 0.7) if 'blue wheel' in text or 'blue rim' in text \
        else (0.78, 0.6, 0.2) if 'gold' in text else (0.1, 0.1, 0.11)
    return paint, trim, frame, wheel


def f(x):
    s = f'{x:.4f}'.rstrip('0').rstrip('.')
    return (s if '.' in s else s) + 'f'


def col(c):
    return f'new Color({f(c[0])}, {f(c[1])}, {f(c[2])})'


def torque_curve(idle, t_nm, t_rpm, p_kw, p_rpm, redline):
    """Peak torque and peak power are published; the shape between and around them is assumed."""
    t_p = p_kw * 1000 / (p_rpm * math.tau / 60)
    pts = [(idle, 0.55 * t_nm), (max(idle + 500, 0.5 * t_rpm), 0.82 * t_nm), (t_rpm, t_nm)]
    if p_rpm > t_rpm + 400:
        pts.append(((t_rpm + p_rpm) / 2, min(t_nm, 0.5 * (t_nm + t_p) * 1.02)))
    pts.append((max(p_rpm, t_rpm + 200), min(t_p, t_nm)))
    pts.append((redline, 0.85 * min(t_p, t_nm)))
    out = []
    for r, n in sorted(pts):
        if not out or r > out[-1][0] + 1:
            out.append((round(r), round(n, 1)))
    return out


def label(e):
    years = re.sub(r'\s*\(.*', '', str(e.get('years') or '')).replace('present', '').replace('–', '-').strip()
    years = re.sub(r'^(\d{4})-(\d{2})(\d{2})$', r'\1-\3', years)          # 2010-2013 -> 2010-13
    years = re.sub(r'(\d{4})-$', r'\1+', years)
    name = e['model_name']
    if not name.lower().startswith(e['brand'].lower()):
        name = f"{e['brand']} {name}"
    v = e.get('variant') or ''
    return f'{name} ({years})' if years else name


def entry(e, index):
    fill = FILL.get(e['id'], {})
    assumed, notes = [], []
    for field, (value, why) in BORROW.get(e['id'], {}).items():
        if not num(e.get(field)):
            e[field] = value
            assumed.append(why)
    style, layout, shape, vang, spoked = kind_of(e)
    cg, rear, cda, grip, lean, brake, fork, seat_z, grip_off, peg, offroad = STYLE[style]
    if e.get('family') == 'Tracer' and kind_of(e)[1] == 'Triple':
        cda = 0.59
        assumed.append("CdA 0.59 m² for the Tracer 900 / 9, fitted to the Tracer 9 GT's measured 214 km/h (the generic 0.46 gave 233)")

    ps = ps_of(e)
    p_rpm = fill.get('power_rpm') or num(e.get('power_rpm'))
    t_nm = fill.get('torque_nm') or num(e.get('torque_nm'))
    t_rpm = fill.get('torque_rpm') or num(e.get('torque_rpm'))
    if 'why' in fill:
        assumed.append(fill['why'])
    if not (ps and p_rpm and t_nm and t_rpm):
        raise ValueError(f"{e['id']}: power/torque incomplete")
    p_kw = ps * 0.7355

    redline = num(e.get('redline_rpm')) or num(e.get('rev_limiter_rpm'))
    if not redline:
        redline = p_rpm + SOUND_SPREAD.get(layout, 800)
        assumed.append(f'redline {redline:,.0f}')
    idle = IDLE.get(layout, 1200)
    launch = round(max(idle + 1500, 0.55 * p_rpm), -2)

    mass = num(e.get('kerb_weight_kg'))
    if not mass and num(e.get('dry_weight_kg')):
        fuel = (num(e.get('fuel_capacity_l')) or 15) * 0.74
        mass = round(e['dry_weight_kg'] + fuel + 4)
        assumed.append(f'kerb {mass:.0f} kg = dry {e["dry_weight_kg"]} + a full tank ({fuel:.0f} kg) + 4 kg of other fluids')
    if not mass:
        raise ValueError(f"{e['id']}: no mass")

    ft, rt = tyre(e.get('front_tyre')), tyre(e.get('rear_tyre'))
    if not (ft and rt):
        raise ValueError(f"{e['id']}: tyres {e.get('front_tyre')} / {e.get('rear_tyre')}")
    wheelbase = num(e.get('wheelbase_mm'))
    if not wheelbase:
        raise ValueError(f"{e['id']}: no wheelbase")
    rake = num(e.get('rake_deg')) or 25.0
    trail = num(e.get('trail_mm')) or 100.0
    if not num(e.get('rake_deg')) or not num(e.get('trail_mm')):
        assumed.append('rake / trail')

    cvt = e.get('cvt') or None
    gears = num(e.get('gear_ratios'))
    primary, final = num(e.get('primary_reduction')), num(e.get('final_reduction'))
    if cvt:
        gears = [cvt['low_ratio']]
        secondary = cvt.get('secondary_reduction')
        if not secondary:
            secondary = 6.034
            assumed.append('secondary reduction 6.034 as the 2012 TMAX 530 (same engine)')
        if primary and primary > 1.5:          # TMAX 500: a primary and a secondary around the belt
            final = secondary
        else:
            primary, final = 1.0, secondary
    else:
        if not gears and e['id'] in GEARS_FROM:
            sib = BY_ID[GEARS_FROM[e['id']]]
            gears = sib['gear_ratios']
            if not primary and num(sib.get('primary_reduction')) and final:
                # the final is the variant's own: the same engine's box and primary make the whole gearing
                primary = sib['primary_reduction']
                assumed.append(f'gear ratios and primary {primary} borrowed from {sib["id"]} (same engine)')
            else:
                assumed.append(f'gear ratios borrowed from {sib["id"]} (spread only: the overall gearing is fitted below)')
        if not gears:
            raise ValueError(f"{e['id']}: no gears")
        if not primary and e['id'] in PRIMARY:
            primary, why = PRIMARY[e['id']]
            assumed.append(why)
        if not (primary and final):
            # fit the overall gearing: 5% past peak power in top gear at the top speed (claimed, else drag-limited)
            top = num(e.get('top_speed_kmh'))
            if not top:
                top = (p_kw * 1000 * 0.9 / (0.5 * 1.2 * cda)) ** (1 / 3) * 3.6
            r = tyre_radius(rt)
            overall = p_rpm * 1.05 * math.tau / 60 * r / (top / 3.6)
            if not primary:
                primary = 1.7
            final = overall / (primary * gears[-1])
            assumed.append(f'primary {primary:.3f} and final {final:.3f}: the overall top gear fitted to {top:.0f} km/h at 105% of the peak-power rpm')

    seat = num(e.get('seat_height_mm'))
    seat = (seat[0] if isinstance(seat, list) else seat) if seat else None
    if not seat:
        seat = {'Scooter': 800, 'Sport': 780, 'Naked': 800, 'SportTouring': 820, 'Adventure': 850, 'Supermoto': 890}[style]
        assumed.append(f'seat {seat} mm')
    sh = seat / 1000
    ft_mm = num(e.get('front_travel_mm')) or {'Scooter': 120, 'Sport': 130, 'Naked': 140, 'SportTouring': 135, 'Adventure': 190, 'Supermoto': 215}[style]
    rt_mm = num(e.get('rear_travel_mm')) or ft_mm
    disc = num(e.get('front_disc_mm')) or 300
    fb = (e.get('front_brake') or '').lower()
    discs = 2 if any(w in fb for w in ('twin', 'dual', 'double', '2 ', 'two')) else 1 if fb else 2
    tank = num(e.get('fuel_capacity_l')) or 0
    clearance = num(e.get('ground_clearance_mm'))
    paint, trim, frame, wheel = livery(e)

    refs = []
    top = num(e.get('top_speed_kmh'))
    tst = (e.get('top_speed_source_type') or '').lower()
    ref_top = top if top and not tst.startswith('estimated') else 0
    ref_100 = num(e.get('accel_0_100_s')) or 0
    assumed.append(f'the {style.lower()} values (CG {cg} m, {int(rear * 100)}% rear, CdA {cda} m², grip {grip}, {lean}° lean, '
                   f'brakes {brake} m/s², riding position), the torque curve between the peaks, idle {idle}, launch {launch:.0f}')
    assumed.append(f'livery from "{e.get("livery_main_colour")}"')

    cc = e.get('displacement_cc')
    kind = 64 + index if index < 32 else 128 + index - 32
    lay_txt = {'Inline4Bike': 'inline four', 'ParallelTwin180': '180° parallel twin', 'ParallelTwin270': '270° parallel twin',
               'ParallelTwin360': '360° parallel twin', 'Triple': 'CP3 triple', 'Single': 'single',
               'VTwin75': '75° V-twin' if shape == 'VTwin' else '75° parallel twin'}[layout]
    gear_txt = f'CVT {gears[0]}-{cvt["high_ratio"]}' if cvt else f'{len(gears)} gears {gears[0]}-{gears[-1]}'
    blurb_tail = {'Scooter': 'twist and go: CVT automatic', 'Supermoto': 'light, tall, wire wheels',
                  'Adventure': 'adventure tyres: grips on gravel and grass', 'SportTouring': 'half fairing, upright',
                  'Naked': 'upright bars', 'Sport': 'clip-ons and a full fairing'}[style]
    src = list((e.get('sources') or {}).values())[:3]
    lines = [f'        // ---- {kind} ----',
             f"        // {label(e)}. docs/data/{os.path.basename(e['_file'])} \"{e['id']}\" ({e.get('variant') or ''}).",
             f"        // {cc} cc {lay_txt}, {ps:.0f} PS at {p_rpm:,.0f} rpm, {t_nm} N·m at {t_rpm:,.0f}; {gear_txt}, primary {primary:.3f}, final {final:.3f}; "
             f"kerb {mass:.0f} kg; wheelbase {wheelbase} mm; rake {rake}°, trail {trail} mm; {ft} + {rt}."]
    if ref_top or ref_100:
        lines.append(f"        // Reference: {ref_top or '-'} km/h ({e.get('top_speed_source_type')}), 0-100 {ref_100 or '-'} s.")
    lines += [f'        // {u}' for u in src]
    lines.append('        // Assumed: ' + '; '.join(assumed) + '.')
    colours = '; '.join(e.get('colours') or []) if isinstance(e.get('colours'), list) else (e.get('colours') or '')
    look = [f'Style = MotoStyle.{style}, EngineShape = MotoEngineShape.{shape}' + (f', VAngleDeg = {f(vang)}' if shape == 'VTwin' else '') + (', Spoked = true' if spoked else '') + ',',
            f'Wheelbase = {f(wheelbase / 1000)}, RakeDeg = {f(rake)}, Trail = {f(trail / 1000)}, ForkLength = {f(fork)},',
            f'FrontTyre = "{ft}", RearTyre = "{rt}", FrontTravel = {f(ft_mm / 1000)}, RearTravel = {f(rt_mm / 1000)},',
            f'FrontDiscs = {discs}, FrontDiscMm = {f(disc)}' + (f', TankLitres = {f(tank)}' if tank else '') + (f', GroundClearance = {f(clearance / 1000)}' if clearance else '') + ',',
            f'Seat = new Vector3(0, {f(sh)}, {f(seat_z)}), Grip = new Vector3({f(grip_off[0])}, {f(sh + grip_off[1])}, {f(grip_off[2])}), Peg = new Vector3({f(peg[0])}, {f(peg[1])}, {f(peg[2])}),',
            f'Paint = {col(paint)}, Trim = {col(trim)}, Frame = {col(frame)}, Wheel = {col(wheel)},']
    curve = torque_curve(idle, float(t_nm), float(t_rpm), p_kw, float(p_rpm), float(redline))
    body = [
        '        new MotorbikeSpec',
        '        {',
        f'            Label = "{label(e)}",',
        f'            Brand = "{e["brand"]}", Family = "{e["family"]}",',
        f'            Blurb = "{cc} cc {lay_txt}, {ps:.0f} PS, {mass:.0f} kg, {blurb_tail}",',
        f'            Engine = EngineLayout.{layout},',
        '            Look = new MotoLook',
        '            {',
        *[f'                {l}' for l in look],
        '            },',
        f'            Colours = "{colours.replace(chr(34), chr(39))}",',
        f'            WetMass = {f(mass)}, CgHeight = {f(cg)}, RearShare = {f(rear)}, Grip = {f(grip)}' + (f', OffroadTyre = {f(offroad)}' if offroad else '') + f', MaxLean = Mathf.DegToRad({f(lean)}),',
        f'            DragArea = {f(cda)}, BrakeDecel = {f(brake)},',
        f'            IdleRpm = {f(idle)}, PeakRpm = {f(p_rpm)}, Redline = {f(redline)}, LaunchRpm = {f(launch)},',
        '            Torque = new (float, float)[] { ' + ', '.join(f'({f(r)}, {f(n)})' for r, n in curve) + ' },',
        '            Gears = new[] { ' + ', '.join(f(g) for g in gears) + f' }}, Primary = {f(primary)}, FinalDrive = {f(final)},',
    ]
    if cvt:
        body.append(f'            Cvt = true, CvtHigh = {f(cvt["high_ratio"])}, CvtRpm = {f(p_rpm)},')
    if ref_top or ref_100:
        body.append(f'            RefZeroTo100 = {f(ref_100)}, RefTopKmh = {f(ref_top)},')
    body.append('        },')
    return '\n'.join(lines + body)


BY_ID = {}


def main():
    entries = []
    for path in DATASETS:
        full = os.path.join(ROOT, path)
        if not os.path.exists(full):
            print(f'skipped (missing): {path}')
            continue
        for e in json.load(open(full, encoding='utf-8')):
            e['_file'] = path
            BY_ID[e['id']] = e
            entries.append(e)
    code = [entry(e, FIRST_INDEX + i) for i, e in enumerate(entries)]
    head = '''using System.Collections.Generic;
using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;

namespace UnitSport.Player;

// GENERATED by tools/motorbikes/gen_catalog.py from docs/data/*_specs.json (#410). Do not edit by hand:
// change the dataset or the script and run it again. Append-only, like the rest of the catalog.
public static partial class MotorbikeCatalog
{
    /// <summary>The bikes from the datasets, after the hand-written entries.</summary>
    private static IEnumerable<MotorbikeSpec> Imported() => new[]
    {
'''
    with open(os.path.join(ROOT, OUT), 'w', encoding='utf-8', newline='\n') as out:
        out.write(head + '\n'.join(code) + '\n    };\n}\n')
    print(f'{len(entries)} bikes -> {OUT}')


if __name__ == '__main__':
    main()
