using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace UnitSport.Core;

/// <summary>How finely the inner LOD rings and their roads/buildings are drawn.</summary>
/// <summary>How much the client streams (#63): Low data keeps fine tiles to the nearest rings.</summary>
public enum DataPreset
{
    Standard = 0,
    Low = 1,
}

public enum DetailPreset
{
    Low = 0,
    Medium = 1,
    High = 2,
    /// <summary>Under Low, for a standalone headset's GPU (#1112): offered in VR only.</summary>
    VeryLow = 3,
}

/// <summary>How the game window occupies the screen.</summary>
public enum WindowMode
{
    Windowed = 0,
    Borderless = 1,
    Fullscreen = 2,
}

/// <summary>How the main view smooths its 3D edges (#768): none, FXAA, or MSAA with 2, 4 or 8 samples.</summary>
public enum AntiAliasing
{
    Off = 0,
    Fxaa = 1,
    Msaa2x = 2,
    Msaa4x = 3,
    Msaa8x = 4,
}

/// <summary>What the on-screen performance overlay shows (F3 cycles it).</summary>
public enum PerfOverlayMode
{
    Off = 0,
    Fps = 1,
    Detailed = 2,
}

/// <summary>
/// Player-tunable rendering and streaming settings, persisted to <c>user://settings.json</c>.
///
/// <para>
/// One static instance, because the things it drives — the chunk manager's ring table, every
/// world material's fog uniforms, the cameras' far plane — are themselves singletons of the
/// running world, and each subscribes to <see cref="Changed"/> rather than being handed a copy
/// at construction. Command-line overrides (<c>--rings</c>, <c>--horizon</c>, <c>--fog</c>,
/// <c>--detail</c>) apply on top of the file for one session and are never written back, so a
/// verification run cannot quietly change what the player sees next time.
/// </para>
/// </summary>
/// <summary>
/// How vehicles and running are tuned. Game is the arcade layer on top of the physics; Sim is the
/// untouched real-world model, kept because a home trainer's watts only mean anything in it.
/// </summary>
public enum RideProfile
{
    Game,
    Sim,
}

public sealed class GameSettings
{
    private const string File = "user://settings.json";

    public static GameSettings Current { get; private set; } = new();

    /// <summary>Raised after any live change so the world can re-apply itself.</summary>
    public static event Action? Changed;

    /// <summary>Tile rings of real terrain around each anchor (Chebyshev radius in km).</summary>
    public int RenderDistanceRings { get; set; } = 15;
    public const int MinRings = 6, MaxRings = 40;
    /// <summary>
    /// The fewest rings this device may load: 3 on a standalone headset (#1112), where every tile is a
    /// draw call a Quest 2 replays in each of its screen bins; the horizon's merged blocks cover the rest.
    /// </summary>
    public static int MinRingsHere => Platform.IsStandaloneVr ? 3 : MinRings;

    /// <summary>How far the 100 m horizon lattice is drawn, in km. 0 turns it off.</summary>
    public int HorizonKm { get; set; } = 60;
    public const int MaxHorizonKm = 150;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DetailPreset Detail { get; set; } = DetailPreset.Medium;

    /// <summary>Distance fog in the world shaders. Off by default: the horizon is the point.</summary>
    public bool Fog { get; set; }

    /// <summary>
    /// The menus' look (#1135, <see cref="Ui.Skins.MenuSkin"/>): "auto" (the day skin by day, the night skin by night)
    /// or a skin's id (a file in <c>assets/skins/</c> or <c>user://skins/</c>).
    /// </summary>
    public string MenuSkin { get; set; } = Ui.Skins.MenuSkin.SportId;
    public string MenuSkinDay { get; set; } = Ui.Skins.MenuSkin.DayDefault;
    public string MenuSkinNight { get; set; } = Ui.Skins.MenuSkin.NightDefault;

    // ---- the adventure's navigation (#1226, docs/notes/adventure/compass.md, guide.md, travel.md) ----
    /// <summary>The free-roam compass strip at the top of the screen.</summary>
    public bool AdventureCompass { get; set; } = true;

    /// <summary>Versteckis (#1268): the blind seeker hears the count, a tick a second.</summary>
    public bool VersteckisCounting { get; set; } = true;
    /// <summary>The guiding gust: 0 on demand (N), 1 always while a target is tracked, 2 arrow only, 3 off.</summary>
    public int GuideWind { get; set; }
    /// <summary>The guide as a high-contrast arrow on the ground instead of leaves (accessibility).</summary>
    public bool GuideHighContrast { get; set; }
    /// <summary>Offline: travel only through the unlocked network (no free jumps), as on a network server.</summary>
    public bool AdventureTravelNetwork { get; set; }

    /// <summary>No hover tilts, pops or ambient animation in the menus (#1135): plain fades only.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>
    /// Generate the terrain there is no data for, blended into the real tiles beside it
    /// (<see cref="Terrain.FallbackChunkSource"/>). Off leaves void past the real region; offline on a
    /// copy with no terrain it generates anyway, the only ground there is (#1002).
    /// </summary>
    public bool GeneratedFill { get; set; } = true;

    /// <summary>Tile builds allowed in flight at once; 0 picks by whether a server is involved.</summary>
    public int MaxConcurrentBuilds { get; set; }
    public const int MaxBuildsCap = 32;

    /// <summary>Main-thread milliseconds per frame spent turning built tiles into Godot meshes.</summary>
    public double CommitBudgetMs { get; set; } = 4;

