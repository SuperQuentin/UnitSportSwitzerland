using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Player;

namespace UnitSport.Trailer;

/// <summary>A place in LV95 and a height: above the ground (<paramref name="Agl"/>) or above the sea.</summary>
public readonly record struct Spot(double E, double N, float H = 0f, bool Agl = true)
{
    /// <summary>The same place, <paramref name="h"/> above the sea.</summary>
    public static Spot Alt(double e, double n, float h) => new(e, n, h, Agl: false);

    /// <summary><paramref name="metres"/> along a compass bearing (0 north, 90 east), same height.</summary>
    public Spot Toward(float bearing, float metres) =>
        this with { E = E + metres * Math.Sin(Mathf.DegToRad(bearing)), N = N + metres * Math.Cos(Mathf.DegToRad(bearing)) };

    public Spot Up(float h) => this with { H = h };
}

/// <summary>
/// A point a camera key stands at or looks at: a world <see cref="Spot"/>, a point in an actor's
/// travel frame (right, up, back: metres, back = behind it), or for a look only, a compass
/// direction from the eye.
/// </summary>
public sealed record Pt
{
    public Spot? World { get; private init; }
    public int Actor { get; private init; } = -1;
    public Vector3 Offset { get; private init; }
    public float Bearing { get; private init; } = float.NaN;
    public float Pitch { get; private init; }

    public static implicit operator Pt(Spot s) => new() { World = s };
    public static Pt At(double e, double n, float agl) => new() { World = new Spot(e, n, agl) };
    public static Pt Alt(double e, double n, float alt) => new() { World = Spot.Alt(e, n, alt) };

    /// <summary>In actor <paramref name="actor"/>'s frame: metres to its right, above its feet, behind it.</summary>
    public static Pt On(int actor, float right = 0f, float up = 1f, float back = 0f) =>
        new() { Actor = actor, Offset = new Vector3(right, up, back) };

    /// <summary>
    /// On the shot's road <paramref name="route"/>, <paramref name="arc"/> m along it (counted like
    /// <see cref="Cast.Arc"/>), <paramref name="right"/> m to the right of its way, <paramref name="up"/> m
    /// over the ground: a camera at a bend the cars will reach.
    /// </summary>
    public static Pt Road(string route, float arc, float right = 0f, float up = 1.5f) =>
        new() { Route = route, Offset = new Vector3(right, up, arc) };

    public string? Route { get; private init; }

    /// <summary>
    /// From actor <paramref name="actor"/>'s own seat: its ride's first-person eye
    /// (<see cref="Rideable.FirstPersonEye"/>), or <paramref name="ahead"/> m ahead of it along the way
    /// its body faces: the driver's view through the windscreen.
    /// </summary>
    public static Pt Cockpit(int actor, float ahead = 0f, float up = 0f) => new() { Actor = actor, Seat = true, Offset = new Vector3(0f, up, ahead) };

    public bool Seat { get; private init; }

    /// <summary>A look along a compass bearing (0 north, 90 east), pitched up (+) or down (−), degrees.</summary>
    public static Pt Dir(float bearing, float pitch = 0f) => new() { Bearing = bearing, Pitch = pitch };

    public bool IsDirection => !float.IsNaN(Bearing);

    /// <summary>A point of the shot's set (<see cref="Shot.Set"/>), in its own metres (the dormitory's: see <see cref="DormSet"/>).</summary>
    public static Pt Set(float x, float y, float z) => new() { InSet = new Vector3(x, y, z) };

    public Vector3? InSet { get; private init; }
}

/// <summary>
/// A camera key: where the eye is and what it looks at, <paramref name="T"/> seconds into the shot
/// (or into its <see cref="Shot.KeysFrom"/> path), through a lens of <paramref name="Lens"/> mm
/// (full frame), rolled <paramref name="Roll"/> degrees.
/// </summary>
public sealed record Key(double T, Pt Eye, Pt Look, float Lens = 35f, float Roll = 0f);

/// <summary>A line of text over the picture from <paramref name="T"/> for <paramref name="Seconds"/>.</summary>
public sealed record Caption(double T, double Seconds, string Text, string? Sub = null, bool Title = false)
{
    /// <summary>The subtitles' id of <see cref="Text"/> (and of <see cref="Sub"/>, <see cref="SubId"/>); null: the slug of its English (<see cref="SubtitleTrack.Slug"/>).</summary>
    public string? Id { get; init; }
    public string? SubId { get; init; }
}

