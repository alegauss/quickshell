using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Quickshell.App;

/// <summary>One stream of a command's output as an agent is given it: whole, or its head and tail.</summary>
/// <param name="Text">What is sent: the whole stream, or its head, a line naming the cut, and its tail.</param>
/// <param name="CutLines">How many lines were left out between head and tail; zero where none was.</param>
/// <param name="CutBytes">How many bytes those lines were.</param>
/// <param name="Lines">How many lines the stream has.</param>
public sealed record Budgeted(string Text, int CutLines, long CutBytes, int Lines);

/// <summary>A call about kept output refused, in words an agent can act on.</summary>
public sealed class OutputRefusal(string message) : Exception(message);

/// <summary>
/// The outputs the gateway has cut to fit an agent's context, kept whole so nothing is run twice to
/// see more of it (QS329).
///
/// <para><b>Cut at lines, keeping both ends.</b> A stream over the budget keeps its first and last
/// lines, half the budget each, with one line between them saying how much was left out and how to
/// reach it. The error is usually at the end, which is why the tail is kept as well as the head.</para>
///
/// <para><b>Kept in the gateway, for its life.</b> It already holds every byte the command wrote, so
/// paging and searching send the agent only what it asks for, and nothing runs again on the server.
/// The oldest output goes first once the spool passes its size, and all of it goes with the
/// process.</para>
/// </summary>
public sealed class OutputSpool
{
    /// <summary>What a call returns of one stream when it names no budget.</summary>
    public const int DefaultBudget = 16 * 1024;

    /// <summary>The most a call may ask for.</summary>
    public const int LargestBudget = 256 * 1024;

    /// <summary>How much output the spool keeps, in characters, before it lets the oldest go.</summary>
    public const long Capacity = 64L * 1024 * 1024;

    /// <summary>The most matching lines one search returns.</summary>
    public const int MostMatches = 200;

    private readonly LinkedList<(string Id, string Stdout, string Stderr)> _kept = new();
    private long _size;

    /// <summary>Fits <paramref name="text"/> to <paramref name="budget"/> bytes at line boundaries.</summary>
    public static Budgeted Fit(string text, int budget, string? id, string stream)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] lines = Lines(text);

        if (Encoding.UTF8.GetByteCount(text) <= budget)
        {
            return new Budgeted(text, 0, 0, lines.Length);
        }

        int half = budget / 2;
        int head = Take(lines, half, fromEnd: false);
        int tail = Math.Min(Take(lines, half, fromEnd: true), lines.Length - head);
        int cut = lines.Length - head - tail;
        long cutBytes = lines.Skip(head).Take(cut).Sum(line => (long)Encoding.UTF8.GetByteCount(line) + 1);

        StringBuilder sent = new();

        foreach (string line in lines.Take(head))
        {
            sent.Append(line).Append('\n');
        }

        sent.Append(string.Create(CultureInfo.InvariantCulture,
            $"[... {cut} lines, {cutBytes} bytes cut: lines {head + 1} to {head + cut} of {lines.Length}. "
            + $"output_page or output_grep with output \"{id}\" and stream \"{stream}\" reaches them ...]\n"));

        foreach (string line in lines.Skip(lines.Length - tail))
        {
            sent.Append(line).Append('\n');
        }

        return new Budgeted(sent.ToString(), cut, cutBytes, lines.Length);
    }

    /// <summary>Keeps a command's output and returns the id that reaches it.</summary>
    public string Keep(string stdout, string stderr)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

        lock (_kept)
        {
            _kept.AddLast((id, stdout, stderr));
            _size += stdout.Length + stderr.Length;

            while (_size > Capacity && _kept.Count > 1)
            {
                (string _, string oldOut, string oldErr) = _kept.First!.Value;

                _size -= oldOut.Length + oldErr.Length;
                _kept.RemoveFirst();
            }
        }

        return id;
    }

    /// <summary>Lines <paramref name="from"/> to <paramref name="to"/> of a kept stream, numbered, within a budget.</summary>
    public (string Text, int Next, int Lines) Page(string id, string stream, int from, int to, int budget)
    {
        string[] lines = Lines(Stream(id, stream));
        int first = Math.Max(1, from);
        int last = Math.Min(lines.Length, Math.Max(first, to));

        StringBuilder sent = new();
        int at = first;

        for (; at <= last; at++)
        {
            string numbered = string.Create(CultureInfo.InvariantCulture, $"{at}\t{lines[at - 1]}\n");

            if (sent.Length > 0 && Encoding.UTF8.GetByteCount(sent.ToString()) + Encoding.UTF8.GetByteCount(numbered) > budget)
            {
                break;
            }

            sent.Append(numbered);
        }

        return (sent.ToString(), at <= lines.Length ? at : 0, lines.Length);
    }

    /// <summary>The kept stream's lines matching <paramref name="pattern"/>, numbered, at most <see cref="MostMatches"/>.</summary>
    public (string Text, int Matches, bool More) Grep(string id, string stream, string pattern)
    {
        Regex matching;

        try
        {
            matching = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException bad)
        {
            throw new OutputRefusal($"{pattern} is not a pattern: {bad.Message}");
        }

        string[] lines = Lines(Stream(id, stream));
        StringBuilder found = new();
        int matches = 0;

        try
        {
            for (int at = 0; at < lines.Length; at++)
            {
                if (!matching.IsMatch(lines[at]))
                {
                    continue;
                }

                if (++matches <= MostMatches)
                {
                    found.Append((at + 1).ToString(CultureInfo.InvariantCulture)).Append('\t').Append(lines[at]).Append('\n');
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            throw new OutputRefusal($"{pattern} took too long on one line; make it simpler.");
        }

        return (found.ToString(), matches, matches > MostMatches);
    }

    private string Stream(string id, string stream)
    {
        lock (_kept)
        {
            foreach ((string Id, string Stdout, string Stderr) kept in _kept)
            {
                if (kept.Id == id)
                {
                    return stream switch
                    {
                        "stdout" => kept.Stdout,
                        "stderr" => kept.Stderr,
                        _ => throw new OutputRefusal("stream is stdout or stderr."),
                    };
                }
            }
        }

        throw new OutputRefusal($"There is no kept output {id}: it is kept only while this gateway runs, and the oldest goes first.");
    }

    /// <summary>How many lines from one end fit in <paramref name="bytes"/>, each with its newline.</summary>
    private static int Take(string[] lines, int bytes, bool fromEnd)
    {
        int taken = 0;
        long spent = 0;

        while (taken < lines.Length)
        {
            string line = lines[fromEnd ? lines.Length - 1 - taken : taken];
            long cost = Encoding.UTF8.GetByteCount(line) + 1;

            if (spent + cost > bytes)
            {
                break;
            }

            spent += cost;
            taken++;
        }

        return taken;
    }

    private static string[] Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        string[] lines = text.Split('\n');

        return text.EndsWith('\n') ? lines[..^1] : lines;
    }
}
