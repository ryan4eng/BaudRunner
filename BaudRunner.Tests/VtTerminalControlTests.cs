using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Xunit;

namespace BaudRunner.Tests;

public class VtTerminalControlTests
{
    private static List<Run> Runs(VtTerminalControl vt) => ((SelectableTextBlock)vt.Content!).Inlines!.OfType<Run>().ToList();

    private static void Feed(VtTerminalControl vt, string text) => vt.ProcessBytes(Encoding.Latin1.GetBytes(text));

    [AvaloniaFact]
    public void Erase_display_keeps_the_current_colour()
    {
        // A device that sets a colour and then clears expects the colour to survive,
        // exactly as it does on a real terminal.
        var vt = new VtTerminalControl();
        Feed(vt, "\x1B[31mold\x1B[2Jnew");

        var run = Assert.Single(Runs(vt));
        Assert.Equal("new", run.Text);
        Assert.Same(AnsiPalette.Standard(1), run.Foreground);
    }

    [AvaloniaFact]
    public void A_user_clear_resets_the_colour_as_well()
    {
        var vt = new VtTerminalControl();
        Feed(vt, "\x1B[31mold");
        vt.Clear();
        Feed(vt, "new");
        Assert.NotSame(AnsiPalette.Standard(1), Assert.Single(Runs(vt)).Foreground);
    }

    [AvaloniaFact]
    public void A_notice_is_shown_verbatim_without_being_parsed_or_reported()
    {
        var vt = new VtTerminalControl();
        var reported = new StringBuilder();
        vt.TextAppended += text => reported.Append(text);
        Feed(vt, "device");

        vt.AppendNotice("\r\n[Connection lost: \x1B[31mnot a colour]\r\n", Brushes.Red);

        Assert.Equal("device\n[Connection lost: \x1B[31mnot a colour]\n", vt.AllText);
        // The file log gets notices from the caller, so the event must not fire twice.
        Assert.Equal("device", reported.ToString());
        Assert.Same(Brushes.Red, Runs(vt)[^1].Foreground);
    }

    [AvaloniaFact]
    public void A_notice_after_a_bare_carriage_return_does_not_get_erased_by_the_next_byte()
    {
        var vt = new VtTerminalControl();
        Feed(vt, "progress 10%\r");
        vt.AppendNotice("[note]\r\n", null);
        Feed(vt, "done");
        Assert.EndsWith("[note]\ndone", vt.AllText);
    }

    [Theory]
    [InlineData(Key.Escape, KeyModifiers.None, true)]
    [InlineData(Key.W, KeyModifiers.Control, true)]
    [InlineData(Key.C, KeyModifiers.Control, true)]
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Shift, false)]
    [InlineData(Key.End, KeyModifiers.Control, false)]
    [InlineData(Key.D1, KeyModifiers.Control, false)]
    [InlineData(Key.F1, KeyModifiers.None, false)]
    public void The_terminal_claims_escape_and_control_letters_only(Key key, KeyModifiers modifiers, bool claimed)
        => Assert.Equal(claimed, VtTerminalControl.ConsumesKey(key, modifiers));
}