    /// <summary>
    /// Viewport 3D scale, the biggest single fidelity/performance knob: the 3D is drawn at this
    /// fraction of the window's real pixels (<c>stretch/mode = "canvas_items"</c>), so 1 is native
    /// and above 1 supersamples; in PS1, a fraction of the UI canvas instead, so its low resolution
    /// holds on any screen (<see cref="DisplaySettings.EffectiveScale"/>). The UI lays out at a fixed
    /// <see cref="BaseWidth"/>x<see cref="BaseHeight"/> content scale and is untouched by it.
    /// </summary>
    public float RenderScale { get; set; } = 0.75f;
    public const int BaseWidth = 1152, BaseHeight = 648;
    public const float MinRenderScale = 0.25f, MaxRenderScale = 2f;

    public bool VSync { get; set; } = true;

    /// <summary>
    /// The main view's anti-aliasing in Cartoon and Realistic (#768); PS1 keeps its jagged
    /// low-resolution edges, which are its look (<see cref="DisplaySettings"/>). FXAA by default:
    /// MSAA shades a pixel for any triangle covering one of its samples, with the vertex colours
    /// extrapolated past the triangle (shaders have no centroid interpolation here), and the
    /// distant ground's sub-pixel triangles then sparkle along the horizon.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AntiAliasing AntiAliasing { get; set; } = AntiAliasing.Fxaa;

    /// <summary>
    /// The width of the concert screens' camera feed (IMAG, #996), 16:9: 256, 512 or 1024. Each is
    /// another render of the bowl; the size costs less than the render itself.
    /// </summary>
    public int ConcertScreenWidth { get; set; } = 512;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PerfOverlayMode PerfOverlay { get; set; } = PerfOverlayMode.Off;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WindowMode WindowMode { get; set; } = WindowMode.Windowed;

    // --- controls ---
    /// <summary>Right-stick look speed multiplier; 1 turns at <see cref="PlayerInput.StickTurnRate"/>.</summary>
    public float StickSensitivity { get; set; } = 1f;
    /// <summary>Data (#63, every platform): Standard, or Low data (fewer full tiles fetched).</summary>
    public DataPreset Data { get; set; } = DataPreset.Standard;
    /// <summary>Low data whenever the phone's data saver is on (Android; ignored elsewhere).</summary>
    public bool AutoLowData { get; set; } = true;
    /// <summary>Ask before joining a server over a metered connection (cellular, a hotspot).</summary>
    public bool WarnMetered { get; set; } = true;
    /// <summary>Cap on the downloaded-tile cache on disk, GB (oldest evicted first).</summary>
    public float CacheGb { get; set; } = 2f;

    /// <summary>Android's data saver, as last read (<see cref="DataWatch"/>); never saved.</summary>
    [JsonIgnore]
    public static bool DataSaverOn { get; set; }

    /// <summary>Whether the client streams as Low data now: chosen, or the data saver with auto on.</summary>
    [JsonIgnore]
    public bool LowDataActive => Data == DataPreset.Low || AutoLowData && DataSaverOn;

    /// <summary>Low data's render distance cap, in rings: past it the horizon draws the land.</summary>
    public const int LowDataRings = 5;

    /// <summary>Touch look (#63): camera turn per pixel of drag, as a multiple of the mouse's.</summary>
    public float TouchLookSpeed { get; set; } = 1.5f;

    /// <summary>The phone's on-screen controls (#1043): size, 0.75..1.2 of their millimetres.</summary>
    public float TouchScale { get; set; } = 1f;
    /// <summary>How solid the on-screen controls are at rest, 0.2..1; a pressed one is always solid.</summary>
    public float TouchOpacity { get; set; } = 0.45f;
    /// <summary>The stick on the right, the buttons on the left.</summary>
    public bool TouchLeftHanded { get; set; }
    /// <summary>Pushing the stick past its ring runs; off, a Run button does.</summary>
    public bool TouchRunPastRing { get; set; } = true;
    /// <summary>A short tap on the world uses the item in hand, instead of a Use button.</summary>
    public bool TouchTapToUse { get; set; }
    /// <summary>Actions pinned from the More sheet, as "Mode:action" (two a mode at most).</summary>
    public List<string> TouchPins { get; set; } = new();
    /// <summary>
    /// The touch layout editor's moves, sizes and hidden controls (#1048), by
    /// <see cref="TouchLayout.ModeKey"/> or <see cref="TouchLayout.SharedKey"/>.
    /// </summary>
    public Dictionary<string, TouchOverride> TouchOverrides { get; set; } = new();

    public bool InvertY { get; set; }

    /// <summary>Free fly moves on the ground plane, Space / Shift for height, like Minecraft (#1002); off, forward follows the look.</summary>
    public bool LevelFlight { get; set; } = true;

    /// <summary>Creative flight (#1002) passes through the ground, walls and roofs instead of bumping into them.</summary>
    public bool CreativeNoclip { get; set; }

    /// <summary>Stick travel ignored around centre. Worn pads drift, so it is a setting.</summary>
    public float StickDeadzone { get; set; } = 0.18f;

    /// <summary>Controller rumble on landings, impacts and speed.</summary>
    public bool Vibration { get; set; } = true;

