using Godot;
using UnitSport.Avatar;

namespace UnitSport.Avatar
{
    public sealed record BikePalette(Color Frame, Color Accent, Color Tyre, Color Rim, Color Saddle)
    {
        /// <summary>Logos and lettering on the frame; the frame's own colour when a bike has none.</summary>
        public Color Decal { get; init; } = Frame;

        public static readonly BikePalette Default = new(
            Frame: new Color(0.12f, 0.42f, 0.68f),
            Accent: new Color(0.85f, 0.86f, 0.88f),
            Tyre: new Color(0.10f, 0.10f, 0.11f),
            Rim: new Color(0.62f, 0.63f, 0.65f),
            Saddle: new Color(0.09f, 0.09f, 0.10f));

        /// <summary>
        /// Every road bike is the catalogue's bike in its own colours (#1124): riders are told apart
        /// by their kit, as on a real group ride, not by a frame repainted per rider.
        /// </summary>
        public static BikePalette ForRider(int index) => Player.BicycleCatalog.Default.Look;
    }
}

namespace UnitSport.Player
{
    public enum BikeBrakes { Rim, Disc }

    /// <summary>The bar the hands are on (#1159): a road bike's drops, or a trekking bike's swept riser bar.</summary>
    public enum BikeBar { Drop, Riser }

    /// <summary>
    /// A pedelec's mid motor (#1159): it adds <see cref="Support"/> × the rider's own watts, up to its
    /// torque at the cranks and its peak power, and stops helping at <see cref="CutOff"/> (25 km/h,
    /// the Swiss "slow e-bike" that needs no plate).
    /// </summary>
    /// <param name="Torque">At the cranks, N·m (the maker's figure).</param>
    /// <param name="PeakWatts">The motor's short-term most, W.</param>
    /// <param name="Support">The assist mode's factor: Bosch Eco 0.6, Tour 1.4, Sport 2.25, Turbo 3.4.</param>
    /// <param name="CutOff">Assist off at, m/s.</param>
    /// <param name="BatteryWh">The battery, Wh (described, not drained yet).</param>
    public sealed record ElectricAssist(string Motor, float Torque, float PeakWatts, float Support, float CutOff, float BatteryWh)
    {
        /// <summary>The help fades over the last km/h before the cut-off, as the Bosch firmware does.</summary>
        public const float FadeBand = 1f / 3.6f;
    }

    /// <summary>
    /// The main frame (#1159): a diamond's top tube from the head tube to the seat cluster, or a
    /// low frame (Moustache's "Open") whose second tube runs low from the head tube to the
    /// seat tube just over the motor, so a leg steps over it rather than swinging over the saddle.
    /// </summary>
    public enum BikeFrameStyle { Diamond, LowFrame }

    /// <summary>What a city or trekking bike carries that a road bike does not (#1159): drawn by <c>BikeMeshBuilder</c>.</summary>
    /// <param name="SuspensionFork">Travel, m; 0 a rigid fork.</param>
    public sealed record BikeKit(bool Rack, bool Fenders, bool Lights, bool Kickstand, float SuspensionFork, bool SuspensionSeatpost);

    /// <summary>
    /// A frame's geometry as the maker's chart prints it, metres and degrees, plus the cockpit it
    /// is built with (#1124). The chart's numbers are what is typed; every point of the bike is
    /// derived from them in <see cref="BikeFrame"/>.
    /// </summary>
    /// <param name="Stack">Bottom bracket to the top of the head tube, vertically.</param>
    /// <param name="Reach">Bottom bracket to the top of the head tube, horizontally.</param>
    /// <param name="HeadAngle">Head tube against the ground, degrees.</param>
    /// <param name="SeatAngle">Seat tube against the ground, degrees.</param>
    /// <param name="BbDrop">How far the bottom bracket sits below the axles.</param>
    /// <param name="ForkOffset">The fork's rake: the axle ahead of the steering axis, square to it.</param>
    /// <param name="Wheelbase">The chart's, kept to check the derived one against.</param>
    /// <param name="StemAngle">Against square to the steerer, degrees (−6 is the usual road stem).</param>
    /// <param name="Spacers">Headset cap and spacers under the stem.</param>
    /// <param name="BarReach">Bar clamp forward to the hoods.</param>
    /// <param name="BarDrop">Tops down to the drops.</param>
    /// <param name="BarWidth">Centre to centre at the hoods, or at the grips on a riser bar.</param>
    /// <param name="Bar">A riser bar (#1159) reads <paramref name="BarReach"/> as the grips' sweep (negative: back
    /// toward the rider) and <paramref name="BarDrop"/> as their rise above the clamp.</param>
    public sealed record BicycleGeometry(
        float Stack, float Reach, float HeadAngle, float SeatAngle, float HeadTube, float SeatTube,
        float Chainstay, float BbDrop, float ForkOffset, float Wheelbase, float CrankLength,
        float StemLength, float StemAngle, float Spacers, float BarReach, float BarDrop, float BarWidth,
        BikeBar Bar = BikeBar.Drop);

