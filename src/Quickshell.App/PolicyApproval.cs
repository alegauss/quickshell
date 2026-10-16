using System.IO;

namespace Quickshell.App;

/// <summary>
/// The version of the agent policy the person last approved (QS339), kept beside the policy, so a change
/// made outside quickshell's own window, an agent's script among them, is held until the person answers.
///
/// <para><b>The gateway enforces the approved version, not whatever is on disk.</b> While the policy
/// differs from its approved copy, every call is refused, saying the policy waits for the person; the
/// window shows the change and the person accepts it, which makes it the approved copy, or rejects it,
/// which puts the approved copy back. A file nobody approved yet is held the same way, so a policy that
/// appears from nowhere widens nothing.</para>
///
/// <para><b>Not a boundary.</b> A process running as the same user can write both files; the hook keeps
/// the agent's own tools off them, and the design says plainly that the server-side layer is the one that
/// holds.</para>
/// </summary>
public static class PolicyApproval
{
    /// <summary>Where the approved copy of <paramref name="policyFile"/> is kept.</summary>
    public static string ApprovedCopy(string policyFile) => policyFile + ".approved";

    /// <summary>Whether the policy on disk is the one the person approved; no file and no copy is approved too.</summary>
    public static bool Approved(string policyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyFile);

        string copy = ApprovedCopy(policyFile);
        bool present = File.Exists(policyFile);

        if (!present && !File.Exists(copy))
        {
            return true;
        }

        if (!present || !File.Exists(copy))
        {
            return false;
        }

        return File.ReadAllBytes(policyFile).AsSpan().SequenceEqual(File.ReadAllBytes(copy));
    }

    /// <summary>The change waiting for the person, as a diff from the approved copy to the file on disk.</summary>
    public static string Change(string policyFile)
    {
        string copy = ApprovedCopy(policyFile);
        string before = File.Exists(copy) ? File.ReadAllText(copy) : string.Empty;
        string after = File.Exists(policyFile) ? File.ReadAllText(policyFile) : string.Empty;

        return RemoteText.Diff(Path.GetFileName(policyFile), Lines(before), Lines(after));
    }

    /// <summary>Accepts what is on disk as the policy the person approved.</summary>
    public static void Accept(string policyFile)
    {
        string copy = ApprovedCopy(policyFile);

        if (File.Exists(policyFile))
        {
            File.Copy(policyFile, copy, overwrite: true);
        }
        else if (File.Exists(copy))
        {
            File.Delete(copy);
        }
    }

    /// <summary>Rejects what is on disk, putting the approved policy back, or removing one nobody approved.</summary>
    public static void Reject(string policyFile)
    {
        string copy = ApprovedCopy(policyFile);

        if (File.Exists(copy))
        {
            File.Copy(copy, policyFile, overwrite: true);
        }
        else if (File.Exists(policyFile))
        {
            File.Delete(policyFile);
        }
    }

    private static string[] Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        string[] lines = text.ReplaceLineEndings("\n").Split('\n');

        return text.EndsWith('\n') ? lines[..^1] : lines;
    }
}