    /// <summary>Steering wheel, pedals and their bindings (<see cref="SteeringWheel"/>).</summary>
    public WheelSettings Wheel { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RideProfile RideProfile { get; set; } = RideProfile.Game;
    /// <summary>
    /// The rider's weight in kit, kg (#1124): with the bike's, the mass the Simulation road bike's
    /// watts have to move, so a home-trainer rider's real weight sets their speed up a climb. The
    /// default keeps the reference rider (82 kg with the bike and bottles).
    /// </summary>
    public float RiderMass { get; set; } = 72.5f;
    /// <summary>
    /// The rider's critical power, W (#1124): what they hold for about an hour (FTP is ~95% of it).
    /// Above it the Simulation road bike's keys and pad drain a 20 kJ reserve; empty, the rider is held to it.
    /// </summary>
    public float RiderCriticalPower { get; set; } = 240f;
    /// <summary>Cars' tyres wear with the sliding they do and lose grip (off by default).</summary>
    public bool TyreWear { get; set; }
    /// <summary>Cars' brakes heat up and fade, and their pads wear (off by default).</summary>
    public bool BrakeWear { get; set; }
    /// <summary>
    /// E from outside a parked walkable vehicle (a bus, a coach, the steamer: anything with decks)
    /// boards it and puts you at its wheel (on, the default, #384). Off: E does nothing there; you walk
    /// aboard by a door, a gangway or a ladder and take the wheel from inside. Other vehicles keep E.
    /// </summary>
    public bool BoardWalkableFromOutside { get; set; } = true;
    /// <summary>
    /// How airliners fly (#414, #415): Arcade (protections on every type, wings level when the stick is
    /// let go, ready to taxi) or Light sim (engine start, autopilot, trim, fuel; conventional types can stall).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Player.AirlinerHandling Airliner { get; set; } = Player.AirlinerHandling.Arcade;
    /// <summary>How trucks and buses are shifted (#70): automatic, sequential, with the clutch, H-pattern.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Player.HeavyShift HeavyGearbox { get; set; } = Player.HeavyShift.Automatic;
    /// <summary>How cars are shifted (#290): automatic, sequential (auto clutch), or manual with the clutch pedal.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Player.CarGearbox CarGearbox { get; set; } = Player.CarGearbox.Automatic;

    // --- world ---
    /// <summary>Real minutes for a whole day; 0 stops the clock at <see cref="StartHour"/>.</summary>
    public float DayLengthMinutes { get; set; } = 24f;
    public float StartHour { get; set; } = 10f;

    /// <summary>Cars around the player in daytime (about half at night); 0 turns traffic off.</summary>
    public int TrafficCars { get; set; } = 35;
    public bool Trains { get; set; } = true;

    /// <summary>
    /// The traffic modes beyond cars and trains (#1060, tracking #1058): walkers (#1033) and riders (#1056) around the player in
    /// daytime (fewer at night), trams on the street tracks (#1057). 0 by default, also once a mode has landed (the user's choice
    /// for walkers, #1033); a mode's row shows in the settings once it lands (
    /// <see cref="World.Traffic.WalkersReady"/>, <see cref="World.Traffic.RidersReady"/>, <see cref="World.Traffic.TramsReady"/>);
    /// <c>--walkers N</c>, <c>--bikes N</c>, <c>--trams N</c> for one run.
    /// </summary>
    public int TrafficWalkers { get; set; }
    /// <summary>Riders (#1056): 30 by day, like a town's cyclists around the cars' 35.</summary>
    public int TrafficRiders { get; set; } = 30;
    public int Trams { get; set; }

    /// <summary>
    /// Per occasion id: follow the calendar, hide its look, or force it (<see cref="Occasions.OccasionManager"/>).
    /// Absent means Auto.
    /// </summary>
    public Dictionary<string, UnitSport.Occasions.OccasionPreference> OccasionPreferences { get; set; } = new();

    // --- feel ---
    /// <summary>Everything the game plays, 0..1 — the Master bus (see <see cref="Audio.SfxBus.ApplyVolumes"/>).</summary>
    public float MasterVolume { get; set; } = 0.5f;
    /// <summary>Sound effects volume, 0..1 — the Sfx bus. Sliders are perceptual, not linear.</summary>
    public float SfxVolume { get; set; } = 0.5f;
    /// <summary>Ambience volume, 0..1.</summary>
    public float AmbienceVolume { get; set; } = 0.7f;
    /// <summary>Music volume, 0..1 — the Music bus: radios, car stereos, live stations (#261).</summary>
    public float MusicVolume { get; set; } = 0.7f;

    /// <summary>Which sound chip the engines are rendered as (<see cref="Audio.EngineSynth"/>).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Audio.EngineVoice EngineVoice { get; set; } = Audio.EngineVoice.Ps1;

    /// <summary>How the world looks (<see cref="Styles.StyleKit"/>). Client-only, never replicated.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Styles.VisualStyle VisualStyle { get; set; } = Styles.StyleKit.Default;

    /// <summary>
    /// The file has been through the move to Cartoon as the default (#849). Before it, every file
    /// saved PS1 whether the player chose it or not, so a file without this flag that says PS1
    /// is read as Cartoon once; a PS1 picked since is kept.
    /// </summary>
    public bool CartoonDefault { get; set; } = true;

    /// <summary>Camera shake strength, 0 (off) .. 1.</summary>
    public float ScreenShake { get; set; } = 1f;

    /// <summary>Radial streaks at the screen edge at speed.</summary>
    public bool SpeedLines { get; set; } = true;

    /// <summary>The gliders' variometer: a beep that rises with the climb, a low tone in sink (#1225, <see cref="Audio.Variometer"/>).</summary>
    public bool Variometer { get; set; } = true;

    /// <summary>
    /// Radios, stereos and speakers muffled by what stands between them and the ears, found by rays
    /// (<see cref="Audio.Hearing"/>). Off: heard as in the open, no rays cast (#1281).
    /// </summary>
    public bool SoundOcclusion { get; set; } = true;

    /// <summary>A comic speech bubble over a player who talks in the chat near you (#862, <see cref="Ui.Bubbles.ChatBubbles"/>).</summary>
    public bool ChatBubbles { get; set; } = true;

    /// <summary>Over-the-shoulder view on foot and a chase view mounted; V / R3 toggles it in game.</summary>
    public bool ThirdPerson { get; set; } = true;

    /// <summary>The on-foot camera over the left shoulder instead of the right; <c>swap_shoulder</c> flips it in game (#460).</summary>
    public bool LeftShoulder { get; set; }

    /// <summary>
    /// Draw the pigeon tail first, the way it flew before the fix: the bird keeps the half turn
    /// <see cref="Player.Pigeon.BuildVisual"/> used to give a mesh that already faced the right way.
    /// Off by default. Client-only and never replicated, like <see cref="VisualStyle"/>: it decides
    /// how the birds on this screen are drawn — your own and everyone else's — never how they fly.
    /// </summary>
    public bool TailFirstPigeon { get; set; }

    /// <summary>
    /// Ride a snowboard goofy, right foot forward (#922); off is regular. The owner's choice, sent to
    /// the other peers in <c>FootPlayer.TuningBits</c> so they draw the rider facing the same way.
    /// </summary>
    public bool Goofy { get; set; }

    // --- network ---
    /// <summary>List the dedicated servers found on the LAN over mDNS in the main menu (<see cref="Net.LanDiscovery"/>).</summary>
    public bool LanDiscovery { get; set; } = true;

    /// <summary>
    /// Minutes of everyone's movement the replay buffer keeps for the movie studio (#638), 0 = off.
    /// About 1.4 MB a minute per player in view.
    /// </summary>
    public int ReplayMinutes { get; set; } = 5;

    /// <summary>The server last joined from the menu, so the field is not reset to localhost every launch.</summary>
    public string LastHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// The name asked for when joining a server, set the first time the Multiplayer screen opens.
    /// Empty until then; <c>--name</c> overrides it for one run without saving.
    /// </summary>
    public string PlayerName { get; set; } = "";

    /// <summary>
    /// The first-run welcome (#1131, <see cref="Ui.WelcomeScreen"/>) was finished or skipped. True by default, so a
    /// settings file from before the welcome never shows it; <see cref="Load"/> sets it false when there is no file
    /// yet (a fresh install), and the welcome saves false as it opens, so quitting half-way shows it again.
    /// </summary>
    public bool FirstRunDone { get; set; } = true;

    /// <summary>
    /// The first-run tutorial (#517, <see cref="Tutorial"/>) was finished or skipped. Off until
    /// then; Settings › Gameplay turns it off again to replay it.
    /// </summary>
    public bool TutorialDone { get; set; }

    /// <summary>
    /// The rides whose mini tutorial (#517, <see cref="VehicleIntroCard"/>) was done, by
    /// <see cref="VehicleIntroKind"/> name. Cleared with <see cref="TutorialDone"/> by "Play again".
    /// </summary>
    public List<string> VehicleIntrosSeen { get; set; } = new();

    /// <summary>
    /// The player's figure (#394): <see cref="Avatar.Appearance.Pack"/>ed, 0 until one is chosen in
    /// the Character menu (#806; till then the figure comes from the player's network id).
    /// Saved alone with <see cref="SaveOnly"/>; <c>ClientWorld.PublishAppearance</c> puts it on the player.
    /// </summary>
    public int AppearanceBits { get; set; }

    /// <summary>
    /// Where the player landed last (#515), LV95 metres, so the map screen opens on it instead of
    /// sending everyone back to Riddes every session. Zero until a world has been entered.
    /// </summary>
    public double LastLandingE { get; set; }
    public double LastLandingN { get; set; }

    /// <summary>
    /// Hosting from the menu (#799): where players start on this machine's server, LV95 metres (zero:
    /// Riddes), and whether they may teleport. Remembered for the next game hosted.
    /// </summary>
    public double HostSpawnE { get; set; }
    public double HostSpawnN { get; set; }
    public bool HostTeleport { get; set; } = true;

    /// <summary>The world last picked on Play solo and in the Host dialog (#999, a <see cref="WorldChoice"/> id).</summary>
    public string SoloWorld { get; set; } = WorldChoice.OpenId;
    public string HostWorld { get; set; } = WorldChoice.OpenId;

    /// <summary>GPX files replayed recently, newest first (the Play solo track picker lists them).</summary>
    public List<string> RecentGpx { get; set; } = new();

    /// <summary>
    /// Play in a VR headset (#186, OpenXR, a Quest over Link). OpenXR only starts with the engine,
    /// so turning this on or off relaunches the game (<see cref="XR.XrSession.Relaunch"/>), and a
    /// launch from the title with it on relaunches itself into VR.
    /// </summary>
    public bool VrMode { get; set; }

    /// <summary>What the monitor shows while in VR (<see cref="XR.XrMonitor"/>); F8 cycles it.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public XR.MonitorView VrMonitor { get; set; } = XR.MonitorView.FirstPerson;

    // --- the headset's picture (#244): what a streamed headset (Air Link) needs ---
    /// <summary>
    /// Headset MSAA samples: 0, 2, 4 or 8. Aliased edges shimmer, and shimmer is what the Link
    /// video encoder turns into blocks.
    /// </summary>
    public int VrMsaa { get; set; } = 4;
    /// <summary>The headset's 3D resolution, relative to the eye size the runtime asks for.</summary>
    public float VrRenderScale { get; set; } = 1f;
    /// <summary>
    /// A standalone headset's dynamic resolution never goes under this share of its eye buffers'
    /// width and height (#1112, XR/XrDynamicResolution).
    /// </summary>
    public float VrResolutionFloor { get; set; } = 0.75f;
    public const float MinVrResolutionFloor = 0.5f;
    public const float MinVrRenderScale = 0.5f, MaxVrRenderScale = 1.5f;
    /// <summary>Foveated rendering: coarser shading towards the edge of each eye (variable rate shading).</summary>
    public bool VrFoveation { get; set; } = true;
    /// <summary>
    /// A standalone headset's Application SpaceWarp (#1112): the game renders every other frame and the headset makes
    /// the one between from the motion it is given, so a frame has twice the time (XR/XrSession.ApplySpaceWarp).
    /// Off, and only <c>--spacewarp on</c>: on a Quest 2 a black patch flashed in the left eye, and the made frames felt
    /// less smooth than the headset's own reprojection of a slower game.
    /// </summary>
    public bool VrSpaceWarp { get; set; }
    /// <summary>
    /// The headset's refresh rate, Hz: 60, 72, 80, 90 or 120 (the nearest the runtime offers). Physics
    /// ticks at it in VR, so every frame moves the view once. 72 by default: a Quest 2 over Air
    /// Link holds 72 far more often than 90, and each frame gets more of the stream's bitrate (#1036);
    /// on its own GPU (#1112) 72 is its budget too, and 60 the fallback for a scene it cannot hold.
    /// </summary>
    public int VrRefreshHz { get; set; } = 72;
    /// <summary>The rates Settings offers: 60 only on a standalone headset (Link offers none under 72).</summary>
    public static readonly int[] VrRefreshRates = Platform.IsStandaloneVr ? new[] { 60, 72, 80, 90, 120 } : new[] { 72, 80, 90, 120 };
    /// <summary>
    /// The terrain's detail preset in the headset, in place of <see cref="Detail"/>. Low by default:
    /// a headset is held back by triangles (it draws the scene for each eye), and Medium has
    /// three times Low's around the player (#1036).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DetailPreset VrDetail { get; set; } = DetailPreset.Low;

    // --- VR comfort (#439) ---
    /// <summary>The right stick's turn on foot: a snap of 15, 30 or 45 degrees, or 0 for a smooth turn.</summary>
    public int VrSnapDegrees { get; set; } = 30;
    /// <summary>On foot the left stick aims a teleport arc instead of walking.</summary>
    public bool VrTeleport { get; set; }
    /// <summary>How much the view narrows while the world moves under you: 0 off, 1 full.</summary>
    public float VrVignette { get; set; } = 1f;
    /// <summary>The hands swapped: the right controller moves and the left one uses and turns.</summary>
    public bool VrLeftHanded { get; set; }

    // --- cockpit: first person at the wheel of a car (#69) ---
    /// <summary>Your own arms and legs at the wheel. V cycles chase → cockpit with them → cockpit without.</summary>
    public bool CockpitBody { get; set; } = true;
    /// <summary>Working rear-view and door mirrors: each a small extra render of the world.</summary>
    public bool CockpitMirrors { get; set; } = true;
    /// <summary>Speed, gear and rpm on the HUD in the cockpit too; off, the dashboard shows them.</summary>
    public bool CockpitHud { get; set; }
    /// <summary>Vertical field of view from the driver's seat, degrees.</summary>
    public float CockpitFov { get; set; } = 70f;
    /// <summary>The eye moved up (+) or down, and forward (+) or back, from where the seat puts it, m.</summary>
    public float SeatHeight { get; set; }
    public float SeatForward { get; set; }
    /// <summary>The head sways with the car's accelerations: back under power, forward braking, out in a bend.</summary>
    public bool CockpitHeadMotion { get; set; } = true;

    /// <summary>Window size when windowed; 0 leaves whatever size the window already has.</summary>
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    /// <summary>Camera far plane that keeps the whole horizon in view, with room to spare.</summary>
    [JsonIgnore]
    public float CameraFar => Math.Max(20_000f, (HorizonKm + 10) * 1000f);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Reads the file (if any), then the command line. Call once at boot.</summary>
    public static void Load()
    {
        // a phone's first launch starts from its own defaults (#63); a saved file keeps what it says
        var loaded = new GameSettings();
        if (Platform.IsMobile && !Godot.FileAccess.FileExists(File)) loaded.UsePhoneDefaults();
        // a fresh install: the welcome (#1131) runs before the title
        if (!Godot.FileAccess.FileExists(File)) loaded.FirstRunDone = false;
        try
        {
            if (Godot.FileAccess.FileExists(File))
            {
                using var file = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Read);
                string text = file.GetAsText();
                loaded = JsonSerializer.Deserialize<GameSettings>(text, JsonOptions) ?? loaded;
                // before #261 the radios' volume lived in radio.cfg: carried over once
                if (!text.Contains("\"musicVolume\"", StringComparison.OrdinalIgnoreCase) && OldRadioVolume() is float old)
                    loaded.MusicVolume = old;
                if (!text.Contains("\"cartoonDefault\"", StringComparison.OrdinalIgnoreCase) && loaded.VisualStyle == Styles.VisualStyle.Ps1)
                    loaded.VisualStyle = Styles.StyleKit.Default;
                if (Platform.IsStandaloneVr) loaded.MigrateStandalone();
            }
            else if (OldRadioVolume() is float old) loaded.MusicVolume = old;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[settings] could not read {File}: {e.Message}");
        }

        loaded.Clamp();
        loaded.ApplyCommandLine(CmdArgs.All);
        // a probe that asked for the generated world gets it whatever the player's saved toggle
        // says (#666: a machine with "Generated terrain" off never loaded a near tile)
        if (WantsGeneratedWorld(CmdArgs.All)) loaded.GeneratedFill = true;
        Current = loaded;
        GD.Print($"[settings] rings={loaded.RenderDistanceRings} horizon={loaded.HorizonKm}km "
            + $"detail={loaded.Detail} fog={loaded.Fog} builds={loaded.MaxConcurrentBuilds} "
            + $"commit={loaded.CommitBudgetMs}ms scale={loaded.RenderScale} aa={loaded.AntiAliasing} vsync={loaded.VSync} "
            + $"window={loaded.WindowMode} perf={loaded.PerfOverlay}");
    }