/// <summary>
/// A comic speech bubble (#862): actor <paramref name="Who"/> of the shot says
/// <paramref name="Text"/> from <paramref name="T"/> s into the shot, its tail on that actor's head
/// (projected through the shot's camera every frame), the text typing in. It stays
/// <see cref="Seconds"/>, by default long enough to read (<see cref="Ui.Bubbles.BubbleGeometry.ReadingTime"/>),
/// never past the shot's end. Bubbles on screen together never overlap.
/// <code>
/// Bubbles =
/// [
///     new(0.5, 0, "Who took the fondue?!", BubbleShape.Shout),
///     new(2.0, 1, "Not me...", BubbleShape.Whisper) { Seconds = 2 },
///     new(3.0, 2, "Mmm. Cheese.", BubbleShape.Think),
///     new(3.5, -1, "Hello?") { At = new(0.92f, 0.3f) },   // a voice off: the tail to a point of the frame
/// ],
/// </code>
/// </summary>
public sealed record Bubble(double T, int Who, string Text, Ui.Bubbles.BubbleShape Shape = Ui.Bubbles.BubbleShape.Say)
{
    /// <summary>Seconds on screen; NaN: long enough to type and read it.</summary>
    public double Seconds { get; init; } = double.NaN;
    /// <summary>A point of the frame the tail goes to instead of an actor's head (0..1 across, 0..1 down): a voice off, a radio.</summary>
    public Vector2? At { get; init; }
    /// <summary>A tint washed into the white; null: the speaker's character colour (<see cref="Character.Colour"/>), white for nobody in particular.</summary>
    public Color? Colour { get; init; }
    /// <summary>The subtitles' id; null: the slug of its English (<see cref="SubtitleTrack.Slug"/>).</summary>
    public string? Id { get; init; }

    /// <summary>When it goes, shot time: its <see cref="Seconds"/> or its reading time, at most the shot's end.</summary>
    public double Until(double shotLength) =>
        Math.Min(shotLength, T + (double.IsNaN(Seconds) ? Ui.Bubbles.BubbleGeometry.ReadingTime(Text.Length, Shape) : Seconds));
}

/// <summary>
/// A person in a story: the name the chat shows in <paramref name="Colour"/>, a face and build
/// (<paramref name="Looks"/>), and the clothes they wear (<see cref="Avatar.Garments"/> items), the
/// same in every shot so they read as the same person.
/// </summary>
public sealed record Character(string Name, Color Colour, Avatar.Appearance Looks, Items.ItemId[] Wears)
{
    public int AppearanceBits => Looks.Pack();
    public long OutfitBits => Avatar.Outfit.Of(Wears).Bits;
}

/// <summary>
/// A line of the in-game chat as dialogue, typed in at <paramref name="T"/>; a null speaker is the
/// game's own system line ("PIGEON joined the game"). Lines stay up to the end of their shot.
/// </summary>
public sealed record Line(double T, Character? Who, string Text)
{
    /// <summary>The subtitles' id; null: the slug of its English (<see cref="SubtitleTrack.Slug"/>).</summary>
    public string? Id { get; init; }
}

/// <summary>A documentary place and time card ("AIROLO · 08:15"), top left, from <paramref name="T"/> for <paramref name="Seconds"/>.</summary>
public sealed record Super(double T, double Seconds, string Text);

/// <summary>What moves an actor.</summary>
public enum Drive
{
    /// <summary>Stands (or sits on its machine) where it was put.</summary>
    Stand,
    /// <summary>The race autopilot along its road (<see cref="AutoPilot.For"/>): cars, lean-steered mounts, runners.</summary>
    Road,
    /// <summary>Scripted ground controls, <see cref="Cast.Controls"/>.</summary>
    Controls,
    /// <summary>Launched into the air at <see cref="Cast.Launch"/> m/s, then <see cref="Cast.Flight"/>.</summary>
    Fly,
    /// <summary>On foot, <see cref="Cast.Walk"/>.</summary>
    Walk,
    /// <summary>
    /// Along its road at <see cref="Cast.Speed"/>, steered at a point ahead (pure pursuit): any
    /// ground machine, trucks and buses too, which have no race autopilot.
    /// </summary>
    Follow,
}

