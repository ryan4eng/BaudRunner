using System.Text;
using Xunit;

namespace BaudRunner.Tests;

public class LogWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "BaudRunnerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Writes_are_flushed_to_the_labelled_file_on_dispose()
    {
        string path;
        using (var writer = new LogWriter(_directory, "Serial", new UTF8Encoding(false)))
        {
            path = writer.FilePath;
            writer.Write("hello world");
        }
        Assert.Contains("Serial", Path.GetFileName(path));
        Assert.Contains("hello world", File.ReadAllText(path));
    }

    [Fact]
    public void Each_transport_gets_its_own_file_so_captures_do_not_interleave()
    {
        using var serial = new LogWriter(_directory, "Serial", new UTF8Encoding(false));
        using var tcp = new LogWriter(_directory, "TCP Client", new UTF8Encoding(false));
        Assert.NotEqual(serial.FilePath, tcp.FilePath);
    }

    [Fact]
    public void A_session_header_marks_where_each_capture_starts()
    {
        string path;
        using (var writer = new LogWriter(_directory, "Serial", new UTF8Encoding(false))) { path = writer.FilePath; writer.Write("first"); }
        using (var writer = new LogWriter(_directory, "Serial", new UTF8Encoding(false))) { writer.Write("second"); }

        var text = File.ReadAllText(path);
        Assert.Equal(2, text.Split("log opened").Length - 1);
    }

    [Fact]
    public void Exceeding_the_size_cap_rolls_to_the_next_file()
    {
        var rolled = new List<string>();
        string first;
        using (var writer = new LogWriter(_directory, "Serial", new UTF8Encoding(false), sizeCap: 512))
        {
            writer.Rolled += path => { lock (rolled) rolled.Add(path); };
            first = writer.FilePath;
            // The roll is evaluated when the next batch is written, so keep feeding it
            // the way a live stream would rather than writing everything up front.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && writer.FilePath == first)
            {
                writer.Write(new string('x', 128));
                Thread.Sleep(25);
            }
            Assert.NotEqual(first, writer.FilePath);
        }
        Assert.NotEmpty(rolled);
    }

    [Fact]
    public void Prune_removes_only_files_older_than_the_retention_window()
    {
        Directory.CreateDirectory(_directory);
        var old = Path.Combine(_directory, "BaudRunner-2020-01-01-Serial.log");
        var recent = Path.Combine(_directory, "BaudRunner-2026-08-21-Serial.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-40));

        var removed = LogWriter.Prune(_directory, TimeSpan.FromDays(30));
        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Prune_on_a_missing_directory_is_a_no_op()
        => Assert.Equal(0, LogWriter.Prune(Path.Combine(_directory, "absent"), TimeSpan.FromDays(1)));

    [Fact]
    public void Candidate_paths_offer_numbered_fallbacks_when_the_first_is_locked()
    {
        var paths = LogWriter.CandidatePaths(_directory, "TCP Server", new DateTime(2026, 8, 21)).ToList();
        Assert.Equal(10, paths.Count);
        Assert.EndsWith("BaudRunner-2026-08-21-TCPServer.log", paths[0]);
        Assert.EndsWith("BaudRunner-2026-08-21-TCPServer_2.log", paths[1]);
    }
}