    /// <summary>
    /// What a phone starts with (#63), and what Settings → Performance → "Use phone defaults" puts
    /// back: about a third of the desktop's world in view, a low LOD table, a shorter horizon, a
    /// lower 3D resolution, less traffic and no cockpit mirrors (each one another camera).
    /// </summary>
    public void UsePhoneDefaults()
    {
        RenderDistanceRings = 8;
        HorizonKm = 25;
        Detail = DetailPreset.Low;
        RenderScale = 0.6f;
        TrafficCars = 10;
        TrafficRiders = 8;
        CockpitMirrors = false;
        CommitBudgetMs = 3;
        MaxConcurrentBuilds = 0; // auto, which ChunkManager caps on a phone
        CacheGb = 0.5f;
        if (Platform.IsStandaloneVr) UseStandaloneVrDefaults();
    }

    /// <summary>
    /// A standalone headset's first launch (#1112, docs/notes/xr/quest-standalone.md), measured on a Quest 2:
    /// its GPU sorts every triangle into screen bins and replays every draw in each, so fewer tiles
    /// (each a draw) and fewer triangles count more than pixels; traffic is its CPU's physics.
    /// </summary>
    private void UseStandaloneVrDefaults()
    {
        StandaloneDefaults = LatestStandaloneDefaults;
        VrDetail = DetailPreset.VeryLow;   // Low's 1.5 M triangles near the player held it to 10 fps
        RenderDistanceRings = 3;           // the horizon's merged blocks beyond
        VrMsaa = 2;                        // 4x halves the screen bins' size: ~10 ms more
        TrafficCars = 6;
        TrafficRiders = 4;
        ReplayMinutes = 1;                 // the replay holds what it saw that long (Movie/WorldRecorder): memory
    }