/// <summary>
/// One actor of a shot: a figure, on foot or on a machine, put at a spot (or on the road nearest it)
/// and moved by its <see cref="Drive"/>. Times given to the scripts are seconds since the actors
/// started moving (the pre-roll included).
/// </summary>
public sealed record Cast
{
    public RideKind Ride { get; init; } = RideKind.OnFoot;
    public required Spot At { get; init; }
    /// <summary>Compass bearing it faces, degrees (on a road: which way along it, roughly).</summary>
    public float Heading { get; init; }
    public Drive Drive { get; init; } = Drive.Stand;

    /// <summary>On a road: metres along it from the point nearest <see cref="At"/> (negative: behind).</summary>
    public float Arc { get; init; }
    /// <summary>On a road: which road. Actors naming the same one share it (built once, from the first one's spot).</summary>
    public string? Route { get; init; }
    /// <summary>On a road: a point it drives toward, else the way it faces.</summary>
    public Spot? Toward { get; init; }
    /// <summary>The autopilot's skill (1 = a good driver) and aggression.</summary>
    public float Skill { get; init; } = 1f;
    public float Aggression { get; init; } = 0.2f;
    /// <summary>The autopilot starts only after this many seconds (a grid start, a car that waits).</summary>
    public double GoAt { get; init; }

    /// <summary>Metres a second it holds along its road (<see cref="Drive.Follow"/>).</summary>
    public float Speed { get; init; } = 12f;
    /// <summary>On a road (<see cref="Drive.Follow"/>): it stops this many metres along it from its spot (the end of a road).</summary>
    public float StopAt { get; init; } = float.NaN;

    public Func<double, RideInput>? Controls { get; init; }
    public Func<double, FlightInput>? Flight { get; init; }
    /// <summary>Metres a second along <see cref="Heading"/> when launched (<see cref="Drive.Fly"/>), or put on the water.</summary>
    public float Launch { get; init; }
    /// <summary>Launched climbing (+) or diving (−) this many degrees.</summary>
    public float Climb { get; init; }
    /// <summary>A trailer coupled behind its truck (<see cref="TrailerCatalog"/> index), −1 = none.</summary>
    public int Trailer { get; init; } = -1;
    /// <summary>On foot: a wish in its own frame (x right, y ahead, length ≤ 1) and whether it runs.</summary>
    public Func<double, (Vector2 Wish, bool Run)>? Walk { get; init; }

    /// <summary>A dance move or emote (<see cref="FootPlayer.DanceId"/>), 0 = none.</summary>
    public int Dance { get; init; }
    /// <summary>The seed of its looks (<see cref="Avatar.Appearance.ForSeed"/>), when it plays nobody in particular.</summary>
    public int Seed { get; init; } = 1;
    /// <summary>Who it plays: the looks and clothes of a story's character.</summary>
    public Character? Who { get; init; }
    /// <summary>Comes out of the nearest front door to <see cref="At"/>: put on its step facing out, the door opened.</summary>
    public bool FromDoor { get; init; }
    /// <summary>
    /// Aboard actor <see cref="Aboard"/>'s machine: put at <see cref="Deck"/> in its frame (x right,
    /// y up, −z ahead) once it is under way, moving with it (the steamer's deck, the freighter's hold);
    /// its <see cref="Heading"/> is then from the machine's (180: facing aft). It walks, an NPC never boards.
    /// </summary>
    public int Aboard { get; init; } = -1;
    public Vector3 Deck { get; init; }
    public bool Lights { get; init; }
    /// <summary>A car preset (<see cref="CarSetups"/>), 0 = none.</summary>
    public int Setup { get; init; }
    /// <summary>Seen from its own seat (<see cref="Pt.Cockpit"/>): its machine draws the cockpit, as for its driver.</summary>
    public bool FirstPerson { get; init; }
    /// <summary>What it holds (<see cref="Items.ItemId"/>), 0 = nothing.</summary>
    public int Item { get; init; }
    /// <summary>When it uses what it holds (<see cref="FootPlayer.ItemAction"/> 2): a drink goes to the mouth.</summary>
    public Func<double, bool>? Use { get; init; }
    /// <summary>
    /// In the shot's set (<see cref="Shot.Set"/>) at this point of it, facing <see cref="Heading"/>
    /// (0 = the set's −Z): inside, as a player in an interior, the terrain's rules off.
    /// </summary>
    public Vector3? InSet { get; init; }
    /// <summary>Its machine's doors (<see cref="FootPlayer.DoorsOpen"/>): the freighter's ramp is bit 3.</summary>
    public byte Doors { get; init; }
    /// <summary>A boat: a dry spot to get in at, before it is put on the water at <see cref="At"/>. A flyer
    /// (<see cref="Drive.Fly"/>) launched over water: where it mounts before it is launched at <see cref="At"/>.</summary>
    public Spot? Board { get; init; }
    /// <summary>A boat: how deep its body is put under the surface, m (the steamer floats at 1.6).</summary>
    public float Draught { get; init; } = 0.2f;

