using UnitSport.Audio.Cd;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>Uploading a CD: the server's file rules and the antivirus's verdicts (src/Audio/Cd, linked in, #736).</summary>
public class CdUploadTests
{
    [Theory]
    [InlineData("song.mp3", true)]
    [InlineData("Song.FLAC", true)]
    [InlineData("track.opus", true)]
    [InlineData("setup.exe", false)]
    [InlineData("song.mp3.exe", false)]
    [InlineData("noext", false)]
    public void Only_audio_files(string name, bool ok) => Assert.Equal(ok, CdUploadRules.AudioFile(name));

    [Fact]
    public void A_name_cannot_climb_out_of_its_folder_or_carry_odd_characters()
    {
        Assert.Equal("passwd.bin", CdUploadRules.SafeName("../../etc/passwd"));
        Assert.Equal("evil.mp3", CdUploadRules.SafeName(@"..\..\evil.mp3"));
        Assert.Equal("a_b_c.mp3", CdUploadRules.SafeName("a;b|c.mp3"));
        Assert.Equal("upload.wav", CdUploadRules.SafeName("....wav"));
        Assert.Equal("Café Del Mar (Live).flac", CdUploadRules.SafeName("Café Del Mar (Live).FLAC"));
        Assert.True(CdUploadRules.SafeName(new string('x', 300) + ".mp3").Length <= 84);
    }

    [Fact]
    public void Each_antivirus_exit_code_reads_right()
    {
        Assert.Equal(ScanVerdict.Clean, CdScanner.Interpret(CdScanner.Engine.ClamScan, 0));
        Assert.Equal(ScanVerdict.Infected, CdScanner.Interpret(CdScanner.Engine.ClamScan, 1));
        Assert.Equal(ScanVerdict.Failed, CdScanner.Interpret(CdScanner.Engine.ClamDaemon, 2));
        Assert.Equal(ScanVerdict.Clean, CdScanner.Interpret(CdScanner.Engine.Defender, 0));
        Assert.Equal(ScanVerdict.Infected, CdScanner.Interpret(CdScanner.Engine.Defender, 2));
        Assert.Equal(ScanVerdict.Failed, CdScanner.Interpret(CdScanner.Engine.Defender, 1));
    }
}
