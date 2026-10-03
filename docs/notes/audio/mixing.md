# The mix: buses, levels, spaces (#375)

## Bus tree (`SfxBus.Ensure`, built in code)
```
Master  [AudioEffectHardLimiter, ceiling -1 dB, release 0.1 s]: catches peaks only
 |- Sfx     the world: others, vehicles, ambience, impacts, air bed  [cabin low-pass, reverb]
 |- Player  this body: own steps/landings/whoosh, own engine (non-positional EngineSynth), skis  [reverb x0.8]
 '- Music   radios, car CDs, live stations  [reverb x0.7]
```
- Sliders: Sfx slider -> Sfx and Player; Music slider -> Music; Master -> bus 0 (square law, `SliderDb`).
- Cabin: in a closed vehicle the Sfx bus gets a log-swept low-pass to ~1.4 kHz and -8 dB
  (`SetCabin(Ears.Shut)`, writes only on change, filter disabled when open). Player and Music are
  not behind the glass; radios outside are muffled by `Hearing` instead (Music has no cabin filter).
- Any new own-body sound: Player bus. Anything positional in the world: Sfx.

## Levels (heuristic targets, from the research below)
- Mix integrated loudness ~ -24 LUFS, true peak <= -1 dBTP (Sony ASWG-R001; the limiter holds the peak).
- Own footsteps 6-10 dB under action sounds: `PlayerFeel` plays them at 0.14-0.44 linear, landings 0.2-0.8.
- Air bed 12-18 dB under the anchor: wind 0.07-0.20, leaves 0.09 x wooded (`AirBed`): noticed when you stop.
- Engine in the cabin ~ -9..-16 dB; car radio a few dB under it at cruise (BaseDb -6/-8 + cabin path).

## Spaces (`ReverbZones`, values room/damp/wet/predelay ms)
- open 0.08/0.9/0.03/6: the ground reflection, never fully dry (fully dry read as a studio void).
- high (>2200 m) 0.05/0.95/0.015; forest 0.45/0.82/0.07/20 (diffuse, damped, no echo).
- street (facades both sides within 25 m) ~0.3/0.55/0.07; covered 0.28/0.6/0.09.
- valley: an echo, not a hall: predelay = round trip to the nearest rock (20-500 ms), feedback 0.22, wet 0.03-0.08.
- indoors by room volume (`RoomVolume` from the plan): room <40 m³, indoors <250, hall above; church 0.9/0.3/0.32/35.
- tunnel 0.85/0.3/0.4/12; car cabin 0.05/0.9/0.05/2.

## Footsteps (`Surfaces.Make`)
Heel contact, then the ball of the foot 55-90 ms later at ~0.5-0.65 gain with faster decays, and
a 60-110 Hz weight thump under both (less on snow/grass). Pitch +-4 %, gain +-1.5 dB per play, 10
variants, never the same twice. Others' steps: `BodySteps` (remote copies, from their motion, 35 m).

## Sources
- Loudness: https://www.audiogang.org/wp-content/uploads/2015/04/IESD-Mix-Ref-Levels-v03.02.pdf
- HDR / ducking: https://frostbite.com/frostbite/news/how-hdr-audio-makes-battlefield-bad-company-go-boom ,
  https://www.gamedeveloper.com/audio/game-audio-theory-ducking
- Snapshots: https://gamedeveloper.com/audio/the-game-audio-mixing-revolution
- Air absorption: https://sengpielaudio.com/calculator-air.htm (3.3 dB/100 m at 4 kHz, 11.7 at 8 kHz)
- Reverb times: https://commercial-acoustics.com/guides/target-reverb-times/ , https://jcaa.caa-aca.ca/index.php/jcaa/article/view/783
- Footsteps: Turchet/Serafin/Nordahl DAFx-10 https://vbn.aau.dk/files/37619408/Serafin_Turchet_Nordahl_DAFx10_P82.pdf ;
  Farnell, Designing Sound (MIT Press 2010); Cook PhISEM / STK https://ccrma.stanford.edu/software/stk/
- Listener placement: https://www.audiokinetic.com/qa/6112/unity-distance-relative-the-player-panning-relative-camera ,
  https://blog.criware.com/index.php/2022/09/02/focus-point/
- In-car / occlusion: https://community.bistudio.com/wiki/Arma_Reforger:Audio:_Occlusion
- Godot: https://docs.godotengine.org/en/4.7/classes/class_audiostreamplayer3d.html

## Not done yet (ideas)
- Sidechain ducking (compressor on Music keyed by a loud bus) and HDR-style windowing.
- Per-source air absorption (a distance-driven cutoff per player beyond Godot's single attenuation filter).
- Interior/exterior engine layers for one's own car.