    // ---- the film kit (#882) ---------------------------------------------------------------------

    /// <summary>
    /// On foot (<see cref="Drive.Walk"/>): jump and crouch held over time, as the keys would be
    /// (<see cref="FootPlayer.FootActions"/>; a jump is a press, false then true): a slide is a crouch
    /// at a run, a wall jump a jump in the air at a wall, a second jump falling high up opens the
    /// wingsuit, then flown by <see cref="Flight"/>. Such a walker is an NPC body (it reads these, not the keys).
    /// </summary>
    public Func<double, (bool Jump, bool Crouch)>? Actions { get; init; }
    /// <summary>
    /// A fist fight's stance or move over time (<see cref="FootPlayer.FightPose"/>: 1 stand, 2 guard,
    /// 8 dazed, 9 victory; 16 + a <c>FightMove</c>: 17 jab, 18 kick, 21 jump kick, 22 uppercut), 0 = none.
    /// A move plays once from when the value changes.
    /// </summary>
    public Func<double, int>? Fight { get; init; }
    /// <summary>What it wears on its head (<see cref="FootPlayer.HeadwearId"/>, <see cref="Avatar.Headwear"/>: 1 witch hat, 2 pumpkin head, 3 Santa hat, 4 antlers), 0 = bare.</summary>
    public int Headwear { get; init; }
    /// <summary>A farm machine's implement or header let down (<c>Truck.Lowered</c>): the plough in the ground, the combine's header.</summary>
    public bool Lowered { get; init; }
    /// <summary>The radio in its hand plays this CD (<see cref="FootPlayer.HeldRadio"/>, 1 = the first bundled one): it sparkles and bounces to the beat. 0 = off. Hold <c>Item = Radio</c>.</summary>
    public int Radio { get; init; }
    /// <summary>
    /// Swims at <see cref="At"/> (water), stroking with <see cref="Walk"/>: put down on the dry
    /// <see cref="Board"/> spot (or beside the water when there is none), then at the surface at its spot.
    /// </summary>
    public bool Swim { get; init; }
    /// <summary>A saucer actor (<c>Ride = RideKind.Ufo</c>): its beam, lamps, ramp and legs over time (<see cref="UfoSwitches"/>).</summary>
    public Func<double, UfoSwitches>? Ufo { get; init; }
    /// <summary>
    /// Walks down the ramp of a landed saucer: an actor's (<see cref="RampOf.Actor"/>) or one of the
    /// shot's <see cref="Shot.Craft"/> (<see cref="RampOf.Craft"/>). Put at the hatch, facing down the
    /// ramp, which is solid under it for the shot; give it a <see cref="Walk"/> (ahead) to come down.
    /// </summary>
    public RampOf? DownRamp { get; init; }
}

