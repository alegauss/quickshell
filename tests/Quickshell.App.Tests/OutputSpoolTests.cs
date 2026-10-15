using System.Text;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>QS329: output fitted to an agent's budget at line boundaries, both ends kept, the rest reachable.</summary>
public sealed class OutputSpoolTests
{
    private static string Numbers(int count) => string.Concat(Enumerable.Range(1, count).Select(each => $"line {each}\n"));

    [Fact]
    public void OutputWithinTheBudgetComesBackWhole()
    {
        Budgeted fitted = OutputSpool.Fit("one\ntwo\n", 1024, null, "stdout");

        Assert.Equal("one\ntwo\n", fitted.Text);
        Assert.Equal(0, fitted.CutLines);
        Assert.Equal(2, fitted.Lines);
    }

    /// <summary>Over the budget: whole lines from each end, and one line naming the cut and how to reach it.</summary>
    [Fact]
    public void OutputOverTheBudgetKeepsBothEndsAndNamesTheCut()
    {
        string text = Numbers(10000);
        Budgeted fitted = OutputSpool.Fit(text, 1000, "abc123def456", "stdout");

        Assert.True(Encoding.UTF8.GetByteCount(fitted.Text) <= 1000 + 200, $"{Encoding.UTF8.GetByteCount(fitted.Text)} bytes");
        Assert.StartsWith("line 1\nline 2\n", fitted.Text, StringComparison.Ordinal);
        Assert.EndsWith("line 9999\nline 10000\n", fitted.Text, StringComparison.Ordinal);
        Assert.Contains("output \"abc123def456\"", fitted.Text, StringComparison.Ordinal);
        Assert.Equal(10000, fitted.Lines);
        Assert.True(fitted.CutLines > 9000);
        Assert.Equal(10000 - fitted.CutLines, fitted.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1);
    }

    [Fact]
    public void AKeptOutputIsPagedAndSearchedByLine()
    {
        OutputSpool spool = new();
        string id = spool.Keep(Numbers(500), "warning: disk\n");

        (string page, int next, int lines) = spool.Page(id, "stdout", 250, 252, 4096);

        Assert.Equal("250\tline 250\n251\tline 251\n252\tline 252\n", page);
        Assert.Equal(253, next);
        Assert.Equal(500, lines);

        (string found, int matches, bool more) = spool.Grep(id, "stdout", "^line 4[0-9]{2}$");

        Assert.Equal(100, matches);
        Assert.False(more);
        Assert.StartsWith("400\tline 400\n", found, StringComparison.Ordinal);
        Assert.Equal("1\twarning: disk\n", spool.Grep(id, "stderr", "disk").Text);
    }

    [Fact]
    public void AnOutputNotKeptSaysSo() =>
        Assert.Contains("no kept output", Assert.Throws<OutputRefusal>(() => new OutputSpool().Page("000000000000", "stdout", 1, 2, 100)).Message,
                        StringComparison.Ordinal);
}