    /// <summary>
    /// Which of a standalone headset's defaults a saved file has had (#1112): a file saved before a default changed
    /// is given the new value once, for that setting alone, and keeps every other choice. 1: a minute of replay
    /// (the recorder's memory) and SpaceWarp off (it flashed black in one eye).
    /// </summary>
    public int StandaloneDefaults { get; set; }
    private const int LatestStandaloneDefaults = 1;

    /// <summary>Brings a headset's saved settings up to <see cref="LatestStandaloneDefaults"/>, written to the file at once.</summary>
    private void MigrateStandalone()
    {
        if (StandaloneDefaults >= LatestStandaloneDefaults) return;
        if (StandaloneDefaults < 1)
        {
            ReplayMinutes = 1;
            VrSpaceWarp = false;
            SaveOnly(nameof(ReplayMinutes), ReplayMinutes);
            SaveOnly(nameof(VrSpaceWarp), VrSpaceWarp);
        }
        StandaloneDefaults = LatestStandaloneDefaults;
        SaveOnly(nameof(StandaloneDefaults), StandaloneDefaults);
        GD.Print($"[settings] standalone defaults brought to {LatestStandaloneDefaults}: replay {ReplayMinutes} min, SpaceWarp off");
    }

    /// <summary>Writes the touch layout alone, as the editor's Done does (a command-line run's other values stay out).</summary>
    public static void SaveTouchOverrides() =>
        SaveOnly(nameof(TouchOverrides), JsonSerializer.SerializeToNode(Current.TouchOverrides, JsonOptions)!);

