# Venue music (#1244, part 6 of epic #1232)

The venues play live radio on the car radio's relay (`web-radio`), as the owner decided: a station
fitting the venue, the same sample on every client. The tracker-module fallback (offline or without
the relay) is still to come: it needs module files downloaded under free licences, chosen with the
owner.

- **Sources** (`Audio/Live/VenueMusicSource`): a `Node3D` that registers itself while in the tree,
  with a station and a volume. `WebRadio.Scan` adds every one to the players and parked cars it
  already hangs `WebRadioSpeaker`s on, and wants their stations like any other (nearest first, three
  at most, `KeepFor`): no new message, the server taps what is wanted.
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
- **Next**: the tracker fallback (a small XM/MOD player of our own and a few modules, licences in the
  repo), venue ambience (chatter, cutlery, balls and pins, coin drops) in the sound player, quiet hours
  for terraces.
