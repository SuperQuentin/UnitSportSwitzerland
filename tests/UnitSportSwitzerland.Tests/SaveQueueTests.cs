using System.Text;
using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The background save writer (src/Core/SaveQueue.cs, linked in, #221).</summary>
public class SaveQueueTests
{
    private static string Dir()
    {
        string d = Path.Combine(Path.GetTempPath(), "usw_savequeue_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Last_save_per_file_wins_and_no_part_is_left()
    {
        string dir = Dir();
        var paths = new[] { "a.json", "b.json", "sub/c.json" }.Select(p => Path.Combine(dir, p)).ToArray();
        // interleaved, many times each, as plants and claims would come in
        for (int i = 0; i < 3000; i++)
            SaveQueue.Enqueue(paths[i % 3], $"{{\"n\":{i}}}");
        SaveQueue.Flush();
        Assert.Equal("{\"n\":2997}", File.ReadAllText(paths[0]));
        Assert.Equal("{\"n\":2998}", File.ReadAllText(paths[1]));
        Assert.Equal("{\"n\":2999}", File.ReadAllText(paths[2]));
        Assert.Empty(Directory.GetFiles(dir, "*.part", SearchOption.AllDirectories));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Flush_waits_for_every_queued_save()
    {
        string dir = Dir();
        string big = new('x', 2_000_000);
        for (int i = 0; i < 20; i++)
            SaveQueue.Enqueue(Path.Combine(dir, $"{i}.json"), big + i);
        SaveQueue.Flush();   // what Main does on quit: right after it, everything is on disk
        for (int i = 0; i < 20; i++)
            Assert.Equal(big + i, File.ReadAllText(Path.Combine(dir, $"{i}.json"), Encoding.UTF8));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void A_failed_write_is_reported_and_the_queue_goes_on()
    {
        string dir = Dir();
        string blocker = Path.Combine(dir, "file");
        File.WriteAllText(blocker, "");
        Exception? error = null;
        SaveQueue.Enqueue(Path.Combine(blocker, "x.json"), "{}", e => error = e);   // a folder that is a file
        string ok = Path.Combine(dir, "ok.json");
        SaveQueue.Enqueue(ok, "{}");
        SaveQueue.Flush();
        Assert.NotNull(error);
        Assert.Equal("{}", File.ReadAllText(ok));
        Directory.Delete(dir, true);
    }
}
