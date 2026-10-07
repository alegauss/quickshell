using System.IO;

namespace Quickshell.App;

/// <summary>
/// A portable copy's settings and saved sessions, brought into the copy it installs — once, and
/// only into a profile that has none of its own (QS192).
///
/// <para><b>The natural path through this client is to try it portable first.</b> Unzip, run,
/// import the MobaXterm sessions, and install once it has earned that. An install that left the
/// sessions in the portable copy's <c>data\</c> opened an installed copy with none of them and no
/// word of where they went.</para>
///
/// <para><b>Copied, never moved, and never merged.</b> The portable copy keeps working with its own.
/// Where the installed copy already has a settings folder, nothing is touched: two sets of sessions
/// are a decision a person makes, and the sentence says where the portable ones are instead.</para>
///
/// <para><b>What describes a person's setup, and nothing that describes the other copy</b>: the
/// settings, the saved sessions, the window placements and the answer about closing. Never the
/// logs, the crash reports, the recordings or the diagnostic bundles.</para>
/// </summary>
public static class PortableData
{
    /// <summary>The files carried, by name under the data folder.</summary>
    public static readonly IReadOnlyList<string> Carried =
        ["settings.json", "sessions.json", "windows.json", "close-without-asking"];

    /// <summary>Copies a portable copy's setup into an installed copy's folder, where it has none.</summary>
    /// <param name="portable">The portable copy's data folder.</param>
    /// <param name="installed">The installed copy's own folder, as it will discover it.</param>
    /// <returns>The sentence the finished install adds, or empty where there was nothing to say.</returns>
    public static string Carry(string portable, string installed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portable);
        ArgumentException.ThrowIfNullOrWhiteSpace(installed);

        string[] there = [.. Carried.Where(name => File.Exists(Path.Combine(portable, name)))];

        if (there.Length == 0)
        {
            return string.Empty;
        }

        if (Directory.Exists(installed))
        {
            return $"Your installed copy already had its own settings in {installed}, so nothing was "
                   + $"brought across: the portable copy's settings and saved sessions are still in {portable}.";
        }

        Directory.CreateDirectory(installed);

        foreach (string name in there)
        {
            File.Copy(Path.Combine(portable, name), Path.Combine(installed, name));
        }

        string what = there.Contains("sessions.json", StringComparer.Ordinal)
            ? "settings and saved sessions"
            : "settings";

        return $"Your {what} came with it, copied into {installed}; the portable copy keeps its own.";
    }
}