    public void Save()
    {
        try
        {
            JsonStore.Save(File, this, JsonOptions);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[settings] could not write {File}: {e.Message}");
        }
    }

    /// <summary>The radio panel's volume as it was kept before the Music bus (#261), if that file is there.</summary>
    private static float? OldRadioVolume()
    {
        var cfg = new ConfigFile();
        return cfg.Load("user://radio.cfg") == Error.Ok ? Math.Clamp(cfg.GetValue("radio", "volume", 0.7f).AsSingle(), 0f, 1f) : null;
    }

    /// <summary>
    /// Writes one setting into the file without the rest of this run's values (a command-line
    /// <c>--view</c> or <c>--traffic</c> must not become the saved choice): the radio panel's
    /// volume slider, saved as it is dragged; the tutorial's done flag.
    /// </summary>
    public static void SaveOnly(string key, System.Text.Json.Nodes.JsonNode value)
    {
        try
        {
            var root = Godot.FileAccess.FileExists(File)
                ? System.Text.Json.Nodes.JsonNode.Parse(Godot.FileAccess.GetFileAsString(File)) as System.Text.Json.Nodes.JsonObject
                : null;
            root ??= System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new GameSettings(), JsonOptions)) as System.Text.Json.Nodes.JsonObject;
            if (root == null) return;
            string name = JsonOptions.PropertyNamingPolicy?.ConvertName(key) ?? key;
            foreach (var existing in root.Select(kv => kv.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
                root.Remove(existing);
            root[name] = value;
            JsonStore.Save(File, root, JsonOptions);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[settings] could not write {File}: {e.Message}");
        }
    }

    /// <summary>Something not saved changed what the settings mean (the data saver): tell the world.</summary>
    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>Applies a change made in the UI: clamps, notifies the world, persists.</summary>
    public void Commit()
    {
        Clamp();
        Changed?.Invoke();
        Save();
    }

