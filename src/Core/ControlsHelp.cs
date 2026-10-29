using Godot;

namespace UnitSport.Core;

/// <summary>
/// Every control in the game on one screen, opened with F1 (or the main menu's Controls button).
///
/// <para>
/// Built from the live <see cref="InputMap"/> through <see cref="InputHints"/>, with a keyboard
/// and a pad column side by side, so it is the actual layout and not a copy of it that can go
/// stale — the keyboard column even prints the letters of the player's own layout. It only reads,
/// so it does not take the input: you can keep playing with it open.
/// </para>
/// </summary>
public partial class ControlsHelp : CanvasLayer
{
    private PanelContainer _panel = null!;
    private GridContainer _columns = null!;

    public bool IsOpen => _panel.Visible;

    public static ControlsHelp Create() => new() { Name = "ControlsHelp" };

    /// <summary>A row: what it does, and either an action or fixed labels (keyboard, pad) for things that are not one.</summary>
    internal readonly record struct Row(string What, string? Action = null, string? Keys = null, string? Pad = null);

    /// <summary>The four movement keys, as printed on this keyboard (Z Q S D on AZERTY).</summary>
    private const string Wasd = "{move_forward} {move_left} {move_back} {move_right}";

    /// <summary>Every control by group; also the phone's More sheet (#1043, <see cref="TouchMore"/>).</summary>
    internal static readonly (string Title, Row[] Rows)[] Groups =
    {
        ("On foot", new Row[]
        {
            new("Move", Keys: Wasd, Pad: "{move_forward}"),
            new("Look", Keys: "Mouse", Pad: "{look_right}"),
            new("Jump / wall jump / mantle", PlayerInput.Jump),
            new("Run", PlayerInput.Sprint),
            new("Slide (while running)", PlayerInput.CrouchSlide),
            new("Interact: get in or out, search, door", PlayerInput.InteractMount),
            new("Elevator: call it at its button, choose a floor inside", PlayerInput.InteractMount),
            new("Travel menu: mounts and vehicles", PlayerInput.RideMenu, Pad: "{interact_mount} (nothing near)"),
            new("First / third person (driving: chase, cockpit, cockpit without your body)", PlayerInput.CameraToggle),
            new("Camera over the other shoulder", PlayerInput.SwapShoulder, Pad: "R3 (aiming a gun)"),
            new("Third person: camera zoom in / out, down to the face (Shift or not)", Keys: "{view_zoom_in} / {view_zoom_out}", Pad: "R3 (hold) + right stick ↑ ↓"),
            new("Base jump: jump again while falling", PlayerInput.Jump),
            new("Pogo at a concert: dancing, jump on the beat", PlayerInput.Jump),
            new("Pinball: E at a machine steps up to it and presses Start (again to add a player in the first ball); Esc steps back", PlayerInput.InteractMount),
            new("Pinball: the flippers (A / D, the arrows, or Left / Right Shift)", Keys: "{pinball_left} / {pinball_right}", Pad: "LB / RB (or LT / RT)"),
            new("Pinball: hold to pull the plunger, let go to launch (Space, S or Enter)", PlayerInput.PinballPlunge),
            new("Pinball: nudge the cabinet (too hard, too often: tilt); Q / R to the sides, the pad's left stick sideways", PlayerInput.PinballNudge),
            new("Pinball: the player's view or straight down on the playfield", PlayerInput.CameraToggle),
            new("Bowling: E at the shoe counter, a ball rack, the ball return, then on the approach to bowl", PlayerInput.InteractMount),
            new("Bowling: move your spot", PlayerInput.MoveLeft),
            new("Bowling: turn your line", PlayerInput.BowlAimLeft),
            new("Bowling: hold to swing, sideways for hook, let go to roll", PlayerInput.Jump),
            new("Festival alphorn row: sit down to the song; blow, pick the note, yodel", Keys: "{interact_mount}, hold {use_item}, wheel or {move_forward} / {move_back}, {jump}", Pad: "{interact_mount}, hold {use_item}, right stick ↑ ↓, {jump}"),
        }),
        ("Items", new Row[]
        {
            new("Use the item in hand", PlayerInput.UseItem),
            new("Aim (binoculars, camera, shotgun); with anything else, a throw", PlayerInput.AimItem),
            new("Throw: hold Aim, hold Use to wind up, let go", Keys: "{aim_item} + {use_item}", Pad: "{aim_item} + {use_item}"),
            new("Drop the item in hand (Ctrl: the whole stack)", PlayerInput.DropItem),
            new("Pick up what you point at; a radio: tap to switch it on / off, hold to open it", PlayerInput.InteractMount),
            new("Radio in hand: tap to switch it on / off, hold to open it; aim + hold to throw it", PlayerInput.UseItem),
            new("Take the radio you point at into your hand", PlayerInput.UseItem),
            new("Pick a hotbar slot (the plain wheel zooms the camera)", Keys: "1–6 / {next_item} / {prev_item}", Pad: "{next_item} (tap)"),
            new("Put the item in hand away / take it out (also the number of the slot in hand; it goes away by itself after 8 s idle: Settings › Gameplay)", Keys: "{holster_item}", Pad: "{next_item} (hold)"),
            new("Quick wheel (hold)", PlayerInput.QuickWheel),
            new("Emote wheel: dances and gestures (hold; tap: stop / again)", PlayerInput.EmoteWheel),
            new("Inventory", PlayerInput.Inventory, Pad: "{inventory} (tap)"),
            new("Gather stone, water, wood (hold)", PlayerInput.Gather),
            new("Journal: quests, birds, fish (tabs: Q / E)", PlayerInput.BirdJournal, Pad: "{quick_menu} (hold) › Journal; LB / RB for the tabs"),
            new("Talk to an errand giver (a figure at a post office, hut, station or landing): take the day's cards, hand cargo over", PlayerInput.InteractMount),
            new("Character: your figure (on foot)", PlayerInput.CharacterMenu, Pad: "{quick_menu} (hold) › Character, or {menu} › Character"),
        }),
        ("Adventure: navigation and travel (#1226)", new Row[]
        {
            new("Guiding wind toward what you track (on foot, mounted)", PlayerInput.Guide, Pad: "{quick_menu} (hold) › Guide"),
            new("Departure board at a station, stop, landing or cable-car sign", PlayerInput.InteractMount),
            new("Board: pick a destination, travel; type to search", Keys: "↑ ↓, Enter", Pad: "D-pad, A"),
            new("Map: place a pin / edit a pin", Keys: "RMB", Pad: "X at the centre cross"),
            new("Map: pin icon", Keys: "1–6", Pad: "D-pad, A"),
            new("Map: track a place, a pin, a travel point", Keys: "Track (click)", Pad: "A on it, then Track"),
        }),
        ("Festivals: the Älplerfest's stone put (#1269)", new Row[]
        {
            new("Put the stone at its ground (an open Älplerfest): lift it", PlayerInput.InteractMount),
            new("Lift: hold, let go on the meter's peak; then once on each beat of the drum, the last is the push (look up to about 40 degrees)", PlayerInput.UseItem),
            new("Another stone while you are lifting it", PlayerInput.Jump),
        }),
        ("Festivals: the Désalpe cow drive (#1269)", new Row[]
        {
            new("At a cow of the herd on the alp: put on the next headdress shape (the alp sign starts the drive, the fair's ring gives the vote)", PlayerInput.InteractMount),
            new("At a cow: the next colour; in the fair's ring: the next herd to vote for", PlayerInput.UseItem),
        }),
        ("Festivals: the Christmas market (#1348)", new Row[]
        {
            new("At a market stall: the day's free taste (the stall-hop), then buy its ware for Batzen; at the Samichlaus on 6 December: his bag", PlayerInput.InteractMount),
        }),
        ("Festivals: the Escalade (#1351)", new Row[]
        {
            new("At the Marmite: smash it (the party's youngest and oldest both press); at the soup pot: ladle a bowl for the party; at the start line: begin the Course de l'Escalade", PlayerInput.InteractMount),
        }),
        ("Festivals: the Sechseläuten (#1358)", new Row[]
        {
            new("At the Böögg's pyre before it is lit: change the minute you bet on", PlayerInput.UseItem),
            new("At the pyre: place your bet; after the bang: grill a Cervelat on the embers; at the Zunft's Weibel: take the day's purse", PlayerInput.InteractMount),
        }),
        ("Festivals: the Chalandamarz (#1356)", new Row[]
        {
            new("At the Treichel stand: begin the procession; then, walking the village loop, ring the Treichel on the march beat (the drum and the HUD's pulse); rings in step with a party member score extra", PlayerInput.InteractMount),
        }),
        ("Festivals: the 1 August (#1355)", new Row[]
        {
            new("At the lampion stand: a lampion; at the fireworks stand: a box for Batzen; at the lampion post at night: begin the lampion walk with your party", PlayerInput.InteractMount),
            new("With a box of fireworks in hand, at night: light it", PlayerInput.UseItem),
        }),
        ("Quick menu and invites (#840)", new Row[]
        {
            new("Quick menu: activities, invites, inventory, vehicles, map, emotes (hold, aim, let go)", PlayerInput.QuickMenu, Pad: "{quick_menu} (hold)"),
            new("Phone: take it out, again to open it; out, WASD and the mouse work it (click, swipe), Enter selects, Esc / Backspace / right click goes back, hold Esc puts it away", PlayerInput.Phone, Pad: "{quick_menu} (hold) › Phone"),
            new("Accept the invite on screen", PlayerInput.QuickMenu, Keys: "{quick_menu} (tap)", Pad: "{menu}, then A"),
            new("Decline the invite on screen", PlayerInput.InviteDecline, Pad: "{quick_menu} (hold) › Invites"),
        }),
        ("Fishing (rod in hand)", new Row[]
        {
            new("Wind up a cast (hold), cast (let go)", PlayerInput.UseItem),
            new("Strike when the float dips", PlayerInput.UseItem),
            new("Reel in (hold); let go when the line strains or the fish runs", PlayerInput.UseItem),
            new("Wind the line in; cancel a cast", PlayerInput.AimItem),
        }),
        ("Flag swinging at the Älplerfest (#1269)", new Row[]
        {
            new("Step onto the flag ground and swing the flag", PlayerInput.InteractMount),
            new("Draw the figure shown (up, down, a sweep, a circle, an eight) before its last beat", Keys: "Mouse", Pad: "Right stick"),
            new("High throw: hold, let go on a beat; catch on the beat it lands", PlayerInput.UseItem),
        }),
        ("Fist fight (#495)", new Row[]
        {
            new("Challenge a player you look at, or accept their challenge", PlayerInput.InteractMount),
            new("Step towards / away", Keys: "{move_left} {move_right}", Pad: "{move_right}"),
            new("Jump / crouch", Keys: "{move_forward} {jump} / {move_back} {crouch_slide}", Pad: "{move_forward} / {move_back}"),
            new("Punch", PlayerInput.FightPunch),
            new("Kick", PlayerInput.FightKick),
            new("Block (hold; crouch to block low)", PlayerInput.FightBlock),
            new("Uppercut: down, forward + punch", Keys: "{move_back} {move_right} + {fight_punch}", Pad: "{move_back} {move_right} + {fight_punch}"),
            new("String: punch, punch, kick", Keys: "{fight_punch} {fight_punch} {fight_kick}", Pad: "{fight_punch} {fight_punch} {fight_kick}"),
            new("Finish them: down, down + kick", Keys: "{move_back} {move_back} + {fight_kick}", Pad: "{move_back} {move_back} + {fight_kick}"),
        }),
        ("Schwingen (#1269, the Älplerfest ring, /schwing <player or bot>)", new Row[]
        {
            new("Join the ring's queue (at the sawdust ring), or give the place up", PlayerInput.InteractMount),
            new("Lean (towards / away / round)", Keys: "{move_left} {move_right} {move_forward} {move_back}", Pad: "{move_right}"),
            new("Throw (the lean picks which)", PlayerInput.FightPunch),
            new("Leg hook (Gammen)", PlayerInput.FightKick),
            new("Brace against a throw (hold), or lean its counter", PlayerInput.FightBlock),
            new("On the ground: mash to pin or to get out", PlayerInput.FightPunch),
            new("Leave: /schwing leave", Keys: "/schwing", Pad: "/schwing"),
        }),
        ("Aura battle (#907, /aura <player>)", new Row[]
        {
            new("Planner: add the move picked", Keys: "click, or arrows + {ui_accept}", Pad: "{ui_accept}"),
            new("Planner: take back the last move", PlayerInput.AuraRemove),
            new("Planner: suggest a combo", PlayerInput.AuraSuggest),
            new("Planner: lock in", PlayerInput.AuraLock),
            new("Next move, on the downbeat (the timing scores)", PlayerInput.AuraNext),
            new("Rival performing: keep a straight face when tempted (only then!)", PlayerInput.AuraNext),
            new("Accept a player's aura challenge: look at them", PlayerInput.InteractMount),
            new("Battle a bot: /aura <bot's name>", Keys: "/aura", Pad: "/aura"),
            new("Cheer a battle you watch (emote wheel: Cheer, Clap, Fist pump)", PlayerInput.EmoteWheel),
        }),
        ("Building (hammer in hand)", new Row[]
        {
            new("Build the piece shown", PlayerInput.UseItem),
            new("Take back your own piece", Keys: "{aim_item} + {use_item}", Pad: "{aim_item} + {use_item}"),
            new("Next piece", Keys: "{aim_item} + {next_item}", Pad: "{aim_item} + {next_item}"),
            new("Turn the piece", PlayerInput.BuildTurn),
            new("Change the material", Keys: "{aim_item} + {build_turn}", Pad: "{aim_item} + {build_turn}"),
        }),
        ("Riding and driving", new Row[]
        {
            new("Throttle / pedal", PlayerInput.Throttle),
            new("Brake / reverse", PlayerInput.Brake),
            new("Steer", Keys: "{move_left} {move_right}", Pad: "{move_forward}"),
            new("Tuck / sprint effort; motorbike: pull a wheelie (tap on the gas: clutch pop)", PlayerInput.TuckBoost),
            new("Hop / handbrake", PlayerInput.Jump),
            new("Trick in the air (hold + stick); not on the pad in a car you shift yourself", PlayerInput.Trick),
            new("Boost; not on the pad in a car you shift yourself", PlayerInput.Boost),
            new("Look behind", PlayerInput.LookBehind),
            new("Engine on / off", PlayerInput.EngineToggle),
            new("Car: headlights / pop-ups", PlayerInput.LightsToggle),
            new("Car, sequential or manual gearbox (Settings → Vehicles): shift up / down", Keys: "{shift_up} / {shift_down}", Pad: "{shift_up} / {shift_down}"),
            new("Car, manual gearbox: clutch (hold)", PlayerInput.Clutch),
            new("Car, manual gearbox: gates, reverse, neutral (or a wheel's H-shifter)", Keys: "1–6, ` , 0", Pad: "—"),
            new("Cruise control (car, truck, farm machine, motorbike): on at this speed / off; sped up or slowed, the new speed; the brake switches it off", Keys: "{cruise}", Pad: "{lights_toggle} (hold)"),
            new("Car radio: next station", PlayerInput.RadioNext),
            new("Car radio: previous station", PlayerInput.RadioPrev),
            new("Car radio: stations and CDs (passengers too)", PlayerInput.RadioPanel),
            new("Car: fold the soft top / pump the hydraulics (Yaris)", PlayerInput.RoofToggle),
            new("At a car door: open it, then get in", PlayerInput.InteractMount),
            new("Open / shut the car door you are at", PlayerInput.CarDoor),
            new("Get out", PlayerInput.InteractMount),
        }),
        ("E-bike (#1167)", new Row[]
        {
            new("Assist level: Off, Eco, Tour, Sport, Turbo (the Bosch remote's button; in VR also a poke at the left grip)", PlayerInput.EngineToggle),
        }),
        ("Home-trainer training (phone › Training)", new Row[]
        {
            new("Next junction: take the road to the left / right", Keys: "{move_left} {move_right}", Pad: "{move_left} {move_right}"),
            new("Back to the course from a road off it", PlayerInput.RideRejoin, Pad: "phone › Training › Rejoin"),
            new("Turn round", PlayerInput.RideUTurn, Pad: "phone › Training › Turn round"),
            new("ERG on / off (the trainer holds a power)", PlayerInput.RideErg, Pad: "phone › Training › ERG"),
            new("ERG target +10 W / -10 W", Keys: "{ride_power_up} {ride_power_down}", Pad: "phone › Training › +10 W / -10 W"),
        }),
        ("Stadiums", new Row[]
        {
            new("On a stand's row: sit down; again, or walk off, to stand up", PlayerInput.InteractMount),
            new("At a concert's front-of-house console: the light desk (cues, effects, show input)", PlayerInput.InteractMount),
            new("Running a concert camera (/concert camera take N): zoom in", PlayerInput.CamZoomIn),
            new("Running a concert camera: zoom out", PlayerInput.CamZoomOut),
            new("Running a concert camera: cut to it (on air); Esc leaves it", PlayerInput.CamCut),
        }),
        ("Passengers (online)", new Row[]
        {
            new("Get into a seat of a car someone drives / get out", PlayerInput.InteractMount),
            new("In a bus: walk in by a door, E at a seat sits, E again stands up, E at the wheel drives", PlayerInput.InteractMount),
            new("Take the wheel, when nobody holds it", PlayerInput.TakeWheel),
            new("Look round from your seat / chase view", PlayerInput.CameraToggle),
        }),
        ("Trucks and buses", new Row[]
        {
            new("Couple / uncouple a trailer", PlayerInput.Couple),
            new("Shift up / splitter high", PlayerInput.ShiftUp),
            new("Shift down / splitter low", PlayerInput.ShiftDown),
            new("Clutch (hold)", PlayerInput.Clutch),
            new("H-pattern gates, reverse, neutral", Keys: "1–6, ` , 0", Pad: "—"),
            new("Retarder stalk more / less", Keys: "{retarder_up} / {retarder_down}", Pad: "—"),
            new("Parking brake (hold)", PlayerInput.Jump),
            new("Bus: doors", PlayerInput.CarDoor),
            new("Boat trailer: launch the boat / winch it aboard, stopped", PlayerInput.CarDoor),
            new("Bus: kneel", PlayerInput.Kneel),
            new("Bus: destination display", PlayerInput.Destination),
            new("Tipper: tip the body up / down (stopped)", PlayerInput.Destination),
            new("Mixer: discharge / stop (stopped; the drum turns while the engine runs)", PlayerInput.Destination),
        }),
        ("Farm machines", new Row[]
        {
            new("Couple an implement (linkage) or the tipping trailer (drawbar)", PlayerInput.Couple),
            new("Lower / raise the implement or the combine's header", PlayerInput.Kneel),
            new("Combine: swing the auger out / in (over a parked or a driven tipping trailer); at a farm co-op: deliver the load", Keys: "{destination}", Pad: "{car_door}"),
            new("Tipping trailer: tip the bin (at a farm co-op: deliver the load)", Keys: "{destination}", Pad: "{car_door}"),
            new("On foot at a loaded trailer or combine tank: take a sack (hold, or with run held: ten)", PlayerInput.InteractMount),
        }),
        ("Selling farm produce", new Row[]
        {
            new("Farm stand: set it up by a road (stand in hand) / pack it up empty (empty hand)", PlayerInput.UseItem),
            new("At a farm stand: open it (yours: stock, take back, collect the honesty box; another's: buy)", PlayerInput.InteractMount),
            new("At a sugar factory or a mill on foot: sell the sacks it buys", PlayerInput.InteractMount),
            new("Farm machine at a co-op or a specialty buyer: deliver the load (the prompt shows the price)", Keys: "{destination}", Pad: "{car_door}"),
        }),
        ("Forklift (#583)", new Row[]
        {
            // the paddles, free on a machine with no gearbox: held, the mast runs; let go, it stops
            // pallets need nothing more: forks run in under one and raised lift it, lowered set it down
            new("Raise the forks (hold); under a pallet, lifts it", PlayerInput.ShiftUp),
            new("Lower the forks (hold); with a pallet on, sets it down", PlayerInput.ShiftDown),
        }),
        ("Excavator (#611)", new Row[]
        {
            // drive mode: the tracks; dig mode: the arm on both sticks, ISO pattern, tracks held
            new("Tracks: forward / back, turn on the spot", Keys: "{move_forward} {move_back} / {move_left} {move_right}", Pad: "{throttle} {brake} / {move_left}"),
            new("Dig mode on / off", PlayerInput.DigMode),
            new("Dig: slew the house (hold)", Keys: "{arm_slew_left} / {arm_slew_right}", Pad: "left stick ← →"),
            new("Dig: stick out / in (hold)", Keys: "{arm_stick_out} / {arm_stick_in}", Pad: "left stick ↑ ↓"),
            new("Dig: boom up / down (hold)", Keys: "{arm_boom_up} / {arm_boom_down}", Pad: "right stick ↓ ↑"),
            new("Dig: bucket curl / dump (hold)", Keys: "{arm_bucket_curl} / {arm_bucket_dump}", Pad: "right stick ← →"),
        }),
        ("Tower crane (#618): at the ladder's foot or the remote's box on the mast", new Row[]
        {
            // the ladder up into the cab, or the radio remote from the ground; the sticks in the ISO crane pattern
            new("Climb the ladder / take the remote; at the controls, let go of them", PlayerInput.InteractMount),
            new("Ladder: up / down; let go", Keys: "{move_forward} / {move_back}; {jump}", Pad: "left stick ↑ ↓; {jump}"),
            new("Slew left / right (hold)", Keys: "{arm_slew_left} / {arm_slew_right}", Pad: "left stick ← →"),
            new("Trolley out / in (hold)", Keys: "{arm_stick_out} / {arm_stick_in}", Pad: "left stick ↑ ↓"),
            new("Hoist up / down (hold)", Keys: "{arm_boom_up} / {arm_boom_down}", Pad: "right stick ↓ ↑"),
            new("Hook a pallet on / set it down", PlayerInput.Couple),
            new("Hook camera / your own view", PlayerInput.CameraToggle),
        }),
        ("Mini excavator (#614): the excavator's controls, and", new Row[]
        {
            // the excavator's controls, and a dozer blade on the gear paddles, driving or digging
            new("Blade up / down (hold, driving or digging)", Keys: "{blade_raise} / {blade_lower}", Pad: "RB / LB"),
        }),
        ("Wheel loader (#612)", new Row[]
        {
            // it bends in the middle to steer, and keeps driving in work mode: only the right stick changes
            new("Work mode on / off (it still drives)", PlayerInput.DigMode),
            new("Work: lift the arm / lower it (hold)", Keys: "{arm_boom_up} / {arm_boom_down}", Pad: "right stick ↓ ↑"),
            new("Work: roll the bucket back / dump it (hold)", Keys: "{arm_bucket_curl} / {arm_bucket_dump}", Pad: "right stick ← →"),
            // the variant with a fork carriage (#615): the same controls, the forks lift pallets
            new("With forks: run them in under a pallet and lift the arm to take it, lower it to set it down", PlayerInput.ArmBoomUp),
        }),
        ("Mini dumper (#614)", new Row[]
        {
            // tracks like the excavator's; the skip on the tipper's action
            new("Tracks: forward / back, turn on the spot", Keys: "{move_forward} {move_back} / {move_left} {move_right}", Pad: "{throttle} {brake} / {move_left}"),
            new("Tip the skip / bring it down (stopped)", PlayerInput.Destination),
        }),
        ("Compact roller (#614)", new Row[]
        {
            // it bends in the middle like the loader; the work-mode toggle sets the drums vibrating
            new("Drums vibrating on / off", PlayerInput.DigMode),
        }),
        ("Telehandler (#614)", new Row[]
        {
            // it drives in work mode, as the loader does; the paddles are the forklift mast's
            new("Steering: front / four-wheel / crab", PlayerInput.RoofToggle),
            new("Work mode on / off (it still drives)", PlayerInput.DigMode),
            new("Work: lift the boom / lower it (hold)", Keys: "{arm_boom_up} / {arm_boom_down}", Pad: "right stick ↓ ↑"),
            new("Work: run the boom out / in (hold)", Keys: "{shift_up} / {shift_down}", Pad: "RB / LB"),
            new("Work: tilt the forks back / down (hold)", Keys: "{arm_bucket_curl} / {arm_bucket_dump}", Pad: "right stick ← →"),
        }),
        ("Paddle steamer", new Row[]
        {
            new("Telegraph: one step ahead / astern (FULL ASTERN .. STOP .. FULL AHEAD)", Keys: "{move_forward} / {move_back}", Pad: "{move_forward} up / down"),
            new("Wheel (the rudder needs way on)", Keys: "{move_left} / {move_right}", Pad: "{move_forward}"),
            new("Whistle (hold)", PlayerInput.Horn),
            new("Gangways, stopped", PlayerInput.CarDoor),
            new("Walk aboard by a gangway; E at the wheel in the wheelhouse drives, E at a seat sits", PlayerInput.InteractMount),
        }),
        ("Airliners", new Row[]
        {
            new("Thrust levers forward / back (held back at idle on the ground: reverse)", Keys: "{sprint} / {crouch_slide}", Pad: "{throttle} / {brake}"),
            new("Pitch and roll; on the ground: steer the nose wheel", Keys: "{move_forward}{move_left}{move_back}{move_right}", Pad: "{move_forward}"),
            new("Wheel brakes (hold)", PlayerInput.Jump),
            new("Flaps a notch down / up", Keys: "{flaps_down} / {flaps_up}", Pad: "{flaps_down} / {flaps_up}"),
            new("Gear up / down (in the air)", PlayerInput.CarDoor),
            new("Speedbrake: retracted, half, full", PlayerInput.Speedbrake),
            new("Parking brake", Keys: "{parking_brake}", Pad: "{speedbrake} (hold)"),
            new("Landing lights", PlayerInput.LightsToggle),
            new("Light sim: start the engines / shut them down", PlayerInput.EngineToggle),
            new("Light sim: autopilot on / off (then the stick turns the heading and altitude, the levers the speed)", Keys: "{autopilot}", Pad: "{lights_toggle} (hold)"),
            new("Cockpit view, without your body, chase camera", PlayerInput.CameraToggle),
            new("Light sim: pitch trim nose down / up (AN-124, military freighter)", Keys: "{trim_nose_down} / {trim_nose_up}", Pad: "—"),
        }),
        ("UFO", new Row[]
        {
            new("Fly where you look (strafe sideways)", Keys: Wasd, Pad: "{move_forward}"),
            new("Up / down (let go: it hovers)", Keys: "{jump} / {crouch_slide}", Pad: "{throttle} / {brake}"),
            new("Boost", Keys: "{sprint} / {boost}", Pad: "{sprint} / {boost}"),
            new("Tractor beam on / off", PlayerInput.TractorBeam),
            new("Ramp (landed) / legs (flying)", PlayerInput.CarDoor),
            new("Rim lights", PlayerInput.LightsToggle),
            new("Get out (down the ramp when landed)", PlayerInput.InteractMount),
        }),
        ("Flying", new Row[]
        {
            new("Pitch and roll", Keys: Wasd, Pad: "{move_forward}"),
            new("Helicopter up / down", Keys: "{jump} / {crouch_slide}", Pad: "{throttle} / {brake}"),
            new("Plane throttle up / down", Keys: "{sprint} / {crouch_slide}", Pad: "{throttle} / {brake}"),
            new("Guns (armed aircraft)", PlayerInput.Fire),
        }),
        ("Creative mode and fly camera", new Row[]
        {
            new("Creative mode / survival", PlayerInput.ToggleMode),
            new("Spectator fly camera / back (wheel: speed)", PlayerInput.Spectator, Pad: "hold {toggle_mode}"),
            new("Creative: take off / land", Keys: "{jump} {jump}", Pad: "{jump} {jump}"),
            new("Creative: up / down", Keys: "{jump} / {sprint}", Pad: "{jump} / {crouch_slide}"),
            new("Up", PlayerInput.FlyUp),
            new("Down", PlayerInput.FlyDown),
            new("Fast", PlayerInput.FlyBoost),
        }),
        // The map screen on the title menu (#515). Its keys are deliberately the same ones the world
        // uses for other things, as T and the rest already are: only the map screen reads them, and
        // there is no free letter left on the keyboard.
        ("Map screen (main menu)", new Row[]
        {
            new("Pan", Keys: "Arrows / WASD, or right-drag", Pad: "Left stick"),
            new("Zoom", PlayerInput.MapZoomIn),
            new("Draw / erase the selection", Keys: "Drag, or Enter on the centre tile", Pad: "A"),
            new("Switch tool (rectangle, brush, erase)", PlayerInput.MapTool),
            new("Find a place", PlayerInput.MapSearch),
        }),
        ("Versteckis", new Row[]
        {
            new("Use the round tool in hand (tag, sniff, decoy, sprint, voice, disguise)", PlayerInput.UseItem),
            new("Copy the prop you look at (with the Disguise)", Keys: "{aim_item} + {use_item}", Pad: "LB + RB"),
            new("Sprint burst while disguised", PlayerInput.Sprint),
            new("Settle the prop", PlayerInput.CrouchSlide),
            new("Hold: ring the bell, light the fire; take or set down the milk can", PlayerInput.InteractMount),
            new("Caught: follow the next player", Keys: "← / →", Pad: "D-pad ← / →"),
        }),
        ("Game", new Row[]
        {
            new("Map: search a place and go", PlayerInput.Teleport),
            new("Keep the last minutes for the movie studio (also Pause > Save clip)", PlayerInput.SaveClip, Pad: "{menu}, then Save clip"),
            new("Free the mouse cursor (tap; Alt again or click the world to look)", PlayerInput.FreeCursor, Pad: "—"),
            new("Battle Royale: ping for your team (also middle-click on the map)", PlayerInput.Ping),
            new("Menu", PlayerInput.Menu),
            new("Chat / command (the \"/\" button lists the commands as buttons)", Keys: "Enter or /", Pad: "{quick_menu} › Chat"),
            new("This screen", PlayerInput.Help),
            new("Performance overlay / log", Keys: "F3 / F4", Pad: "—"),
            new("Debug menu (offline or admin)", PlayerInput.DebugMenu),
#if PLAYTEST
            new("Playtest panel (--playtest, Debug builds)", Keys: "F10", Pad: "L3 + R3"),
            new("Playtest note: what's wrong, stamped with the place (--playtest-record)", Keys: "F12", Pad: "L3 + R3 › Note"),
            new("Playtest top view / pause and scrub it (Ctrl + wheel)", Keys: "Shift+F12 / Ctrl+F12", Pad: "L3 + R3 › Top view"),
#endif
        }),
    };