    /// <summary>One bicycle: a real model, size and build (#1124).</summary>
    /// <param name="Mass">The bike alone, kg, pedals on.</param>
    /// <param name="TyreWidth">As on the sidewall, metres (28 mm = 0.028).</param>
    /// <param name="WheelInertia">Both wheels' moment of inertia with tyres and rotors, kg·m²: a 700c road wheel is 0.06-0.09.</param>
    /// <param name="Kind">The ride it is (#1159): one <see cref="RideKind"/> per catalogue bike, so the choice replicates as the ride.</param>
    /// <param name="DragArea">The 72.5 kg reference rider's CdA on it, m²: 0.32 on a road bike's hoods, ~0.5 sat up on a trekking bike.</param>
    public sealed record BicycleSpec(
        string Name, string Size, float Mass, float TyreWidth, BicycleGeometry Geometry,
        int[] Chainrings, int[] Cassette, string Groupset, BikeBrakes Brakes, BikePalette Look, float WheelInertia = 0.15f,
        RideKind Kind = RideKind.RoadBike, float DragArea = BikePhysics.ReferenceDragArea)
    {
        /// <summary>The motor, on a pedelec (#1159); null on a bike the legs alone drive.</summary>
        public ElectricAssist? Assist { get; init; }

        /// <summary>Diamond or low frame (#1159).</summary>
        public BikeFrameStyle FrameStyle { get; init; } = BikeFrameStyle.Diamond;

        /// <summary>Rack, fenders, lights, suspension (#1159); null on a bare road bike.</summary>
        public BikeKit? Kit { get; init; }

        /// <summary>The picker's and the HUD's name.</summary>
        public string Label { get; init; } = "Road bike";

        /// <summary>The picker's line under the name.</summary>
        public string Blurb { get; init; } = "";

        /// <summary>Sat up on a riser bar: no drops to get down on, no tuck.</summary>
        public bool Upright => Geometry.Bar == BikeBar.Riser;

        /// <summary>
        /// What the spinning wheels add to the mass being accelerated, kg: I/r². Every kilo at the rim
        /// counts about twice, which is why light wheels matter and a light frame much less.
        /// </summary>
        public float WheelEffectiveMass => WheelInertia / (WheelRadius * WheelRadius);

        /// <summary>A 622 mm (700c) bead seat with the tyre's height on it: about the width, for a road tyre.</summary>
        public float WheelRadius => 0.311f + TyreWidth;

        /// <summary>Distance rolled per wheel turn, m.</summary>
        public float Circumference => Mathf.Tau * WheelRadius;
    }

