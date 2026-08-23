using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using BaudRunner.Tests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace BaudRunner.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

public class LogViewTests
{
    /// <summary>A LogView laid out inside a window, so the viewport and extent are real.</summary>
    private static LogView Shown(int maxLines = 5000)
    {
        var log = new LogView { MaxLines = maxLines };
        var window = new Window { Width = 600, Height = 300, Content = new ScrollViewer { Content = log } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return log;
    }

    private static void Feed(LogView log, params string[] lines)
    {
        foreach (var line in lines) log.Append(line + "\r\n", null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Appending_lines_grows_the_buffer()
    {
        var log = Shown();
        Feed(log, "one", "two", "three");
        // Three terminated lines plus the empty line being written into.
        Assert.Equal(4, log.LineCount);
    }

    [AvaloniaFact]
    public void Crlf_counts_as_one_line_break()
    {
        var log = Shown();
        log.Append("a\r\nb\r\n", null);
        Assert.Equal(3, log.LineCount);
    }

    [AvaloniaFact]
    public void A_line_break_split_across_two_appends_is_still_one_break()
    {
        var log = Shown();
        log.Append("a\r", null);
        log.Append("\nb", null);
        Assert.Equal(2, log.LineCount);
    }

    [AvaloniaFact]
    public void Scrollback_is_trimmed_to_the_configured_limit()
    {
        var log = Shown(maxLines: 300);
        for (var i = 0; i < 2000; i++) log.Append($"line {i}\r\n", null);
        Dispatcher.UIThread.RunJobs();
        // Trimming happens in chunks, so the buffer settles between the limit and
        // limit + chunk rather than exactly at the limit.
        Assert.InRange(log.LineCount, 300, 801);
    }

    [AvaloniaFact]
    public void Lowering_the_limit_trims_immediately()
    {
        var log = Shown();
        for (var i = 0; i < 3000; i++) log.Append($"line {i}\r\n", null);
        Dispatcher.UIThread.RunJobs();
        log.MaxLines = 500;
        Assert.InRange(log.LineCount, 500, 1001);
    }

    [AvaloniaFact]
    public void Select_all_returns_the_whole_buffer()
    {
        var log = Shown();
        Feed(log, "alpha", "beta");
        log.SelectAll();
        Assert.True(log.HasSelection);
        Assert.Contains("alpha", log.SelectedText);
        Assert.Contains("beta", log.SelectedText);
    }

    [AvaloniaFact]
    public void Clear_empties_the_buffer_and_resumes_following()
    {
        var log = Shown();
        Feed(log, "alpha", "beta");
        log.SelectAll();
        log.Clear();
        Assert.Equal(1, log.LineCount);
        Assert.False(log.HasSelection);
        Assert.True(log.AutoScroll);
    }

    [AvaloniaFact]
    public void Search_counts_every_occurrence_including_repeats_on_one_line()
    {
        var log = Shown();
        Feed(log, "error here", "fine", "error and error again");
        Assert.True(log.SetSearch("error", caseSensitive: false, useRegex: false));
        Assert.Equal(3, log.MatchCount);
    }

    [AvaloniaFact]
    public void Search_is_case_insensitive_unless_asked_otherwise()
    {
        var log = Shown();
        Feed(log, "Error", "error", "ERROR");

        log.SetSearch("error", caseSensitive: false, useRegex: false);
        Assert.Equal(3, log.MatchCount);

        log.SetSearch("error", caseSensitive: true, useRegex: false);
        Assert.Equal(1, log.MatchCount);
    }

    [AvaloniaFact]
    public void Search_finds_matches_in_lines_appended_after_the_query_was_set()
    {
        var log = Shown();
        Feed(log, "first");
        log.SetSearch("target", caseSensitive: false, useRegex: false);
        Assert.Equal(0, log.MatchCount);

        Feed(log, "a target appears", "another target");
        Assert.Equal(2, log.MatchCount);
    }

    [AvaloniaFact]
    public void Regular_expressions_are_supported_and_an_invalid_one_is_reported()
    {
        var log = Shown();
        Feed(log, "code 404", "code 200", "no code");

        Assert.True(log.SetSearch(@"code \d{3}", caseSensitive: false, useRegex: true));
        Assert.Equal(2, log.MatchCount);

        Assert.False(log.SetSearch("code (", caseSensitive: false, useRegex: true));
        Assert.Equal(0, log.MatchCount);
    }

    [AvaloniaFact]
    public void FindNext_selects_each_match_in_turn_and_wraps()
    {
        var log = Shown();
        Feed(log, "hit one", "miss", "hit two");
        log.SetSearch("hit", caseSensitive: false, useRegex: false);

        Assert.True(log.FindNext(forward: true));
        Assert.Equal("hit", log.SelectedText);
        var first = log.CurrentMatchIndex;

        Assert.True(log.FindNext(forward: true));
        Assert.NotEqual(first, log.CurrentMatchIndex);

        // Two matches, so a third step wraps back to the first.
        Assert.True(log.FindNext(forward: true));
        Assert.Equal(first, log.CurrentMatchIndex);
    }

    [AvaloniaFact]
    public void FindNext_returns_false_when_nothing_matches()
    {
        var log = Shown();
        Feed(log, "nothing to see");
        log.SetSearch("absent", caseSensitive: false, useRegex: false);
        Assert.False(log.FindNext(forward: true));
    }

    [AvaloniaFact]
    public void Clearing_the_query_drops_the_matches()
    {
        var log = Shown();
        Feed(log, "match");
        log.SetSearch("match", caseSensitive: false, useRegex: false);
        Assert.Equal(1, log.MatchCount);
        log.SetSearch("", caseSensitive: false, useRegex: false);
        Assert.Equal(0, log.MatchCount);
    }

    [AvaloniaFact]
    public void Matches_on_trimmed_lines_are_dropped_rather_than_pointing_at_stale_content()
    {
        var log = Shown(maxLines: 250);
        log.SetSearch("needle", caseSensitive: false, useRegex: false);
        for (var i = 0; i < 40; i++) log.Append("needle\r\n", null);
        for (var i = 0; i < 3000; i++) log.Append($"filler {i}\r\n", null);
        Dispatcher.UIThread.RunJobs();

        // Every needle line has scrolled out of the retained buffer.
        Assert.Equal(0, log.MatchCount);
    }

    [AvaloniaFact]
    public void Following_the_tail_stays_on_by_default_and_ScrollToEnd_restores_it()
    {
        var log = Shown();
        Assert.True(log.AutoScroll);
        for (var i = 0; i < 500; i++) log.Append($"line {i}\r\n", null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(log.AutoScroll);

        // Scrolling away from the bottom is what pauses follow mode.
        ((Avalonia.Controls.Primitives.IScrollable)log).Offset = new Vector(0, 0);
        Assert.False(log.AutoScroll);

        log.ScrollToEnd();
        Assert.True(log.AutoScroll);
    }

    [AvaloniaFact]
    public void Scrolling_back_to_the_bottom_resumes_following()
    {
        var log = Shown();
        for (var i = 0; i < 500; i++) log.Append($"line {i}\r\n", null);
        Dispatcher.UIThread.RunJobs();

        var scrollable = (Avalonia.Controls.Primitives.IScrollable)log;
        scrollable.Offset = new Vector(0, 0);
        Assert.False(log.AutoScroll);

        scrollable.Offset = new Vector(0, scrollable.Extent.Height);
        Assert.True(log.AutoScroll);
    }

    [AvaloniaFact]
    public void AllText_returns_the_retained_buffer_for_saving()
    {
        var log = Shown();
        Feed(log, "alpha", "beta");
        var text = log.AllText();
        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
    }

    [AvaloniaFact]
    public void Coloured_segments_are_accepted_and_rendered_without_error()
    {
        var log = Shown();
        log.AppendSegments(new[]
        {
            new LogSegment("green ", Brushes.Green),
            new LogSegment("plain\r\n", null),
        });
        Dispatcher.UIThread.RunJobs();
        log.SelectAll();
        Assert.Contains("green plain", log.SelectedText);
    }

    [AvaloniaFact]
    public void A_line_longer_than_the_hard_wrap_limit_is_split_rather_than_growing_without_bound()
    {
        var log = Shown();
        log.Append(new string('x', 5000), null);
        Dispatcher.UIThread.RunJobs();
        // 1024 characters per line, so a 5000-character run occupies five lines.
        Assert.Equal(5, log.LineCount);
    }
}