/// <summary>
/// A plain box standing on the ground (a wall to crash into), <paramref name="Bearing"/> its long
/// side's normal; or, with <see cref="Actor"/>, <see cref="Ahead"/> m in front of that actor once it
/// is placed, square across its way; or across a road at an arc.
/// </summary>
public sealed record Prop(Spot At, float Bearing, Vector3 Size, Color Colour)
{
    /// <summary>An item set down instead of a box (the fondue pot, the radio, the flag planted), <see cref="Scale"/> times its size.</summary>
    public Items.ItemId Item { get; init; }
    public float Scale { get; init; } = 1f;
    public int Actor { get; init; } = -1;
    public float Ahead { get; init; }
    /// <summary>Or on the shot's road <see cref="Route"/>, <see cref="Arc"/> m along it, square across it.</summary>
    public string? Route { get; init; }
    public float Arc { get; init; }
    /// <summary>Or in the shot's set, at this point of it (an item rests on whatever is under it there: the table).</summary>
    public Vector3? InSet { get; init; }
    /// <summary>A character sitting there instead, the hip at the spot, facing <see cref="Bearing"/>: a still figure, as a passenger sits.</summary>
    public Character? Seated { get; init; }
    /// <summary>A placed object's look instead (<see cref="Items.PlacedKind"/>: a burning campfire, a workbench, a camo net), drawn only, never placed or saved.</summary>
    public Items.PlacedKind Placed { get; init; }
    /// <summary>A built piece instead (a hammer's wall or floor, <see cref="Build.StructureMeshes"/>) in <see cref="Material"/>, solid, standing on the ground.</summary>
    public Build.PieceKind? Piece { get; init; }
    public Build.BuildMaterial Material { get; init; }
    /// <summary>
    /// A real parked vehicle of this kind instead (<see cref="Vehicles.VehicleManager.Place"/>, taken
    /// away after the shot): a tractor beam lifts it (12 m long at most). <see cref="Setup"/> picks a car's preset.
    /// </summary>
    public RideKind Vehicle { get; init; }
    public int Setup { get; init; }
    /// <summary>A tractor beam over it lifts it (an item, a box, a seated figure, a placed look): the beam is its cue.</summary>
    public bool Beamable { get; init; }
}

/// <summary>
/// A UFO sighting staged in a shot (#863): saucers of the game's own sightings
/// (<see cref="Occasions.UfoSightings.Stage"/>), flying their scripted path
/// (<see cref="Occasions.SightingPath"/>) about <paramref name="At"/>: the point a flyover crosses,
/// the hover point of an abduction (put a <see cref="Cows"/> herd there: the beam lifts what is under
/// it), the landing spot, the point under the mothership (its <see cref="Spot.H"/> raises the whole
/// path). <paramref name="Heading"/> is the compass bearing they come in on, degrees.
/// <paramref name="From"/> is where the sighting's own clock stands when the camera rolls, s: 0
/// starts it with the roll, 24 rolls on an abduction's beam coming on, 30 on a landed saucer with
/// its ramp down, negative starts it that much later (the timings are
/// <see cref="Occasions.SightingPath"/>'s constants).
/// </summary>
public sealed record Saucers(Occasions.SightingKind Kind, Spot At, float Heading = 0f, int Count = 1, double From = 0, uint Seed = 1);

/// <summary>
/// A herd staged in a shot (#863): <paramref name="Count"/> cows of <paramref name="Breed"/> grazing
/// within <paramref name="Radius"/> m of <paramref name="At"/> (<see cref="Farming.Herds.Stage"/>),
/// from the shot's staging on, as the pasture herds do (a beam lifts them too).
/// </summary>
public sealed record Cows(Spot At, int Count = 8, Farming.CowBreed Breed = Farming.CowBreed.Brown, float Radius = 18f);

/// <summary>
/// One shot of the trailer: where and when, who is in it, how the camera moves, what it says.
/// It runs from the start of bar <see cref="FromBar"/> for <see cref="Bars"/> bars of the song.
/// </summary>
public sealed record Shot
{
    public required int Number { get; init; }
    public required string Name { get; init; }
    public required int FromBar { get; init; }
    public required int Bars { get; init; }
    /// <summary>The feature it shows, for the log and the storyboard.</summary>
    public string Feature { get; init; } = "";

    /// <summary>Hour of the day when it rolls (pre-roll included).</summary>
    public double Hour { get; init; } = 11;
    /// <summary>A whole day lasts this many real minutes during the shot (a time-lapse); null: the clock stands.</summary>
    public float? MinutesPerDay { get; init; }
    public string Style { get; init; } = "ps1";
    /// <summary>The sea state for the lakes, 0..1; null: <see cref="CalmSea"/>.</summary>
    public float? Sea { get; init; }
    public const float CalmSea = 0.15f;
    /// <summary>Traffic cars around the camera; null: the player's setting.</summary>
    public int? Traffic { get; init; }