    private ScrollContainer _scroll = null!;
    private Label _footer = null!;
    private Control _padChip = null!;

    public override void _Ready()
    {
        Layer = 42;   // over the menus, so it can be opened from them

        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(centre);

        // the menus' glass, themed on this panel's own root (docs/notes/ui/style-guide.md)
        _panel = new PanelContainer { Visible = false, Theme = Ui.UiTheme.Get() };
        _panel.AddThemeStyleboxOverride("panel", Ui.UiTheme.GlassPanel(0.92f, 12, 20));
        centre.AddChild(_panel);

        var rows = Ui.UiKit.VBox(10);
        _panel.AddChild(rows);

        var head = Ui.UiKit.HBox(14);
        head.AddChild(Ui.UiKit.Text("Controls", Ui.UiTheme.FontHeading, Ui.UiTheme.Text, bold: true));
        head.AddChild(Ui.UiKit.Spacer(expand: true));
        head.AddChild(Chip("Keyboard", KeyColor, true));
        _padChip = Chip("Pad", PadColor, true);
        head.AddChild(_padChip);
        rows.AddChild(head);

        // the groups flow into as many columns as the window has room for, and scroll past its height
        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        rows.AddChild(_scroll);
        _columns = new GridContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _columns.AddThemeConstantOverride("h_separation", 12);
        _scroll.AddChild(Ui.UiKit.Margin(_columns, 0, 0, 10, 0));

        _footer = Ui.UiKit.Text("", Ui.UiTheme.FontTiny, Ui.UiTheme.TextFaint, wrap: true);
        rows.AddChild(_footer);

        PlayerInput.DeviceChanged += Rebuild;
        GetViewport().SizeChanged += Rebuild;
        Rebuild();
        // "--controls" opens it from boot, for screenshotting it
        if (CmdArgs.Has("--controls")) Callable.From(Open).CallDeferred();
    }

