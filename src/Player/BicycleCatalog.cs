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
    /// <param name="BarWidth">Centre to centre at the hoods.</param>
    public sealed record BicycleGeometry(
        float Stack, float Reach, float HeadAngle, float SeatAngle, float HeadTube, float SeatTube,
        float Chainstay, float BbDrop, float ForkOffset, float Wheelbase, float CrankLength,
        float StemLength, float StemAngle, float Spacers, float BarReach, float BarDrop, float BarWidth);

    /// <summary>One bicycle: a real model, size and build (#1124).</summary>
    /// <param name="Mass">The bike alone, kg, pedals on.</param>
    /// <param name="TyreWidth">As on the sidewall, metres (28 mm = 0.028).</param>
    public sealed record BicycleSpec(
        string Name, string Size, float Mass, float TyreWidth, BicycleGeometry Geometry,
        int[] Chainrings, int[] Cassette, string Groupset, BikeBrakes Brakes, BikePalette Look)
    {
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
                Saddle: new Color(0.05f, 0.05f, 0.055f)) { Decal = new Color(0.80f, 0.80f, 0.82f) });

        public static readonly BicycleSpec[] All = { AddictRc15 };

        public static BicycleSpec Default => All[0];
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
        /// <summary>The +X hood's top; the other is mirrored.</summary>
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
            Hood = BarClamp + new Vector3(g.BarWidth / 2f, 0.030f, g.BarReach - 0.005f);
            Drop = BarClamp + new Vector3(g.BarWidth / 2f, -g.BarDrop, g.BarReach * 0.4f);
            Wrist = Hood + new Vector3(0, 0.025f, -0.025f);
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

        /// <summary>A leg's hip joint, <paramref name="side"/> as <see cref="Pedal"/>'s.</summary>
        public Vector3 LegHip(int side) => Hip + new Vector3(side * HipHalfWidth, 0, 0);
    }
}
