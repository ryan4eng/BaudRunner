using System.Text;
using Xunit;

namespace BaudRunner.Tests;

public class AnsiScannerTests
{
    /// <summary>Feeds bytes and returns the text that survived, plus the CSI sequences that completed.</summary>
    private static (string Text, List<AnsiCsi> Sequences) Run(AnsiScanner scanner, string input)
    {
        var text = new StringBuilder();
        var sequences = new List<AnsiCsi>();
        foreach (var value in Encoding.Latin1.GetBytes(input))
        {
            if (scanner.Feed(value, out var csi))
            {
                if (csi.Final != '\0') sequences.Add(csi);
                continue;
            }
            text.Append((char)value);
        }
        return (text.ToString(), sequences);
    }

    [Fact]
    public void Plain_text_passes_through()
        => Assert.Equal("hello", Run(new AnsiScanner(), "hello").Text);

    [Fact]
    public void Csi_is_consumed_whole_and_reported()
    {
        var (text, sequences) = Run(new AnsiScanner(), "a\x1B[1;32mb");
        Assert.Equal("ab", text);
        var csi = Assert.Single(sequences);
        Assert.Equal('m', csi.Final);
        Assert.Equal(1, csi[0]);
        Assert.Equal(32, csi[1]);
    }

    [Fact]
    public void Osc_title_does_not_leak_into_the_text()
    {
        // ESC ]0;title BEL - what a shell emits on every prompt.
        Assert.Equal("prompt", Run(new AnsiScanner(), "\x1B]0;my board\x07prompt").Text);
    }

    [Fact]
    public void Osc_terminated_by_string_terminator_is_also_consumed()
        => Assert.Equal("x", Run(new AnsiScanner(), "\x1B]0;title\x1B\\x").Text);

    [Fact]
    public void Charset_designator_consumes_its_argument()
        => Assert.Equal("ok", Run(new AnsiScanner(), "\x1B(Bok").Text);

    [Fact]
    public void Two_byte_escape_sequences_are_consumed_whole()
        => Assert.Equal("ok", Run(new AnsiScanner(), "\x1B=\x1B>ok").Text);

    [Fact]
    public void Csi_with_intermediate_bytes_does_not_leak_its_final_byte()
    {
        // ESC[5 q (set cursor style) - the space is an intermediate byte, and treating
        // it as the final byte used to print a stray "q".
        var (text, sequences) = Run(new AnsiScanner(), "\x1B[5 qtext");
        Assert.Equal("text", text);
        Assert.Equal('q', Assert.Single(sequences).Final);
    }

    [Fact]
    public void Private_parameter_prefixes_other_than_question_mark_are_handled()
    {
        var (text, sequences) = Run(new AnsiScanner(), "\x1B[>0cdone");
        Assert.Equal("done", text);
        Assert.Equal('c', Assert.Single(sequences).Final);
        Assert.Equal('>', sequences[0].Private);
    }

    [Fact]
    public void A_sequence_split_across_two_feeds_is_still_recognised()
    {
        var scanner = new AnsiScanner();
        var first = Run(scanner, "a\x1B[1;3");
        var second = Run(scanner, "2mb");
        Assert.Equal("a", first.Text);
        Assert.Equal("b", second.Text);
        Assert.Equal(32, Assert.Single(second.Sequences)[1]);
    }

    [Fact]
    public void Omitted_parameters_are_distinguishable_from_zero()
    {
        var (_, sequences) = Run(new AnsiScanner(), "\x1B[K");
        var csi = Assert.Single(sequences);
        Assert.False(csi.HasParameter(0));
        Assert.Equal(7, csi[0, fallback: 7]);
    }

    [Fact]
    public void An_over_long_parameter_run_aborts_instead_of_latching()
    {
        var scanner = new AnsiScanner();
        var (text, _) = Run(scanner, "\x1B[" + new string('9', 500) + "recovered");
        Assert.False(scanner.InSequence);
        Assert.Contains("recovered", text);
    }

    [Fact]
    public void A_control_character_inside_a_sequence_is_handed_back_so_the_line_still_breaks()
    {
        var (text, _) = Run(new AnsiScanner(), "\x1B[12\nnext");
        Assert.Equal("\nnext", text);
    }
}

public class SgrStateTests
{
    private static AnsiCsi Sgr(params int[] parameters) => new('\0', parameters, "", 'm');

    [Fact]
    public void Reset_clears_every_attribute()
    {
        var state = new SgrState();
        state.Apply(Sgr(1, 31, 7));
        state.Apply(Sgr(0));
        Assert.Null(state.Foreground);
        Assert.False(state.Bold);
        Assert.False(state.Inverse);
    }

    [Fact]
    public void An_empty_parameter_list_means_reset()
    {
        var state = new SgrState();
        state.Apply(Sgr(31));
        state.Apply(new AnsiCsi('\0', Array.Empty<int>(), "", 'm'));
        Assert.Null(state.Foreground);
    }

    [Fact]
    public void Basic_and_bright_colours_map_to_different_brushes()
    {
        var standard = new SgrState(); standard.Apply(Sgr(32));
        var bright = new SgrState(); bright.Apply(Sgr(92));
        Assert.NotNull(standard.Foreground);
        Assert.NotNull(bright.Foreground);
        Assert.NotSame(standard.Foreground, bright.Foreground);
    }

    [Fact]
    public void Indexed_colour_operands_are_consumed_not_reinterpreted()
    {
        // "38;5;196" must be bright red, not "reset then colour 5 then colour 196".
        var extended = new SgrState();
        extended.Apply(Sgr(38, 5, 196));
        Assert.Same(AnsiPalette.Indexed(196), extended.Foreground);
    }

    [Fact]
    public void Truecolour_operands_are_consumed_not_reinterpreted()
    {
        var state = new SgrState();
        state.Apply(Sgr(38, 2, 10, 200, 30));
        Assert.Same(AnsiPalette.Rgb(10, 200, 30), state.Foreground);
    }

    [Fact]
    public void A_background_request_does_not_change_the_foreground()
    {
        var state = new SgrState();
        state.Apply(Sgr(31));
        var foreground = state.Foreground;
        state.Apply(Sgr(48, 5, 21));
        Assert.Same(foreground, state.Foreground);
        Assert.Same(AnsiPalette.Indexed(21), state.Background);
    }

    [Fact]
    public void A_truncated_extended_colour_does_not_reinterpret_the_operands()
    {
        var state = new SgrState();
        state.Apply(Sgr(38, 5));
        Assert.Null(state.Foreground);
    }
}
