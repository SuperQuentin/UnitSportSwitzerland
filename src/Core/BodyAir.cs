using System;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// A body's air (#1853, docs/plans/planets.md sections 6 and 7): what drags a craft and what the sky scatters. Single
/// scattering in two parts, as the shell draws it (<c>shaders/atmosphere.gdshader</c>): a fine one with Rayleigh's phase
/// (the Earth's molecules; Mars' fine dust) and a coarse, forward-scattering one (the Earth's aerosols; Mars' dust as it
/// lights the sun's surroundings), each with a colour (its scattering coefficient per channel at the ground) and a scale
/// height. Values at the ground, per metre.
/// </summary>
/// <param name="SurfaceDensity">The gas's density at the reference surface, kg/m³ (drag).</param>
/// <param name="ScaleHeightM">The gas's density scale height, metres (drag).</param>
/// <param name="Fine">The fine part's scattering per channel (R, G, B), per metre at the ground.</param>
/// <param name="FineHeightM">Its scale height, metres.</param>
/// <param name="Coarse">The coarse part's scattering per channel, per metre at the ground.</param>
/// <param name="CoarseHeightM">Its scale height, metres.</param>
/// <param name="CoarseG">Its phase's asymmetry (Henyey-Greenstein-like, Cornette-Shanks): how forward it scatters.</param>
/// <param name="CoarseExtinction">Its extinction over its scattering (absorption): 1.1 for the Earth's aerosols.</param>
/// <param name="ThicknessM">The shell's top over the reference surface, metres.</param>
/// <param name="SkyFromM">The sky starts to read as space (the stars by day) above this camera altitude, metres.</param>
/// <param name="SkyFullM">All space above this one.</param>
/// <param name="FineAbsorption">What the fine part absorbs on top of what it scatters, per channel, per metre at the
/// ground: none for the Earth's molecules; Mars' dust takes blue (the horizon goes redder, not greener).</param>
/// <param name="SunPower">The sun's light in the shell's sum (the shader's <c>sun_power</c>): the Earth's 4.</param>
public sealed record BodyAir(
    double SurfaceDensity, double ScaleHeightM,
    (double R, double G, double B) Fine, double FineHeightM,
    (double R, double G, double B) Coarse, double CoarseHeightM, double CoarseG, double CoarseExtinction,
    double ThicknessM, double SkyFromM, double SkyFullM,
    (double R, double G, double B) FineAbsorption = default, double SunPower = 4)
{
    /// <summary>
    /// The Earth's: exactly the shell's constants before #1853 (Rayleigh's 5.8, 13.5, 33.1 per Mm at 8 km, a clear day's
    /// aerosols at 1.2 km, g 0.76) and the sky's 15-40 km band; its drag reads the 1976 standard atmosphere instead
    /// (<see cref="Atmosphere.Density"/>), the exponential here only its sea-level stand-in.
    /// </summary>
    public static readonly BodyAir Earth = new(1.225, 8500,
        (5.8e-6, 13.5e-6, 33.1e-6), 8000,
        (4e-6, 4e-6, 4e-6), 1200, 0.76, 1.1,
        100_000, 15_000, 40_000);

    /// <summary>
    /// Mars' (#1853): CO₂ at 0.020 kg/m³ at the reference, 11.1 km scale height (NASA's Mars fact sheet). The sky is its
    /// dust's, well mixed to the same height: the fine part scatters red over blue (the butterscotch day; a vertical
    /// optical depth of 0.33 red, 0.17 blue, a clear Martian day's 0.3-0.5), the coarse part is forward-peaked and bluer (the blue
    /// round the setting sun). The colours are tuned by eye against the rovers' sky; the depths and heights are the
    /// measured ones' order.
    /// </summary>
    public static readonly BodyAir Mars = new(0.020, 11_100,
        (30e-6, 21.6e-6, 15e-6), 11_100,
        (2e-6, 3.5e-6, 7e-6), 11_100, 0.85, 1.3,
        120_000, 20_000, 60_000,
        FineAbsorption: (0, 6e-6, 20e-6), SunPower: 7);

    /// <summary>The gas's density at a height over the reference, kg/m³ (0 above the shell).</summary>
    public double Density(double h) => h > ThicknessM ? 0 : SurfaceDensity * Math.Exp(-Math.Max(h, -ScaleHeightM * 3) / ScaleHeightM);

    /// <summary>How far into space a camera at this altitude is for the sky: 0 in the air, 1 above it (smooth).</summary>
    public double SpaceAt(double altitude)
    {
        double t = Math.Clamp((altitude - SkyFromM) / (SkyFullM - SkyFromM), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