    /// <summary>
    /// The bicycles there are (#1124). Entry 0 is the road bike everyone rides.
    /// </summary>
    public static class BicycleCatalog
    {
        /// <summary>
        /// The owner's own bike: Scott Addict RC 15 (2018), size S (52), black. Ultegra Di2,
        /// hydraulic discs, 52/36 with an 11-28 cassette, ~8.0 kg.
        /// <para>
        /// Geometry from the 2018 Addict RC size S chart as Bike Insights and The Pro's Closet print
        /// it: stack 526.7, reach 384, head 72°, seat 74°, head tube 120, chainstay 405, wheelbase
        /// 980, fork offset 43.6. <b>Assumed</b> until measured on the bike: 70 mm BB drop, 700×28c
        /// tyres (the disc RC 15's stock), 172.5 mm cranks, a 100 mm −6° stem on 20 mm of spacers,
        /// a 400 mm bar with 80 mm reach and 125 mm drop.
        /// </para>
        /// </summary>
        public static readonly BicycleSpec AddictRc15 = new(
            Name: "Scott Addict RC 15 (2018)", Size: "S/52", Mass: 8.0f, TyreWidth: 0.028f,
            Geometry: new BicycleGeometry(
                Stack: 0.5267f, Reach: 0.384f, HeadAngle: 72f, SeatAngle: 74f, HeadTube: 0.120f, SeatTube: 0.46f,
                Chainstay: 0.405f, BbDrop: 0.070f, ForkOffset: 0.0436f, Wheelbase: 0.980f, CrankLength: 0.1725f,
                StemLength: 0.100f, StemAngle: -6f, Spacers: 0.020f, BarReach: 0.080f, BarDrop: 0.125f, BarWidth: 0.40f),
            Chainrings: new[] { 52, 36 },
            Cassette: new[] { 11, 12, 13, 14, 15, 17, 19, 21, 23, 25, 28 },
            Groupset: "Shimano Ultegra Di2 R8050, hydraulic disc",
            Brakes: BikeBrakes.Disc,
            Look: new BikePalette(
                Frame: new Color(0.045f, 0.045f, 0.05f),
                Accent: new Color(0.16f, 0.16f, 0.17f),
                Tyre: new Color(0.07f, 0.07f, 0.075f),
                Rim: new Color(0.06f, 0.06f, 0.065f),
                Saddle: new Color(0.05f, 0.05f, 0.055f)) { Decal = new Color(0.80f, 0.80f, 0.82f) })
        {
            Label = "Road bike",
            Blurb = "Scott Addict RC 15, 8 kg of carbon: your legs and nothing else",
        };

        /// <summary>
        /// Moustache Samedi 28.4 (2025), size M (47), metallic red glossy (#1159): a trekking pedelec.
        /// Bosch Performance Line Smart System, 75 N·m, assist to 25 km/h, PowerTube 625 Wh in the down
        /// tube; Shimano CUES 10-speed, 11-39; 170 mm cranks; Schwalbe Big Apple 700×50C; SR Suntour
        /// NEX-E25 fork (63 mm); a suspension seatpost (40 mm); hydraulic discs (Shimano MT200, 180/160);
        /// aluminium rack, fenders, battery-powered lights (AXA NXT, Spanninga), kickstand; 27.2 kg.
        /// <para>
        /// The parts list is the 2025 dealer sheet's (pedali.de); the riser bar's 80 mm rise is the
        /// Samedi 28's. <b>Assumed</b>, because Moustache only prints its chart from the shop page's
        /// script: a trekking frame's stack 625, reach 400, head 69.5°, seat 73.5°, head tube 160,
        /// chainstay 475 (a Bosch motor's long stays), BB drop 65, fork offset 50 (the 700c NEX's),
        /// which derive a 1133 mm wheelbase and 82 mm of trail; a 38 t ring (Bosch's stock sprocket);
        /// a 70 mm +25° stem on 30 mm of spacers; 580 mm between the grips, swept back 90 mm.
        /// Sport mode (225%) is the one assist level, with no key to change it.
        /// </para>
        /// </summary>
        public static readonly BicycleSpec MoustacheSamedi = new(
            Name: "Moustache Samedi 28.4 (2025)", Size: "M/47", Mass: 27.2f, TyreWidth: 0.050f,
            Geometry: new BicycleGeometry(
                Stack: 0.625f, Reach: 0.400f, HeadAngle: 69.5f, SeatAngle: 73.5f, HeadTube: 0.160f, SeatTube: 0.47f,
                Chainstay: 0.475f, BbDrop: 0.065f, ForkOffset: 0.050f, Wheelbase: 1.133f, CrankLength: 0.170f,
                StemLength: 0.070f, StemAngle: 25f, Spacers: 0.030f, BarReach: -0.090f, BarDrop: 0.080f, BarWidth: 0.58f,
                Bar: BikeBar.Riser),
            Chainrings: new[] { 38 },
            Cassette: new[] { 11, 13, 15, 18, 21, 24, 28, 32, 36, 39 },
            Groupset: "Shimano CUES U6020 10-speed, hydraulic disc",
            Brakes: BikeBrakes.Disc,
            Look: new BikePalette(
                Frame: new Color(0.62f, 0.05f, 0.07f),
                Accent: new Color(0.13f, 0.13f, 0.14f),
                Tyre: new Color(0.09f, 0.09f, 0.09f),
                Rim: new Color(0.11f, 0.11f, 0.12f),
                Saddle: new Color(0.16f, 0.12f, 0.10f)) { Decal = new Color(0.90f, 0.90f, 0.88f) },
            // two 700c wheels with 50 mm balloon tyres, 32 spokes and rotors: ~2.4 kg each, most at the rim
            WheelInertia: 0.26f,
            Kind: RideKind.EBike,
            DragArea: 0.50f)
        {
            Label = "E-bike",
            Blurb = "Moustache Samedi 28.4, red: a Bosch motor helps your legs up to 25 km/h",
            Assist = new ElectricAssist("Bosch Performance Line Smart System", Torque: 75f, PeakWatts: 600f, Support: 2.25f,
                CutOff: 25f / 3.6f, BatteryWh: 625f),
            Kit = new BikeKit(Rack: true, Fenders: true, Lights: true, Kickstand: true, SuspensionFork: 0.063f, SuspensionSeatpost: true),
        };

