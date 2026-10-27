# Cinemas (#1232)

> Research for #1232, Oct 2026: tagged [S] sourced, [E]/[H] estimated, [D] derived. Constants built from [E] numbers stay tunable.

Main source: UNIC "Cinema auditorium design" handbook (Feb 2020). OSM `amenity=cinema` is 272 in Switzerland.

## ProCinema statistics (Facts & Figures 2024, BFS) [S]

- **258 cinemas, 615 screens, 96,641 seats, 188 operators, 102 open-airs**; 10.65 M admissions, CHF 170.9 M
  box office, average ticket CHF 16.05. Mean 2.4 screens and 375 seats per cinema; **157 seats per screen**.
- Trend (cinemas / screens / seats): 2001 334 / 508 / 108,025; 2010 305 / 558 / 111,012; 2019 272 / 609 /
  101,739; 2024 258 / 615 / 96,641. Fewer venues, more screens.
- **Cinemas by screen count**: 1 screen 155 (60 %), 2: 35, 3: 20, 4: 12, 5: 8, 6: 8, 7: 2, 8: 5, 9: 1,
  10: 5, 11: 2, 12: 2, 13: 1, 14: 1, 18: 1 (sums to ~625 screens, approximate).
- **18 multiplexes** (>= 8 screens): Cinedome Abtwil, Arena Basel, Kuchlin Basel, Cinedome Muri/Bern, Pathe
  Westside Bern, blue Cinema Chur, Pathe Dietlikon, Pathe Ebikon/Luzern, Maxx Emmen, Arena Fribourg, Arena
  Geneve, Pathe Balexert, Galeries Lausanne, Kinepolis Schaffhausen, Pathe Spreitenbach, Kiwi Winterthur,
  Abaton Zurich, Arena Zurich (Sihlcity). They take **39.7 %** of admissions (4.23 M), the rest 60.3 %.
- By region (cinemas / screens / seats): German 163 / 422 / 63,488; French 84 / 167 / 28,911; Italian
  11 / 26 / 4,242. Tech: 99 % digital, **281 3D**, **6 IMAX**, 15 4DX, 6 ScreenX, 4 D-BOX.
- **Generator weights** per municipality [E]: 60 % single-screen town cinema (1 hall, 80-250 seats), 25 %
  2-4 halls (60-200 seats each), 15 % mall/multiplex 6-14 halls (100-380 seats each, up to 18).

## Seats, rows, aisles

- **Seat width** (centre to centre): tip-up 0.50-0.75 m, double armrests 0.65-0.90, recliner 0.65-0.90+ [S].
  Defaults **0.58 m** standard, **0.70 m** premium, **1.2-1.4 m** love seat.
- **Row pitch** (back to back): UNIC >= 1.20 m, commercial 1.0-1.2, recliner 1.4-1.6 [S]. Defaults
  **1.10 m** standard, **1.25 m** comfortable, **1.50 m** recliner. Min clear passage 0.40-0.45 m (VStaettV
  0.40; UNIC 0.35-0.50) [S].
- **Seat depth** folded 0.50-0.80 m, fixed 0.80-1.00, recliner 0.9-1.4 (1.8 reclined); last row +0.15-0.20 m [S].
- **Block size**: 30-50 seats between two aisles (UNIC). Generator: blocks of **6-14 seats**, max 22 per row
  in small rooms, 3 blocks (wider centre) above 200 seats [S/H].
- **Aisles**: stair aisles 1.0-1.2 m; defaults **1.1 m** side, **1.2-1.5 m** cross [S]. Optional wall gap
  0.4-0.5 m. **Cross aisle** (rooms > 14 rows): seatless 1.2-1.5 m or a row at 2.2-2.5 m pitch; the
  entrance lands on it (step-free), the riser in front is higher [S]. Curved rows optional (~10 deg).
- **Wheelchair** 0.9 x 1.4 m + 1.5 m turning circle, paired with a companion seat, never front row, on the
  cross-aisle level; ramps 1:15-1:20, handrails 0.9-1.1 m [S].

## Stadium floor

- Shoebox with stepped seating; dark carpet in aisles and in front of the screen, hard floor between rows [S].
- **Riser per row** grows toward the back: **0.10-0.20 m** rows 1-4, **0.25-0.35** mid, **0.35-0.45** rear.
  Sightline clearance >= 200 mm good, 150-200 acceptable, eye height 1.2 m [S]. Max step 0.20 m: riser < 0.40
  uses 2 steps, < 0.60 uses 3; tread >= 0.25 m.
