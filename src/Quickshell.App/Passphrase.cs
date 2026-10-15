using System.IO;
using System.Security.Cryptography;

namespace Quickshell.App;

/// <summary>
/// The passphrase a vault is written under: one generated, and an estimate of one typed (QS306).
///
/// <para><b>The passphrase is the protection.</b> A vault is a file that can be copied and guessed
/// at offline for as long as it exists, and Argon2id only makes each guess dearer. So the export
/// offers six words drawn from the EFF's large list, about 77 bits, and refuses a typed one whose
/// estimate falls under <see cref="RequiredBits"/>.</para>
///
/// <para><b>An estimate, and a deliberately suspicious one.</b> It cannot know which dictionary an
/// attacker holds, so a run of letters is taken to be a word, worth about what one word of the list
/// is worth, unless it is short enough to count letter by letter; digits that look like a year or a
/// sequence count for little; a repeated piece counts once. It errs towards refusing, and the
/// generated phrase is always there to take instead.</para>
/// </summary>
public static class Passphrase
{
    /// <summary>What a passphrase must be estimated at to be accepted: five words of the list clear it.</summary>
    public const double RequiredBits = 64;

    /// <summary>How many words a generated passphrase has.</summary>
    public const int Words = 6;

    /// <summary>What one word drawn from the list is worth: log2 of 7,776.</summary>
    public static readonly double WordBits = Math.Log2(7776);

    private static readonly Lazy<string[]> List = new(Read);

    /// <summary>The EFF large wordlist, 7,776 words.</summary>
    public static IReadOnlyList<string> Wordlist => List.Value;

    /// <summary>Six words drawn at random from the list, with a space between each.</summary>
    public static string Generate()
    {
        string[] words = List.Value;

        return string.Join(' ', Enumerable.Range(0, Words).Select(_ => words[RandomNumberGenerator.GetInt32(words.Length)]));
    }

    /// <summary>The estimated strength, in bits.</summary>
    public static double Estimate(string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);

        double bits = 0;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string run, Kind kind) in Runs(passphrase))
        {
            // The same piece again adds nothing an attacker has to guess.
            if (!seen.Add(run))
            {
                continue;
            }

            bits += kind switch
            {
                Kind.Letters => LetterBits(run),
                Kind.Digits => DigitBits(run),
                _ => run == " " ? 0 : run.Length * Math.Log2(33),
            };
        }

        return bits;
    }

    /// <summary>Why a passphrase is refused, or null where its estimate is enough.</summary>
    public static string? Refusal(string passphrase)
    {
        double bits = Estimate(passphrase);

        if (bits >= RequiredBits)
        {
            return null;
        }

        return $"This passphrase is estimated at about {Math.Floor(bits)} bits, and a vault needs at least {RequiredBits}: "
               + "anyone who copies the file can guess at it offline for as long as it exists. "
               + "Add more words, or take the generated one, which is about 77.";
    }

    /// <summary>
    /// A run of letters: a short one letter by letter, a longer one as the word it may well be, and a
    /// little more where its case is mixed past the first letter.
    /// </summary>
    private static double LetterBits(string run)
    {
        bool mixed = run.Skip(1).Any(char.IsUpper) && run.Skip(1).Any(char.IsLower);
        double perLetter = Math.Log2(mixed ? 52 : 26);

        if (run.Length <= 3)
        {
            return run.Length * perLetter;
        }

        double asWord = Math.Max(WordBits, run.Length * 1.5);

        return asWord + (mixed ? run.Length : 0) + (char.IsUpper(run[0]) ? 1 : 0);
    }

    /// <summary>A run of digits: little for a year, a sequence or one digit repeated, else each digit.</summary>
    private static double DigitBits(string run)
    {
        if (run.Length == 4 && (run.StartsWith("19", StringComparison.Ordinal) || run.StartsWith("20", StringComparison.Ordinal)))
        {
            return 7;
        }

        bool repeated = run.All(digit => digit == run[0]);
        bool rising = Enumerable.Range(1, run.Length - 1).All(at => run[at] == run[at - 1] + 1);
        bool falling = Enumerable.Range(1, run.Length - 1).All(at => run[at] == run[at - 1] - 1);

        return repeated || rising || falling ? 3 + Math.Log2(run.Length) : run.Length * Math.Log2(10);
    }

    private enum Kind
    {
        Letters,
        Digits,
        Other,
    }

    /// <summary>The passphrase cut into runs of letters, of digits, and of anything else.</summary>
    private static IEnumerable<(string Run, Kind Kind)> Runs(string passphrase)
    {
        int start = 0;

        for (int at = 1; at <= passphrase.Length; at++)
        {
            if (at == passphrase.Length || Of(passphrase[at]) != Of(passphrase[start]))
            {
                if (at > start)
                {
                    yield return (passphrase[start..at], Of(passphrase[start]));
                }

                start = at;
            }
        }
    }

    private static Kind Of(char character) =>
        char.IsLetter(character) ? Kind.Letters : char.IsAsciiDigit(character) ? Kind.Digits : Kind.Other;

    private static string[] Read()
    {
        using Stream stream = typeof(Passphrase).Assembly.GetManifestResourceStream("Quickshell.App.eff-large-wordlist.txt")
            ?? throw new InvalidOperationException("The EFF wordlist is not built into this executable.");
        using StreamReader reader = new(stream);

        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