        /// <summary>
        /// The Samedi 28.4 <b>Open</b> (2025), the low frame (#1159): the same motor,
        /// battery, parts and colour, the top tube swapped for a low one from the head tube to the seat
        /// tube. <b>Assumed</b>: the diamond's chart and weight, Moustache printing the Open's from its
        /// script too; its seat tube is the same length, so the fit is the same.
        /// </summary>
        public static readonly BicycleSpec MoustacheSamediOpen = MoustacheSamedi with
        {
            Name = "Moustache Samedi 28.4 Open (2025)",
            Kind = RideKind.EBikeOpen,
            FrameStyle = BikeFrameStyle.LowFrame,
            Label = "E-bike (low frame)",
            Blurb = "Moustache Samedi 28.4 Open, red, low frame: step through it, the motor helps up to 25 km/h",
        };

        /// <summary>Append-only: each entry's <see cref="BicycleSpec.Kind"/> is replicated.</summary>
        public static readonly BicycleSpec[] All = { AddictRc15, MoustacheSamedi, MoustacheSamediOpen };

        public static BicycleSpec Default => All[0];

        /// <summary>The catalogue bike a ride kind is, or null (#1159).</summary>
        public static BicycleSpec? For(RideKind kind)
        {
            foreach (var spec in All)
                if (spec.Kind == kind) return spec;
            return null;
        }

        /// <summary>A bicycle of any make (#1159): what "is this a bike" asks, never <c>== RideKind.RoadBike</c>.</summary>
        public static bool IsBicycle(RideKind kind) => For(kind) != null;
    }

    /// <summary>
    /// Every point of a bicycle and of the rider fitted to it, derived from a <see cref="BicycleSpec"/>
    /// (#1124). Author space as the meshes: +Z forward, origin on the ground with the bottom bracket
    /// 2 cm behind it.
    /// <para>
    /// The fit is a bike fitter's, not an eyeball: the saddle goes up the seat tube's line until the
    /// leg, pedal at the bottom of the stroke, is bent 35° at the knee; the hands are on the hoods.
    /// The rider's upper body is then solved from those points (<c>HumanMeshBuilder</c>), so a frame
    /// with a longer reach stretches the rider rather than leaving their hands in the air.
    /// </para>
    /// </summary>
    public sealed class BikeFrame
    {
        /// <summary>The figure's thigh and shin (<c>HumanMeshBuilder</c>'s standing rig), m.</summary>
        public const float ThighLength = 0.435f, ShinLength = 0.415f;

        /// <summary>Knee bend with the pedal at the bottom: 25-35° is the fitter's window.</summary>
        private const float KneeBendAtBottom = 35f;

        /// <summary>Pedal from the bike's centre plane (half the Q-factor).</summary>
        public const float PedalOffset = 0.070f;

        /// <summary>The figure's leg hip joints from the centre plane.</summary>
        public const float HipHalfWidth = 0.0896f;

        private const float BottomBracketZ = -0.020f;