    private void Clamp()
    {
        RenderDistanceRings = Math.Clamp(RenderDistanceRings, MinRingsHere, MaxRings);
        HorizonKm = Math.Clamp(HorizonKm, 0, MaxHorizonKm);
        MaxConcurrentBuilds = Math.Clamp(MaxConcurrentBuilds, 0, MaxBuildsCap);
        CommitBudgetMs = Math.Clamp(CommitBudgetMs, 1, 16);
        CacheGb = Math.Clamp(CacheGb, 0.1f, 20f);
        RenderScale = Math.Clamp(RenderScale, MinRenderScale, MaxRenderScale);
        ConcertScreenWidth = ConcertScreenWidth switch { <= 384 => 256, <= 768 => 512, _ => 1024 };
        VrMsaa = VrMsaa switch { <= 0 => 0, <= 2 => 2, <= 4 => 4, _ => 8 };
        VrRenderScale = Math.Clamp(VrRenderScale, MinVrRenderScale, MaxVrRenderScale);
        VrResolutionFloor = Math.Clamp(VrResolutionFloor, MinVrResolutionFloor, 1f);
        if (Array.IndexOf(VrRefreshRates, VrRefreshHz) < 0) VrRefreshHz = 72;
        StickSensitivity = Math.Clamp(StickSensitivity, 0.2f, 3f);
        TouchLookSpeed = Math.Clamp(TouchLookSpeed, 0.3f, 4f);
        TouchScale = Math.Clamp(TouchScale, 0.75f, TouchLayout.MaxScale);
        TouchOpacity = Math.Clamp(TouchOpacity, 0.2f, 1f);
        TouchPins ??= new();
        TouchOverrides ??= new();
        MasterVolume = Math.Clamp(MasterVolume, 0f, 1f);
        SfxVolume = Math.Clamp(SfxVolume, 0f, 1f);
        AmbienceVolume = Math.Clamp(AmbienceVolume, 0f, 1f);
        MusicVolume = Math.Clamp(MusicVolume, 0f, 1f);
        DayLengthMinutes = Math.Clamp(DayLengthMinutes, 0f, 240f);
        StartHour = Math.Clamp(StartHour, 0f, 23.99f);
        TrafficCars = Math.Clamp(TrafficCars, 0, 150);
        TrafficWalkers = Math.Clamp(TrafficWalkers, 0, 300);
        TrafficRiders = Math.Clamp(TrafficRiders, 0, 150);
        Trams = Math.Clamp(Trams, 0, 20);
        ScreenShake = Math.Clamp(ScreenShake, 0f, 1f);
        ReplayMinutes = Math.Clamp(ReplayMinutes, 0, 15);
        StickDeadzone = Math.Clamp(StickDeadzone, 0.05f, 0.5f);
        if (VrSnapDegrees is not (0 or 15 or 30 or 45)) VrSnapDegrees = 30;
        VrVignette = Math.Clamp(VrVignette, 0f, 1f);
        CockpitFov = Math.Clamp(CockpitFov, 50f, 100f);
        SeatHeight = Math.Clamp(SeatHeight, -0.1f, 0.1f);
        SeatForward = Math.Clamp(SeatForward, -0.15f, 0.15f);
        WindowWidth = Math.Clamp(WindowWidth, 0, 7680);
        WindowHeight = Math.Clamp(WindowHeight, 0, 4320);
        OccasionPreferences ??= new();
        RecentGpx ??= new();
        PlayerName ??= "";
        VehicleIntrosSeen ??= new();
        Wheel ??= new();
        Wheel.Clamp();
    }

    /// <summary>
    /// "--rings N", "--horizon km", "--fog on|off", "--detail low|medium|high",
    /// "--generated on|off", "--generated-roads raw|on", "--style ps1|cartoon|real-|real+" — for
    /// screenshotting one configuration against another without touching the saved file.
    /// </summary>
    private static bool WantsGeneratedWorld(string[] args)
    {
        if (args.Contains("--generated")) return false;   // explicit: ApplyCommandLine has the last word
        return args.Contains("--generated-world") || args.Contains("--shot") || args.Contains("--shot-queue")
            || (Systems.Narrowed && Systems.On(Systems.Generated));
    }