    public IReadOnlyList<Cast> Cast { get; init; } = Array.Empty<Cast>();
    public IReadOnlyList<Prop> Props { get; init; } = Array.Empty<Prop>();
    /// <summary>UFO sightings in the shot (#863), on the sightings' own clock from the roll.</summary>
    public IReadOnlyList<Saucers> Ufos { get; init; } = Array.Empty<Saucers>();
    /// <summary>Herds of cows in the shot (#863), grazing from the staging on.</summary>
    public IReadOnlyList<Cows> Herds { get; init; } = Array.Empty<Cows>();
    public required IReadOnlyList<Key> Keys { get; init; }
    /// <summary>Key time at the shot's first frame: shots that carry one move on each start further along it.</summary>
    public double KeysFrom { get; init; }
    /// <summary>The camera's damping, s (0: none): an eye on a bouncing car rides smoothly.</summary>
    public float Smooth { get; init; }
    /// <summary>A handheld drift of the camera, degrees.</summary>
    public float Shake { get; init; }
    /// <summary>Seconds the actors move before the camera rolls (cars up to speed).</summary>
    public double Preroll { get; init; } = 2;

    public IReadOnlyList<Caption> Captions { get; init; } = Array.Empty<Caption>();
    public IReadOnlyList<Line> Chat { get; init; } = Array.Empty<Line>();
    /// <summary>Comic speech bubbles over the actors' heads (#862), the films' dialogue.</summary>
    public IReadOnlyList<Bubble> Bubbles { get; init; } = Array.Empty<Bubble>();
    public IReadOnlyList<Super> Supers { get; init; } = Array.Empty<Super>();
    /// <summary>
    /// A film set built for the shot (the dormitory, <see cref="DormSet.Build"/>): put far under
    /// <see cref="SetAt"/> (the place it stands for), lit by its own lamps; the camera's
    /// <see cref="Pt.Set"/> points, the actors' <see cref="Cast.InSet"/> and props are in it.
    /// </summary>
    public Func<Godot.Node3D>? Set { get; init; }
    public Spot SetAt { get; init; }
    /// <summary>
    /// A film's moonlight at night (0 = none): a soft blue light from high in the south, this
    /// strong, over the whole place, so what the game's night leaves black reads on screen. The
    /// lamps and lit windows stay what they are.
    /// </summary>
    public float Moon { get; init; }
    /// <summary>A photo taken at this shot time: a flash, then the frame held as a polaroid to the end.</summary>
    public double? Photo { get; init; }
    /// <summary>Where the photo is taken from (the photographer's view, the film's camera elsewhere); null: the film's frame.</summary>
    public Key? PhotoFrom { get; init; }
    /// <summary>The photo's picture from this PNG instead of a render (a screenshot of one of the game's screens).</summary>
    public string? PhotoFile { get; init; }

    // ---- the film kit (#882) ---------------------------------------------------------------------

    /// <summary>
    /// The occasions over the world for the shot: <c>halloween</c>, <c>christmas</c>, <c>ufo</c> or
    /// several (<c>"christmas,ufo"</c>), exactly those whatever the calendar says; <c>none</c>: no
    /// occasion at all; null: the calendar's, as the game runs (Halloween runs all of September and
    /// October). The run ends with the calendar's.
    /// </summary>
    public string? Occasion { get; init; }
    /// <summary>A Battle Royale zone's wall (<see cref="BattleRoyale.ZoneWall"/>) standing on this circle (centre, radius m), no match needed.</summary>
    public (Spot Centre, float Radius)? Zone { get; init; }
    /// <summary>Item effects on cue at an actor's hand (<see cref="ItemCue"/>): an alphorn's call, a fondue, a flare, smoke, a fishing cast.</summary>
    public IReadOnlyList<ItemCue> ItemCues { get; init; } = Array.Empty<ItemCue>();
    /// <summary>One of the game's own screens over the frame on cue (<see cref="ScreenCue"/>): the map, the landing picker, a PNG of another.</summary>
    public IReadOnlyList<ScreenCue> Screens { get; init; } = Array.Empty<ScreenCue>();
    /// <summary>UFO props (<see cref="Trailer.Craft"/>): a saucer landed, crashed or hovering, the mothership; their beams lift what is under them.</summary>
    public IReadOnlyList<Craft> Craft { get; init; } = Array.Empty<Craft>();

    /// <summary>Seconds of fade from black at the start and to black at the end.</summary>
    public double FadeIn { get; init; }
    public double FadeOut { get; init; }

    public double Start => Song.Bar(FromBar);
    public double End => Math.Min(Song.Bar(FromBar + Bars), Song.End);
    /// <summary>The film's song, whose bars the shot is cut on.</summary>
    public Song Song { get; init; } = Song.VoxelRevolution;
    public double Length => End - Start;
}