        public BicycleSpec Spec { get; }
        public float WheelRadius { get; }
        public float CrankLength { get; }
        public Vector3 BottomBracket { get; }
        public Vector3 RearAxle { get; }
        public Vector3 FrontAxle { get; }
        public Vector3 HeadTop { get; }
        public Vector3 HeadBottom { get; }
        /// <summary>Where the top tube meets the seat tube.</summary>
        public Vector3 SeatCluster { get; }
        /// <summary>Unit, up the steerer.</summary>
        public Vector3 SteererUp { get; }
        /// <summary>Unit, up the seat tube.</summary>
        public Vector3 SeatUp { get; }
        /// <summary>Top of the stem's steerer clamp.</summary>
        public Vector3 StemBase { get; }
        public Vector3 BarClamp { get; }
        /// <summary>The +X hood's top, or the +X grip's middle on a riser bar; the other is mirrored.</summary>
        public Vector3 Hood { get; }
        /// <summary>The +X drop's end.</summary>
        public Vector3 Drop { get; }
        /// <summary>The leg hip joints' centre: the fit's saddle point.</summary>
        public Vector3 Hip { get; }
        /// <summary>Top of the saddle, under the sit bones.</summary>
        public Vector3 SaddleTop { get; }
        /// <summary>The +X wrist, hands on the hoods.</summary>
        public Vector3 Wrist { get; }
        /// <summary>The wheelbase this geometry gives, to check against the chart's.</summary>
        public float Wheelbase => FrontAxle.Z - RearAxle.Z;
        /// <summary>Ground to steering axis, ahead of the front contact: what makes a bike self-steer.</summary>
        public float Trail { get; }

        public static BikeFrame Default { get; } = new(BicycleCatalog.Default);

        private static readonly System.Collections.Generic.Dictionary<BicycleSpec, BikeFrame> Frames = new() { [BicycleCatalog.Default] = Default };

        /// <summary>One frame per catalogue bike, shared (#1159): the rig and the meshes are cached on it.</summary>
        public static BikeFrame For(BicycleSpec spec)
        {
            lock (Frames)
            {
                if (!Frames.TryGetValue(spec, out var frame)) Frames[spec] = frame = new BikeFrame(spec);
                return frame;
            }
        }

        public BikeFrame(BicycleSpec spec)
        {
            Spec = spec;
            var g = spec.Geometry;
            WheelRadius = spec.WheelRadius;
            CrankLength = g.CrankLength;

            BottomBracket = new Vector3(0, WheelRadius - g.BbDrop, BottomBracketZ);
            RearAxle = new Vector3(0, WheelRadius, BottomBracketZ - Mathf.Sqrt(g.Chainstay * g.Chainstay - g.BbDrop * g.BbDrop));

            float head = Mathf.DegToRad(g.HeadAngle);
            SteererUp = new Vector3(0, Mathf.Sin(head), -Mathf.Cos(head));
            HeadTop = BottomBracket + new Vector3(0, g.Stack, g.Reach);
            HeadBottom = HeadTop - SteererUp * g.HeadTube;

            // the steering axis down to axle height, then the offset square to it, which at a fixed
            // height is offset / sin(head) further forward
            float down = (HeadTop.Y - WheelRadius) / SteererUp.Y;
            var onAxis = HeadTop - SteererUp * down;
            FrontAxle = onAxis + new Vector3(0, 0, g.ForkOffset / Mathf.Sin(head));
            Trail = (WheelRadius * Mathf.Cos(head) - g.ForkOffset) / Mathf.Sin(head);

            float seat = Mathf.DegToRad(g.SeatAngle);
            SeatUp = new Vector3(0, Mathf.Sin(seat), -Mathf.Cos(seat));
            SeatCluster = BottomBracket + SeatUp * g.SeatTube;

            // the saddle: along the seat tube's line until hip to the bottom pedal is the leg bent
            // KneeBendAtBottom, i.e. |BB + s·up − (BB − crank·Y)| = leg → s² + 2·s·crank·up.y + crank² − leg² = 0
            float bend = Mathf.DegToRad(180f - KneeBendAtBottom);
            float leg = Mathf.Sqrt(ThighLength * ThighLength + ShinLength * ShinLength - 2f * ThighLength * ShinLength * Mathf.Cos(bend));
            float b = CrankLength * SeatUp.Y;
            float s = -b + Mathf.Sqrt(b * b - CrankLength * CrankLength + leg * leg);
            Hip = BottomBracket + SeatUp * s;
            SaddleTop = Hip + new Vector3(0, -0.005f, -0.005f);

            StemBase = HeadTop + SteererUp * g.Spacers;
            // square to the steerer, forward, then the stem's own angle: −6° on a 72° head is ~12° up
            float stem = Mathf.DegToRad(90f - g.HeadAngle + g.StemAngle);
            BarClamp = StemBase + SteererUp * 0.02f + new Vector3(0, Mathf.Sin(stem), Mathf.Cos(stem)) * g.StemLength;
            if (g.Bar == BikeBar.Riser)
            {
                // a riser bar (#1159): the grips up and swept back; "Hood" is the grip's middle, no drops
                Hood = BarClamp + new Vector3(g.BarWidth / 2f, g.BarDrop, g.BarReach);
                Drop = Hood;
                Wrist = Hood + new Vector3(0, 0.020f, -0.010f);
            }
            else
            {
                Hood = BarClamp + new Vector3(g.BarWidth / 2f, 0.030f, g.BarReach - 0.005f);
                Drop = BarClamp + new Vector3(g.BarWidth / 2f, -g.BarDrop, g.BarReach * 0.4f);
                Wrist = Hood + new Vector3(0, 0.025f, -0.025f);
            }
        }