    public override void _ExitTree()
    {
        PlayerInput.DeviceChanged -= Rebuild;
        GetViewport().SizeChanged -= Rebuild;
    }

    /// <summary>The groups whose controls are used mounted: their VR names are the mounted ones.</summary>
    private static readonly string[] Mounted = { "Riding and driving", "Trucks and buses", "Paddle steamer", "Airliners", "UFO", "Flying" };

    private static readonly Color KeyColor = new(1f, 0.84f, 0.42f);
    private static readonly Color PadColor = new(0.6f, 0.8f, 1f);
    private static readonly Color VrColor = new(0.55f, 0.9f, 0.62f);

    /// <summary>The second column: the pad, or in VR the controllers (#435).</summary>
    private static InputDevice PadDevice => PlayerInput.HintDevice == InputDevice.VR ? InputDevice.VR : InputDevice.Gamepad;
    private const float MinGroupWidth = 320;

    private void Rebuild()
    {
        if (_columns == null) return;
        var view = GetViewport().GetVisibleRect().Size;
        var size = new Vector2(Mathf.Min(view.X - 48, 1600), Mathf.Min(view.Y - 48, 900));
        _panel.CustomMinimumSize = size;
        _panel.Size = size;

        bool vr = PadDevice == InputDevice.VR;
        var legend = (Label)_padChip.GetChild(0);
        legend.Text = vr ? "VR" : "Pad";
        legend.AddThemeColorOverride("font_color", vr ? VrColor : PadColor);
        _padChip.AddThemeStyleboxOverride("panel", Ui.UiTheme.Flat(new Color(vr ? VrColor : PadColor, 0.12f), 6, 7, 2,
            new Color(vr ? VrColor : PadColor, 0.35f), 1));

        foreach (var child in _columns.GetChildren()) child.QueueFree();
        float inner = size.X - 40 - 10;   // panel margins, the scrollbar's gutter
        int count = Mathf.Clamp((int)((inner + 12) / (MinGroupWidth + 12)), 1, 4);
        float width = (inner - 12 * (count - 1)) / count;
        _columns.Columns = count;
        // greedy balance: each group goes to the shortest column, in order of the list
        var stacks = new VBoxContainer[count];
        var heights = new int[count];
        for (int i = 0; i < count; i++)
        {
            stacks[i] = Ui.UiKit.VBox(12);
            stacks[i].CustomMinimumSize = new Vector2(width, 0);
            stacks[i].SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _columns.AddChild(stacks[i]);
        }
        foreach (var (title, rows) in Groups)
        {
            int k = Array.IndexOf(heights, heights.Min());
            // in VR the triggers are the triggers in a vehicle, the shoulders on foot (XrPad)
            bool was = XR.XrPad.AssumeShoulders(!Mounted.Contains(title));
            stacks[k].AddChild(Group(title, rows));
            XR.XrPad.AssumeShoulders(was);
            heights[k] += rows.Length + 3;
        }
        _footer.Text = $"{InputHints.Label(PlayerInput.Help, InputDevice.KeyboardMouse)} or Esc closes. "
            + "Keys are shown as printed on your keyboard. The hints at the bottom right change with what you are doing.";
    }