    private void ApplyCommandLine(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            string v = args[i + 1];
            switch (args[i])
            {
                case "--rings" when int.TryParse(v, out int r): RenderDistanceRings = r; break;
                case "--horizon" when int.TryParse(v, out int h): HorizonKm = h; break;
                case "--fog": Fog = v != "off" && v != "0" && v != "false"; break;
                case "--generated": GeneratedFill = v != "off" && v != "0" && v != "false"; break;
                // raw: generated roads without the road network stage (#559), to compare; not saved
                case "--generated-roads": Terrain.FallbackChunkSource.RewriteRoads = v != "raw" && v != "off"; break;
                case "--detail" when Enum.TryParse<DetailPreset>(v, true, out var d): Detail = d; break;
                case "--data" when Enum.TryParse<DataPreset>(v, true, out var dp): Data = dp; break;
                case "--builds" when int.TryParse(v, out int b): MaxConcurrentBuilds = b; break;
                // #880: this run's figure, an alien preset by name (zorp, zib…) or packed bits, for a screenshot; not saved
                case "--appearance" when Avatar.Aliens.Named(v) is { } alien: AppearanceBits = alien.Looks.Pack(); break;
                case "--appearance" when int.TryParse(v, out int bits): AppearanceBits = bits; break;
                case "--commit" when double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double c):
                    CommitBudgetMs = c; break;
                case "--profile" when Enum.TryParse<RideProfile>(v, true, out var rp): RideProfile = rp; break;
                case "--rider-kg" when float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float kg):
                    RiderMass = Mathf.Clamp(kg, 30f, 200f); break;
                case "--rider-cp" when float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float cp):
                    RiderCriticalPower = Mathf.Clamp(cp, 50f, 600f); break;
                case "--tyrewear": TyreWear = v is "on" or "1" or "true"; break;
                case "--brakewear": BrakeWear = v is "on" or "1" or "true"; break;
                case "--boardwalkable": BoardWalkableFromOutside = v is "on" or "1" or "true"; break;
                case "--airliner" when Enum.TryParse<Player.AirlinerHandling>(v, true, out var ah): Airliner = ah; break;
                case "--gearbox":
                    HeavyGearbox = v.ToLowerInvariant() switch
                    {
                        "seq" => Player.HeavyShift.Sequential, "seqclutch" => Player.HeavyShift.SequentialClutch,
                        "hsplit" => Player.HeavyShift.HPatternSplitter, "h" => Player.HeavyShift.HPattern,
                        _ => Player.HeavyShift.Automatic,
                    };
                    break;
                case "--cargearbox":
                    CarGearbox = v.ToLowerInvariant() switch
                    {
                        "seq" => Player.CarGearbox.Sequential, "manual" or "h" => Player.CarGearbox.Manual,
                        _ => Player.CarGearbox.Automatic,
                    };
                    break;
                case "--wheel": Wheel.Enabled = v is "on" or "1" or "true"; break;
                case "--wheelrange" when float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float deg):
                    Wheel.RangeDeg = deg; break;
                case "--voice":
                    EngineVoice = v.ToLowerInvariant() switch
                    {
                        "ps1" => Audio.EngineVoice.Ps1, "nes" => Audio.EngineVoice.Nes,
                        "sid" => Audio.EngineVoice.Sid, "genesis" => Audio.EngineVoice.Genesis,
                        _ => Audio.EngineVoice.Realistic,
                    };
                    break;
                case "--style":
                    Styles.StyleKit.TryParse(v, out var style);
                    VisualStyle = style;
                    break;
                // a fixed time of day, for screenshots: --time 21.5 is half past nine at night
                case "--time" when float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float hour):
                    StartHour = hour; DayLengthMinutes = 0; break;
                case "--traffic" when int.TryParse(v, out int cars): TrafficCars = cars; break;
                // the traffic modes (#1060): walkers, riders, trams for this run
                case "--walkers" when int.TryParse(v, out int walkers): TrafficWalkers = walkers; break;
                // (--bikes: --riders already counts a --drivecheck's racers)
                case "--bikes" when int.TryParse(v, out int riders): TrafficRiders = riders; break;
                case "--trams" when int.TryParse(v, out int trams): Trams = trams; break;
                // first | third, or in a car's cockpit with (body) or without (bare) your own figure
                case "--view":
                    ThirdPerson = v is not ("first" or "1st" or "body" or "bare");
                    if (v is "body" or "bare") CockpitBody = v == "body";
                    break;
                // left | right: the on-foot camera's shoulder (#460)
                case "--shoulder": LeftShoulder = v is "left" or "l"; break;
                case "--tailfirstpigeon": TailFirstPigeon = v is "on" or "1" or "true"; break;
                // regular | goofy: the snowboard's stance (#922)
                case "--stance": Goofy = v is "goofy" or "g"; break;
                case "--mirrors": CockpitMirrors = v is "on" or "1" or "true"; break;
                // what the monitor shows in VR (#186): off | first | eyes | third
                case "--vrmonitor":
                    VrMonitor = v switch { "off" => XR.MonitorView.Off, "eyes" => XR.MonitorView.BothEyes,
                        "third" => XR.MonitorView.ThirdPerson, _ => XR.MonitorView.FirstPerson };
                    break;
                // the headset's refresh rate and terrain detail (#1036), for frame-time checks
                case "--vrhz" when int.TryParse(v, out int hz): VrRefreshHz = hz; break;
                case "--vrdetail" when Enum.TryParse<DetailPreset>(v, true, out var vd): VrDetail = vd; break;
                // the headset's picture, for frame-time checks on a Quest (#1112)
                case "--vrmsaa" when int.TryParse(v, out int ms): VrMsaa = ms; break;
                case "--vrscale" when float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float vs):
                    VrRenderScale = Math.Clamp(vs, MinVrRenderScale, MaxVrRenderScale); break;
                case "--vrfoveation": VrFoveation = v is "on" or "1" or "true"; break;
                case "--spacewarp": VrSpaceWarp = v is "on" or "1" or "true"; break;
                case "--replayminutes" when int.TryParse(v, out int replay): ReplayMinutes = replay; break;
                case "--vrfloor" when float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float vf):
                    VrResolutionFloor = Math.Clamp(vf, MinVrResolutionFloor, 1f); break;
                case "--vsync": VSync = v is "on" or "1" or "true"; break;
                // a film's render profile sets the picture itself (#924, tools/trailer.sh RENDER), never the player's file
                case "--render-scale" when float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float rs):
                    RenderScale = Math.Clamp(rs, MinRenderScale, MaxRenderScale); break;
                case "--aa" when Enum.TryParse<AntiAliasing>(v, true, out var aa): AntiAliasing = aa; break;
                case "--perf":
                    PerfOverlay = v switch { "full" or "detailed" => PerfOverlayMode.Detailed,
                        "fps" => PerfOverlayMode.Fps, _ => PerfOverlayMode.Off };
                    break;
            }
        }
        Clamp();
    }
}