        /// <summary>
        /// A pedal at <paramref name="crankAngle"/>. Zero is forward and level; the angle increases
        /// pedalling forwards, so a crank at the front goes <i>down</i> next (the bike faces +Z).
        /// </summary>
        /// <param name="side">+1 the +X pedal (the figure's "R" leg), −1 the −X one, half a turn behind.</param>
        public Vector3 Pedal(float crankAngle, int side)
        {
            float angle = side > 0 ? crankAngle : crankAngle + Mathf.Pi;
            return BottomBracket + new Vector3(side * PedalOffset, -Mathf.Sin(angle) * CrankLength, Mathf.Cos(angle) * CrankLength);
        }

        /// <summary>
        /// Where the rider and bike's weight sits, against the tyres' contact patches (#1124): the
        /// combined centre of mass's distance behind the front contact, ahead of the rear one, and its
        /// height. A seated rider's own centre is ~20 cm over the hip joints and ~12 cm ahead of them on
        /// the hoods (segment tables, Winter); the bike's is just over the bottom bracket.
        /// </summary>
        public (float ToFront, float ToRear, float Height) Balance(float riderMass)
        {
            var rider = Hip + new Vector3(0, 0.20f, 0.12f);
            var bike = BottomBracket + new Vector3(0, 0.18f, 0.08f);
            float mb = Spec.Mass;
            var cg = (rider * riderMass + bike * mb) / (riderMass + mb);
            return (FrontAxle.Z - cg.Z, cg.Z - RearAxle.Z, cg.Y);
        }

        /// <summary>A leg's hip joint, <paramref name="side"/> as <see cref="Pedal"/>'s, seated on the hoods.</summary>
        public Vector3 LegHip(int side) => Hip + new Vector3(side * HipHalfWidth, 0, 0);

        /// <summary>A leg's hip joint in <paramref name="position"/>: off the saddle standing, on its nose tucked.</summary>
        public Vector3 LegHip(int side, RidingPosition position) => Fit(position).Hip + new Vector3(side * HipHalfWidth, 0, 0);

        /// <summary>
        /// Where the rider's hips and hands are in <paramref name="position"/> (#1124), and how far their
        /// arms reach out of their full length, or the back's angle when the rider sets that instead:
        /// what the rider's pose is solved from.
        /// <list type="bullet">
        /// <item>Hoods: the fit's saddle point and the hood tops, elbows a little bent.</item>
        /// <item>Drops: hands 12.5 cm lower on the drops, the hips a centimetre forward: the back comes down.</item>
        /// <item>Tuck: on the saddle's nose, hands on the tops by the stem, the back near flat (12°), elbows folded.</item>
        /// <item>Standing: hips up and forward over the bottom bracket, arms long on the hoods.</item>
        /// </list>
        /// </summary>
        public (Vector3 Hip, Vector3 Wrist, float Reach, float Back) Fit(RidingPosition position) => position switch
        {
            RidingPosition.Drops => (Hip + new Vector3(0, 0, 0.01f), Drop + new Vector3(0, 0.02f, -0.02f), 0.8f, float.NaN),
            RidingPosition.Tuck => (Hip + new Vector3(0, -0.005f, 0.07f),
                new Vector3(0.10f, BarClamp.Y + 0.02f, BarClamp.Z - 0.01f), 0.8f, Mathf.DegToRad(12f)),
            RidingPosition.Standing => (Hip + new Vector3(0, 0.07f, 0.15f), Wrist, 0.88f, float.NaN),
            _ => (Hip, Wrist, 0.8f, float.NaN),
        };
    }
}
