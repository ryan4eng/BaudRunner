using System.Text;
using Avalonia.Media;
using Xunit;

namespace BaudRunner.Tests;

public class DisplayFormatterTests
{
    private static DisplayFormatter Formatter(Action<DisplayFormatter>? configure = null)
    {
        var formatter = new DisplayFormatter { HexBrush = Brushes.Gray, TimestampBrush = Brushes.DimGray };
        configure?.Invoke(formatter);
        return formatter;
    }

    private static string Render(DisplayFormatter formatter, params string[] batches)
    {
        var output = new List<LogSegment>();
        foreach (var batch in batches) formatter.Format(Encoding.Latin1.GetBytes(batch), output);
        return string.Concat(output.Select(segment => segment.Text));
    }

    private static string Render(DisplayFormatter formatter, byte[] bytes)
    {
        var output = new List<LogSegment>();
        formatter.Format(bytes, output);
        return string.Concat(output.Select(segment => segment.Text));
    }

    [Fact]
    public void Normal_mode_passes_printable_text_through()
        => Assert.Equal("hello\r\n", Render(Formatter(), "hello\r\n"));

    [Fact]
    public void Normal_mode_shows_non_printable_bytes_as_hex()
        => Assert.Equal("A{00}{FF}B", Render(Formatter(), new byte[] { (byte)'A', 0x00, 0xFF, (byte)'B' }));

    [Fact]
    public void Hex_all_mode_shows_every_byte_including_line_breaks()
        => Assert.Equal("{41}{0D}{0A}", Render(Formatter(f => f.Mode = DisplayMode.HexAll), "A\r\n"));

    [Fact]
    public void Hex_except_crlf_keeps_the_line_structure()
        => Assert.Equal("{41}\r\n", Render(Formatter(f => f.Mode = DisplayMode.HexExceptCrLf), "A\r\n"));

    [Fact]
    public void Ascii_only_mode_drops_the_rest()
        => Assert.Equal("AB\n", Render(Formatter(f => f.Mode = DisplayMode.AsciiOnly), new byte[] { (byte)'A', 0x01, 0xF0, (byte)'B', 0x0A }));

    [Fact]
    public void Ansi_colour_is_applied_as_a_span_and_the_sequence_is_not_shown()
    {
        var formatter = Formatter();
        var output = new List<LogSegment>();
        formatter.Format(Encoding.Latin1.GetBytes("plain\x1B[32mgreen\x1B[0mplain"), output);

        Assert.Equal("plaingreenplain", string.Concat(output.Select(segment => segment.Text)));
        var green = Assert.Single(output, segment => segment.Text == "green");
        Assert.NotNull(green.Brush);
        Assert.All(output.Where(segment => segment.Text != "green"), segment => Assert.Null(segment.Brush));
    }

    [Fact]
    public void Strip_mode_removes_the_sequence_without_colouring()
    {
        var formatter = Formatter(f => f.Ansi = AnsiMode.Strip);
        var output = new List<LogSegment>();
        formatter.Format(Encoding.Latin1.GetBytes("\x1B[32mgreen"), output);
        Assert.Equal("green", string.Concat(output.Select(segment => segment.Text)));
        Assert.All(output, segment => Assert.Null(segment.Brush));
    }

    [Fact]
    public void Raw_mode_shows_the_escape_byte_as_hex_like_before()
        => Assert.Equal("{1B}[32mgreen", Render(Formatter(f => f.Ansi = AnsiMode.Raw), "\x1B[32mgreen"));

    [Fact]
    public void An_escape_sequence_split_across_two_reads_is_still_consumed()
    {
        var formatter = Formatter();
        Assert.Equal("ab", Render(formatter, "a\x1B[3", "2mb"));
    }

    [Fact]
    public void Timestamps_are_emitted_once_per_line_not_once_per_batch()
    {
        var clock = new DateTime(2026, 8, 21, 9, 30, 0, 500);
        var formatter = Formatter(f => { f.Timestamps = TimestampMode.Wall; f.Clock = () => clock; });
        formatter.ResetStream();

        // One batch containing three lines: every line gets its own stamp.
        var text = Render(formatter, "one\r\ntwo\r\nthree");
        Assert.Equal("[09:30:00.500] one\r\n[09:30:00.500] two\r\n[09:30:00.500] three", text);
    }

    [Fact]
    public void A_line_split_across_batches_is_stamped_only_once()
    {
        var clock = new DateTime(2026, 8, 21, 9, 30, 0, 500);
        var formatter = Formatter(f => { f.Timestamps = TimestampMode.Wall; f.Clock = () => clock; });
        formatter.ResetStream();

        var text = Render(formatter, "par", "tial\r\n");
        Assert.Equal("[09:30:00.500] partial\r\n", text);
    }

    [Fact]
    public void No_stamp_is_inserted_between_a_carriage_return_and_its_line_feed()
    {
        var clock = new DateTime(2026, 8, 21, 9, 30, 0, 500);
        var formatter = Formatter(f => { f.Timestamps = TimestampMode.Wall; f.Clock = () => clock; });
        formatter.ResetStream();

        Assert.Equal("[09:30:00.500] a\r\n", Render(formatter, "a\r", "\n"));
    }

    [Fact]
    public void Delta_timestamps_measure_the_gap_between_lines()
    {
        var now = new DateTime(2026, 8, 21, 9, 30, 0);
        var formatter = Formatter(f => { f.Timestamps = TimestampMode.Delta; f.Clock = () => now; });
        formatter.ResetStream();

        var output = new List<LogSegment>();
        formatter.Format(Encoding.Latin1.GetBytes("first\n"), output);
        now = now.AddMilliseconds(250);
        formatter.Format(Encoding.Latin1.GetBytes("second\n"), output);

        var text = string.Concat(output.Select(segment => segment.Text));
        Assert.Equal("[+0.000] first\n[+0.250] second\n", text);
    }

    [Fact]
    public void Utf8_mode_decodes_a_character_split_across_two_reads()
    {
        var formatter = Formatter(f => f.Encoding = ReceiveEncoding.Utf8);
        var output = new List<LogSegment>();
        // "°" is C2 B0.
        formatter.Format(new byte[] { 0xC2 }, output);
        formatter.Format(new byte[] { 0xB0, (byte)'C' }, output);
        Assert.Equal("°C", string.Concat(output.Select(segment => segment.Text)));
    }

    [Fact]
    public void Latin1_mode_leaves_high_bytes_as_hex_so_binary_stays_readable()
        => Assert.Equal("{C2}{B0}C", Render(Formatter(), new byte[] { 0xC2, 0xB0, (byte)'C' }));

    [Fact]
    public void ResetStream_drops_a_half_received_escape_sequence()
    {
        var formatter = Formatter();
        Render(formatter, "\x1B[3");
        formatter.ResetStream();
        Assert.Equal("2mtext", Render(formatter, "2mtext"));
    }
}
