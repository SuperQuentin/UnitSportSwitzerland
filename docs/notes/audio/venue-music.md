# Venue music (#1244, part 6 of epic #1232)

The venues play live radio on the car radio's relay (`web-radio`), as the owner decided: a station
fitting the venue, the same sample on every client. When the relay is silent (offline, a server without
ffmpeg or internet), they play tracker modules on our own player, also the same bar on every client.

- **Sources** (`Audio/Live/VenueMusicSource`): a `Node3D` that registers itself while in the tree,
  with a station and a volume. `WebRadio.Scan` adds every one to the players and parked cars it
  already hangs `WebRadioSpeaker`s on, and wants their stations like any other (nearest first, three
  at most, `KeepFor`): no new message, the server taps what is wanted.
- **Out of earshot they sleep** (#1281): `WebRadio.Scan` sets a source beyond `RadioLoudness.Radius(volume)` + 15 m
  (`VenueWakeMargin`) to `ProcessMode.Disabled` (its radio and tracker speakers inherit it) and hangs no speaker on it.
  Every source processing each frame cost Geneva half its main thread (52 ms frames, dotnet-trace).
- **Where** (`Interiors/VenueMusic`): one in each venue room of a built interior (`AddTo`, beside the
  staff in `InteriorManager.Create`), at its centre under the ceiling, volume 0.5; one just inside each
  venue's door (`AtDoor`, beside its neon sign in `VenueSigns`), volume 0.12: the street hears it
  through the wall, within some 17 m. No auditorium or kitchen plays anything.
- **Which station** (`StationFor`, by venue kind or room kind; ids in `Stations`): bowling One FM,
  arcade SRF Virus, cinema lobby Radio Swiss Pop, a Beiz its region's first SRG channel (SRF 1, La 1ère,
  Rete Uno: `RegionOf`, Romandie west of E 2'570'000, Ticino south of N 1'170'000 and east of
  E 2'680'000), pizzeria Rete Uno, café Radio Swiss Jazz, fast food Energy Bern, bar Radio Paradise,
  pub and beer garden SRF 3, club Couleur 3. The research's pairing (`venue-dining`, light and sound)
  within the stations the relay has.
- **Checks**: `--venuecheck` asks a station of every kind in every region. Offline at the Flon
  (`--origin 2537000,1152000`, the bowling shot `701.5,g1.7,-539.4,...`) the log says `listening to
  RTS Couleur 3, One FM, Radio Paradise` outside and `One FM` inside the Bowl Bar. Hearing it needs
  ffmpeg for the server's taps (`BundledTools`, or PATH): this machine had none when it was written.
- **Tracker fallback** (`Audio/Tracker/`, #1244): a `TrackerSpeaker` beside each source's radio speaker
  takes over once the radio has been silent 5 s (`WebRadioSpeaker.HeardIndex < 0`), and stops when it
  comes back. Only the 2 nearest silent sources play (`TrackerSpeaker.Rank`, from `WebRadio.Scan`): each
  mixes its own song. Headless peers make none. Heard like the radio: `Hearing` through walls, ducked by
  `MusicFocus`, 2 dB under it.
- **The modules** (`assets/music/venues/`, 26 files, 5.9 MB, Git LFS): public domain or CC0 on The Mod
  Archive, sources in `assets/ASSETS.md`. Found by crawling its licence listings (`view_by_license`,
  `publicdomain` and `cc0`: 984 modules, 701 IT, 182 XM, 57 MOD, 26 S3M in Oct 2026), then each XM/MOD's
  page for genre and rating; crude titles left out. WebFetch got 503 from the site, curl with a browser
  agent got through. Playlists by `VenueMood` (`TrackerLibrary.Playlists`): arcade chiptune (Drozerix),
  bowling chip-pop, club techno and trance (Katie Cadet, m0d), bar jazz and R&B, café jazz and easy
  listening (congusbongus, JAM), pub funk, hard rock and reggae, Beiz a polka and the Can-Can, pizzeria a
  saltarello, fast food hip-hop and chip-pop, cinema lobby ambient.
- **Not in `assets/audio/`**: no export packs that folder (the chess beat, #718); `assets/music/*` is in
  every preset's `include_filter` (`tools/ci/export_presets.cfg`, `deploy-linux.sh`, `quest.sh`, and
  `release.sh` sets it on its copy of the per-machine presets). An exported pack holds all 26, not the beat.
- **The player** (`TrackerModule`, `TrackerPlayer`, pure C#): MOD (31 samples, `M.K.`/`xCHN`/`xxCH`) read
  as an XM in Amiga mode; XM 1.04 (packed patterns, delta samples, envelopes). FastTracker 2 replay tick
  by tick: effects 0-F, E1-EE, G H K L P R X, the volume column, volume and panning envelopes, fadeout,
  linear and Amiga periods (FastTracker units), linear interpolation, a soft knee over 0.9; gain
  0.9/sqrt(channels). Leaves out IT and S3M, tremor, auto-vibrato, other LFO waveforms. `Skip` moves each
  voice in one step, so a client entering mid-song seeks at once; `Duration` plays a song through without
  mixing until it comes round.
- **Same bar everywhere**: a mood's playlist runs round on `ClockSync.ServerNow`, shifted by a salt of the
  building's key, so a room and its door play the same and two cafés side by side do not
  (`TrackerLibrary.At`); a speaker re-seeks when it drifts over 1 s.
- **A module whole**: `--trackerwav <file>` (#1306) writes one module, all of it, to `test_output/tracker/<name>.wav`
  (22,050 Hz stereo) and quits: a film's soundtrack (`tools/cinema-showcase.py` uses `crusaders.xm`, 92 s).
- **Checks**: `--trackercheck` (every module loads and lasts 69-316 s, plays neither silent nor clipped,
  seeking to 30 s sounds as playing to it, correlation 0.97-1.00; the schedule runs on with the clock;
  20 s of each mood written to `test_output/tracker/`). The sound player: "Venue music", 30 s of each.
  The owner listened to the arcade, Beiz, club and café renders (Oct 27 2026): they sound right.
  Offline at the Flon without ffmpeg the log shows `Bowling plays drozerix_-_poppy_flower_girls.xm from
  12 s` 5 s after the radio stayed silent.
- **Film music** (#1279): a cinema hall plays while its screen is on (`InteriorNode.HallMusic`, a source per
  showing hall, made and freed with its screen): Radio Swiss Classic (`VenueMusic.FilmStation`), the `Film`
  playlist when the relay is silent. Of the archive's 239 public-domain/CC0 XM and MOD modules only "Crusaders"
  is tagged orchestral; the others by mood: Thrones (a 32-channel MOD, quiet: rms 0.015), Haunted Occult
  Mansion, Journey through time, Vocatio Spirituum.
- **A draw per hall** (#1279): each hall's seed picks the radio (one draw among the Film modules plus one) or
  the modules alone, which then start at once (`TrackerSpeaker`: a source with station 0 waits for no relay);
  the halls are spread evenly round the playlist's cycle (`TrackerLibrary.Cycle`), so the next hall is a song on.
- **Even across a hall** (#1279): a source's `Spread` is its speakers' `UnitSize` (a car stereo's 3 m); a cinema
  hall's is its half-diagonal, 3 m up, so the whole hall hears it at full level, not only its middle; every venue
  room's (lobby, lanes, guest rooms) the same.
- **Next**: venue ambience (chatter, cutlery, balls and pins, coin drops) in the sound player, quiet hours
  for terraces, IT modules (most of the archive's free ones) if a playlist wants them.
