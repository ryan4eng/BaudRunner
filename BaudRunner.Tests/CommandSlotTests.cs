using System.Text;
using Xunit;

namespace BaudRunner.Tests;

public class CommandSlotTests
{
    [Theory]
    [InlineData("01 03 00 6B", new byte[] { 0x01, 0x03, 0x00, 0x6B })]
    [InlineData("01,03,00,6B", new byte[] { 0x01, 0x03, 0x00, 0x6B })]
    [InlineData("01-03-00-6B", new byte[] { 0x01, 0x03, 0x00, 0x6B })]
    [InlineData("0x01 0x03", new byte[] { 0x01, 0x03 })]
    [InlineData("0103006B", new byte[] { 0x01, 0x03, 0x00, 0x6B })]
    [InlineData("  a  B  ", new byte[] { 0x0A, 0x0B })]
    [InlineData("", new byte[0])]
    public void ParseHex_accepts_the_common_forms(string text, byte[] expected)
        => Assert.Equal(expected, CommandSlot.ParseHex(text));

    [Theory]
    [InlineData("G1")]
    [InlineData("01 ZZ")]
    [InlineData("0x")]
    public void ParseHex_rejects_non_hexadecimal(string text)
        => Assert.Throws<FormatException>(() => CommandSlot.ParseHex(text));

    [Fact]
    public void ParseHex_rejects_an_odd_length_run_rather_than_guessing()
    {
        // "001" as a single byte 0x01 is the old behaviour and is almost always a typo.
        var error = Assert.Throws<FormatException>(() => CommandSlot.ParseHex("001"));
        Assert.Contains("odd number", error.Message);
    }

    [Fact]
    public void ParseText_expands_control_tokens()
        => Assert.Equal(new byte[] { (byte)'A', 0x0D, 0x0A, 0x09, 0x1B, 0x00, 0x7F }, CommandSlot.ParseText("A<CR><LF><TAB><esc><NUL><0x7f>"));

    [Fact]
    public void ParseText_accepts_the_brace_form_the_log_displays()
        => Assert.Equal(new byte[] { 0x0A, 0x0D, 0x00, 0xFF }, CommandSlot.ParseText("<lf><cr>{00}{FF}"));

    [Fact]
    public void ParseText_leaves_braces_that_are_not_a_byte_alone()
        => Assert.Equal(new byte[] { (byte)'{', (byte)'x', (byte)'}' }, CommandSlot.ParseText("{x}"));

    [Fact]
    public void ParseText_maps_latin1_one_to_one_instead_of_replacing_with_question_marks()
        => Assert.Equal(new byte[] { 0xB0, 0x43 }, CommandSlot.ParseText("°C"));

    [Fact]
    public void ParseText_reports_a_character_it_cannot_send()
    {
        var error = Assert.Throws<FormatException>(() => CommandSlot.ParseText("€"));
        Assert.Contains("cannot be sent", error.Message);
    }

    [Theory]
    [InlineData(LineEnding.None, "AT")]
    [InlineData(LineEnding.Lf, "AT\n")]
    [InlineData(LineEnding.Cr, "AT\r")]
    [InlineData(LineEnding.CrLf, "AT\r\n")]
    public void ToBytes_appends_the_selected_terminator(LineEnding ending, string expected)
    {
        var slot = new CommandSlot { Text = "AT", Ending = ending };
        Assert.Equal(Encoding.ASCII.GetBytes(expected), slot.ToBytes());
    }

    [Fact]
    public void ToBytes_appends_the_terminator_in_hex_mode_too()
    {
        var slot = new CommandSlot { Text = "01 02", Hex = true, Ending = LineEnding.CrLf };
        Assert.Equal(new byte[] { 0x01, 0x02, 0x0D, 0x0A }, slot.ToBytes());
    }

    [Fact]
    public void Validate_returns_null_for_good_input_and_a_reason_otherwise()
    {
        Assert.Null(CommandSlot.Validate("01 02", hex: true));
        Assert.NotNull(CommandSlot.Validate("01 Q2", hex: true));
        Assert.Null(CommandSlot.Validate("hello", hex: false));
    }
}