- **Slope** max 15-20 deg (QSC); 0.35 / 1.10 = 17.6 deg. Rear rise for 200 seats (12 rows) 2.5-3.2 m. Clear
  height under the beam >= 1.7 m (1.6 absolute) at the last row [S/D].
- Old halls: ramp 1:12-1:20, often a balcony (Rang/Loge). Step lights: LED strips on risers or row ends.

## Screen

- **Aspect** scope 2.39:1 default (flat 1.85 pillarboxed); old halls 1.85 or 1.37 with curtains [S].
  H = W / 2.39 (W 7.5 gives 3.1 m; 10.5 gives 4.4; 15 gives 6.3) [D].
- **Width** = room width - 1.0 to 2.0 m (halls <= 300 seats); wall to wall (-0.3 m) for premium [S/H].
- **Bottom edge** 0.8-1.0 m above the front floor (0.5-0.6 with a 0.2-0.4 m stage); flat or 5 % curve [S].
- **Front row** eye distance >= **0.6 x W**, hard min 0.5 x W; vertical angle to top <= 45 deg; **last
  row** <= ~2.0-2.2 x W [S/H]; horizontal angle < 45 deg (good <= 35).
- **Behind the screen** >= 1.2 m (1.0 m if the last row is < 15 m away); black baffle wall 0.9 m wider each
  side; perforated screen, speakers hidden [S].

## Room

- **Proportions** W:L ~ 1 : 1.3-1.9, flat rear wall. **Ceiling** 4-6 m at the front, follows the stadium or
  stays flat 3.0-3.5 m above the last row (clear >= 2.6-3.0 m) [D/H].
- **Finishes**: matte black ceiling, no visible grid; walls near the screen matte black; rear walls dark
  (charcoal, aubergine, deep blue, dark red) with fabric panels or wood slats; curtain (old/arthouse)
  burgundy velvet, gold fringe, valance 0.5 m, modern black masking panels on a track [S].
- **Lights** (UNIC): Full 150+ lux, Half 50-150 (entry, credits), Dim 3-5 lux (pre-show), Off 0. Fade 3-5 s,
  tint toward red/amber as they dim. Exit signs and step lights stay on [S].
- **Exits**: green running-man sign visible from every seat; light-proof vestibule at the rear entrance; front
  side exits beside the screen; acoustic doors Rw > 45 dB [S].
- **Speakers**: L/C/R behind the screen at 2/3 picture height, subs below; side surrounds at 2.5-3.5 m, 1.0-2.5 m
  apart; rear surrounds on the back wall; Atmos ceiling grid at 2-3 m [S/H]. Noise < NR25; inter-hall wall
  65-75 dB (double wall) [S].

## Halls by size [D]

Assumes 0.58 m seats, 2 side aisles 1.1 m, 0.4 m wall gaps, pitch 1.10 m, front row at 0.6 W, 1.0-1.2 m behind
the screen, 1.5 m rear zone.

| Seats | Rows x per row | Room W | Room L | Ceiling (front) | Screen W x H | Notes |
|---|---|---|---|---|---|---|
| 40-60 | 6 x 10 | 8 | 11-12 | 3.5 | 5.5 x 2.3 | arthouse, nearly flat, 0.4 m rise |
| 100 | 8 x 13 | 10.5 | 15 | 4.2 | 7.5 x 3.1 | town cinema, small multiplex hall |
| 200 | 12 x 17 | 13-14 | 22 | 5.5 | 10.5 x 4.4 | median newer hall |
| 300 | 15 x 20 + cross aisle | 15-16 | 28 | 7 | 12.5 x 5.2 | 3 seat blocks |
| 400 | 17 x 23 + cross aisle | 17-18 | 31-32 | 8 | 15 x 6.3 | flagship (Balexert hall 3: 380) |
| 600-900 | balcony or 25+ rows | 22-26 | 36-42 | 9-12 | 18-20 x 8 | palace / IMAX class |

- **Smallest full-size hall** ~50 seats, 8.0 x 11.5 x 3.6 m; 150 seats ~12 x 19 m, screen 9 x 3.8. **Swiss anchors** [S]: Pathe Balexert (Geneva, 13 screens, halls 114-380 seats incl. a 324-seat IMAX);
  Pathe Spreitenbach (2019, 10 screens, 1,322 seats, IMAX, kids cinema, VIP "cocoon" rooms, 3 Atmos halls);
  Arena Sihlcity Zurich (~18 halls, > 2,680 seats); Capitole Lausanne (1928, grand hall ~867-900 seats, was
  1,077, plus a cellar hall); Corso Zurich (Kitag, 4 halls, ~1,335 seats, variety theatre of 1900).

