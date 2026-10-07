namespace UnitSport.Trailer;

/// <summary>
/// The trailer's song (#706): "Voxel Revolution", Kevin MacLeod (incompetech.com), CC BY 4.0.
/// Every cut lands on one of its bar lines, measured off the file (spectral-flux tempo, the comb's
/// phase): 123 BPM, the first downbeat at 0.186 s. The file is not in the repository;
/// <c>tools/trailer.sh</c> downloads it.
/// </summary>
public static class Song
{
    public const double Bpm = 123.0;

    /// <summary>The first downbeat, s.</summary>
    public const double FirstBeat = 0.186;

    /// <summary>Where the music has faded out, s: the end of the last shot.</summary>
    public const double End = 127.0;

    public const string Title = "Voxel Revolution";
    public const string Credit = "\"Voxel Revolution\" Kevin MacLeod (incompetech.com)\nLicensed under Creative Commons: By Attribution 4.0";
    public const string Url = "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Voxel%20Revolution.mp3";

    public static double Beat => 60.0 / Bpm;
    public static double BarLength => 4 * Beat;

    /// <summary>Where bar <paramref name="k"/> (from 1) starts, s; bar 1 starts the film at 0.</summary>
    public static double Bar(int k) => k <= 1 ? 0 : FirstBeat + (k - 1) * BarLength;
}