    private static Control Group(string title, Row[] rows)
    {
        var box = Ui.UiKit.VBox(4);
        box.AddChild(Ui.UiKit.Section(title));
        foreach (var row in rows)
        {
            string keys = row.Keys != null ? InputHints.Format(row.Keys, InputDevice.KeyboardMouse)
                : row.Action != null ? InputHints.Label(row.Action, InputDevice.KeyboardMouse) : "—";
            string pad = row.Pad != null ? InputHints.Format(row.Pad, PadDevice) : row.Action != null ? PadOrDash(row.Action) : "—";
            var line = Ui.UiKit.HBox(6);
            var what = Ui.UiKit.Text(row.What, Ui.UiTheme.FontSmall, Ui.UiTheme.Text, wrap: true);
            what.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            line.AddChild(what);
            line.AddChild(Chip(keys, KeyColor));
            line.AddChild(Chip(pad, PadDevice == InputDevice.VR ? VrColor : PadColor));
            box.AddChild(line);
        }
        return Ui.UiKit.Card(box, 0.5f, 12);
    }

    /// <summary>A key cap: the binding on a rounded tile, tinted keyboard-amber or pad-blue; a dash is left bare.</summary>
    private static Control Chip(string text, Color color, bool legend = false)
    {
        bool none = text == "—";
        var chip = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(legend ? 0 : 70, 0),
        };
        chip.AddThemeStyleboxOverride("panel", none
            ? new StyleBoxEmpty()
            : Ui.UiTheme.Flat(new Color(color, 0.12f), 6, 7, 2, new Color(color, 0.35f), 1));
        var label = Ui.UiKit.Text(text, Ui.UiTheme.FontTiny + 1, none ? Ui.UiTheme.TextFaint : color, bold: !none,
            align: HorizontalAlignment.Center);
        chip.AddChild(label);
        return chip;
    }

    /// <summary>The pad (or VR) binding, or a dash where there is only a keyboard one (the label would repeat the key).</summary>
    private static string PadOrDash(string action)
    {
        string pad = InputHints.Label(action, PadDevice);
        return pad == InputHints.Label(action, InputDevice.KeyboardMouse) ? "—" : pad;
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        Rebuild();
        _panel.Visible = true;
    }

    public void Close() => _panel.Visible = false;

    // _Input, not _UnhandledInput: it opens over the main menu, which would otherwise take the
    // Esc meant to close this and act on it itself
    public override void _Input(InputEvent e)
    {
        if (!e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed(PlayerInput.Help))
        {
            Toggle();
            GetViewport().SetInputAsHandled();
        }
        else if (IsOpen && (e.IsActionPressed(PlayerInput.Menu) || e.IsActionPressed("ui_cancel")))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