## Projection room

- **Where**: single screen = room behind and above the rear wall (floor at or above the top row, +0.3-1.0 m),
  stairs from the foyer, port high on the axis; multiplex = **shared booth corridor** behind the halls, one
  porthole per hall; small halls = boothless box hung from the ceiling [S].
- **Size** 2.4-3.0 m deep x 2.5-4.5 m wide per bay x 2.5-3.0 m high. **Porthole ~0.8 x 0.4 m** (1.2-1.4 with a
  viewing window), low-iron glass, angled, gasketed [S].
- **Geometry**: lens ~1.2 m above booth floor, porthole 1.0-1.5 m up, down angle <= 8-10 deg [S].
- **Contents**: digital projector 0.7 x 1.2 x 0.5 m on a pedestal + exhaust duct; 19-inch rack 1.8-2.0 m with
  blinking LEDs; wall dimmer panel with sliders and curtain buttons; optional 35 mm projector 0.5 x 1.6 x
  1.7 m + platter (1.0-1.5 m dia x 1.8, three tiers), film cans 0.4 m dia, rewind bench, splicer; stool and
  desk, intercom, red safe-light, steel fire shutters over old ports [S/H]. Staff-only door, usually locked.
- **Gameplay hook** [H]: from the booth set house lights (full/half/off, dim sequence), start or stop the
  picture, open the curtain, PA on; the viewing port shows the audience.

## Lobby and zones

- **Chain**: entrance (double glass door, vestibule) -> foyer with box office (Kasse/caisse) and posters ->
  concession -> waiting seats -> WCs -> corridor with ticket check at the hall door.
- **Old town cinema**: foyer 3-5 m deep x 6-10 m wide, box office in the entrance hall, stairs to the balcony,
  cloakroom; entrance 4-6 m wide with a marquee; hall at the back of the plot or in a cellar.
- **Sizing** [H]: foyer **0.35-0.6 m2 per seat**, multiplex concourse **0.6-0.9 m2 per seat**. Concession
  counter ~1.2 m per 100 seats, min 4 m, 1.0-1.1 m high, queue depth >= 3 m. Box office 1-3 windows of 1.2 m;
  kiosks 0.8 x 0.5 x 1.8 m, 1 per 3-4 halls. WC 1 cabin per 40-60 seats + 1 accessible (1.5 x 1.8 m). Hall
  doors 1.2-1.8 m double leaf with sound lock.
- **Concession**: popcorn machine, soda fountain 6-12 nozzles + ice machine, candy case, hot dog roller
  (Bratwurst), nacho warmer, POS, 3-6 LED menu boards in CHF, stanchions. Arthouse cinemas (Riffraff, Houdini,
  Kosmos Zurich; Cinema Bourg Lausanne) add a bar with beer, wine, Aperol [H].
- **Corridors** 2.0-3.0 m wide, dark patterned carpet, lit hall number (0.3 x 0.3 m) + LCD at each door [H].
- **Multiplex plan**: ground-floor hub (tickets + concession), 1-2 corridors in a comb, largest halls at the
  far ends, small ones near the lobby, 2-3 levels in malls (escalator + lift), booth corridor behind. Mall
  cinemas face the food court with a sign on the mall side [S].

## Props [W x D x H, m]

- **Seats**: classic tip-up 0.55 x 0.85 (up) / 0.95 (down) x 1.0, burgundy velvet; modern 0.58 x 0.9 x 1.05
  charcoal/grey/dark red, cupholder 0.1 x 0.1; recliner 0.80 x 1.0 (1.8 reclined) x 1.1; love seat 1.3 x 0.9 x 1.0.
- **Hall**: screen W x W/2.39 with black masking border 0.3-0.5 m; curtain 8-14 m wide; step light 0.3-1.0 m
  strip, amber/white, 2-5 lux; exit sign 0.4 x 0.2 (ISO 7010); surround speaker 0.3 x 0.3 x 0.4 black; Atmos
  speaker 0.25 round; sconce 0.15 x 0.3 at 2700 K; hall door 1.4-1.8 x 2.1 dark padded.
- **Concession**: popcorn machine 0.9 x 0.6 x 2.0 red/gold; bucket 0.18 dia x 0.25 red-white; soda cup 0.1 dia
  x 0.18 (S) to 0.12 x 0.25 (XL); fountain 0.6 x 0.5 x 0.7; hot dog roller 0.6 x 0.4 x 0.35; nacho warmer 0.4 x
  0.3 x 0.4; candy case 1.5 x 0.6 x 1.0; counter 1.2-5 m x 0.8 x 1.05 with LED strip; menu board 1.2 x 0.7.
- **Lobby**: lightbox 0.9 x 1.28 x 0.12 or 1.2 x 1.75; standee 0.7 x 0.05 x 1.8; stanchion 0.35 dia x 0.95
  chrome/brass; sofa 2.0 x 0.9 x 0.8 red/teal; bin 0.4 x 0.4 x 0.9; also banners, trailer screens, kids
  corner, old arcade machines, coat check, "Now showing" board, escalator or lift [H/S].

## Ambiance

- **Schemes**: classic (crimson velvet, gold leaf, dark wood, brass, chandeliers); art deco (teal/mint, chrome,
  terrazzo); modern multiplex (black + one accent, LED strips); arthouse (brick, wood floor, Edison bulbs).
  **Carpet**: busy dark geometric, star or swirl patterns; "starry sky" black with white dots is the icon.
- **Light**: lobby 2700-3000 K with neon accents (cyan/magenta/red), lightboxes; corridors dim. Show
  sequence: full -> half -> dim 3-5 lux red-amber -> curtain open -> off; credits at half, then full.
- **Sound**: popcorn pop, register beeps, ice rattle, lobby chatter, trailer through acoustic doors
  (low-passed, louder when opened), door closer, tip-up seat spring, projector fan hum 40-45 dB, air-conditioning
  rumble, muffled bass from the next hall, low lobby music, announcements.

## Exterior

- **Marquee**: canopy 3-8 m wide x 1.0-1.5 deep x 0.6-1.0 high fascia, changeable-letter board (black letters
  0.2-0.3 m, 2-3 lines) front and sides, chaser bulbs, lit underside [S].
- **Blade sign**: vertical 4-10 m x 0.8-1.5 m on the corner, letters top to bottom ("KINO", "REX"), bulbs or neon [S].
- **Multiplex facade**: 3D lit letters 1-2 m high, LED video wall, tall glass lobby. Generic icons only: film
  reel, clapperboard, popcorn bucket, star, projector beam, ticket stub.
- **Poster cases**: 2-6 backlit vitrines 0.8 x 1.2 m beside the entrance, A-frame, "now showing" LED over
  the door; double glass doors, brass handles, red carpet strip, bollards.
- **Names**: Rex, Capitol/Capitole, Apollo, Corso, Royal, Scala, Palace, Lux, Casino, Studio, Stella, Orient,
  Bio (Bern/Basel), Odeon, Eden, Bourg, Roxy, Rialto, Vox, Metropol, Lido, Uto; Kino (DE), Cinema (FR/IT).

## Sources

- https://www.unic-cinemas.org/fileadmin/user_upload/Publications/UNIC_handbook_online_02_20__1_.pdf (UNIC),
  https://www.procinema.ch/pdf/statistics/factsfigures/2024/FactsFigures_SwissCinema_2024.pdf (ProCinema),
  https://www.bfs.admin.ch/bfs/de/home.gnpdetail.2024-0195.html , https://www.moneycab.com/schweiz/leichte-erholung-in-der-schweizer-kinobranche/amp/
- https://en.wikipedia.org/wiki/Projection_booth , https://www.film-tech.com/ubb/f1/t012242.html ,
  https://www.qsys.com/resource-files/corporate/q_cin_ctp_specifications.pdf (slope) ,
  https://professional.dolby.com:443/siteassets/cinema-products---documents/dolby-atmos-specifications.pdf ,
  https://www.gesetze-bayern.de/Content/Rtf/BayVStaettV-10?all=False
- Swiss halls: https://search.ch/kino/Dalloway/Gen%C3%A8ve/Pathe-Balexert/2025-10-09 , https://torpille.ch/listing/cinema-pathe-balexert-vernier/ ,
  https://www.boxofficepro.com/cinemanext-equips-switzerlands-pathe-spreitenbach-with-three-dolby-atmos-sound-systems/ ,
  https://www.lausanne-tourisme.ch/fr/decouvrir/cinema-capitole-cinematheque-suisse/ ,
  https://www.srf.ch/kultur/literatur/kinolandschaft-schweiz-das-sind-die-schoensten-kinos-der-schweiz ,
  https://zuri.net/de/zurich/kinos/arena-cinemas-3400.htm , https://zero.eu/de/luoghi/86887-kitag-cinemas-corso,zurich/
  https://wagnersign.com/wp-content/uploads/2025/04/Marquee_Catalog.pdf , https://www.shomler.com/calsj/blade2.htm ,
  https://www.leadcomseating.com/blog/cinema-seating-capacity-experience/
