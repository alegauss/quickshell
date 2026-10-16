using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Quickshell.App;

/// <summary>
/// quickshell as a small SSH certificate authority for its agents (QS341): a server set up to trust it takes
/// the agent in on a certificate signed for minutes before each connection, so a key that leaks is worth
/// nothing once the last certificate signed for it has run out, and nothing on the server has to change
/// when a key does.
///
/// <para><b>One authority per person, in quickshell's folder.</b> Its private half is kept sealed with the
/// Windows data protection API for this account, never in the clear: it is unsealed into a file of its
/// own only while <c>ssh-keygen -s</c> signs, and that file is deleted after. A process running as the
/// same Windows account can unseal it too, which the threat model (QS320) says of everything on this
/// side; a certificate signed by it still only opens the account the server limited.</para>
///
/// <para><b>The server trusts it in the agent's <c>authorized_keys</c></b>, as a
/// <c>cert-authority,principals="&lt;account&gt;"</c> line carrying the same <c>restrict</c> and
/// <c>from=</c> a plain key would, so the setup never edits <c>sshd_config</c> or reloads sshd.</para>
///
/// <para><b>Signed with the OpenSSH that ships with Windows</b>, the tool every server's sshd already
/// agrees with about what a certificate is, rather than a format written here.</para>
/// </summary>
public sealed class AgentAuthority
{
    /// <summary>What the sealed private half is bound to, beside this account.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("quickshell-agent-authority");

    /// <summary>An authority kept in <paramref name="folder"/>, made the first time it is needed.</summary>
    public AgentAuthority(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Folder = folder;
    }

    /// <summary>The one in <see cref="Locations.AgentKeys"/>.</summary>
    public static AgentAuthority Installed => new(Locations.Current.AgentKeys);

    /// <summary>Where it is kept.</summary>
    public string Folder { get; }

    /// <summary>How long a certificate it signs is good for unless a caller says otherwise.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The public half, one line as a <c>.pub</c> holds it, which a server is set up to trust.</summary>
    private string PublicFile => Path.Combine(Folder, "ca_ed25519.pub");

    /// <summary>The private half, sealed for this Windows account.</summary>
    private string SealedFile => Path.Combine(Folder, "ca_ed25519.sealed");

    /// <summary>Whether it has been made.</summary>
    public bool Exists => File.Exists(PublicFile) && File.Exists(SealedFile);

    /// <summary>The public key line a server trusts, the authority made first where it is not there.</summary>
    public string PublicKey()
    {
        if (!Exists)
        {
            Make();
        }

        return File.ReadAllText(PublicFile).Trim();
    }

    /// <summary>
    /// A certificate for the agent key at <paramref name="key"/>, written beside it as <c>-cert.pub</c>,
    /// for <paramref name="principal"/> alone, good from a minute ago (a server's clock may be behind) for
    /// <paramref name="lifetime"/>.
    /// </summary>
    /// <returns>Where the certificate is.</returns>
    /// <exception cref="AgentSetupRefusal">There is no authority yet, or ssh-keygen would not sign.</exception>
    public string Sign(string key, string principal, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);

        if (!Exists)
        {
            throw new AgentSetupRefusal("There is no agent certificate authority in quickshell's folder yet. Set the agent account up "
                                        + "again from the session menu, which makes one.");
        }

        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "A certificate must be good for some time.");
        }

        string unsealed = Path.Combine(Folder, "signing-" + Guid.NewGuid().ToString("N"));
        string certificate = key + "-cert.pub";

        try
        {
            Unsealed(unsealed);
            File.Delete(certificate);

            string seconds = ((long)Math.Ceiling(lifetime.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

            Keygen("-q", "-s", unsealed, "-I", $"quickshell-agent-{principal}", "-n", principal, "-V", $"-60s:+{seconds}s", key + ".pub");
        }
        finally
        {
            File.Delete(unsealed);
        }

        return File.Exists(certificate)
            ? certificate
            : throw new AgentSetupRefusal($"ssh-keygen signed no certificate for {key}.");
    }

    /// <summary>Makes the pair, and seals the private half straight away.</summary>
    private void Make()
    {
        Directory.CreateDirectory(Folder);

        string made = Path.Combine(Folder, "making-" + Guid.NewGuid().ToString("N"));

        try
        {
            Keygen("-q", "-t", "ed25519", "-N", "", "-C", "quickshell-agent-authority", "-f", made);
            File.WriteAllBytes(SealedFile, ProtectedData.Protect(File.ReadAllBytes(made), Entropy, DataProtectionScope.CurrentUser));
            File.Move(made + ".pub", PublicFile, overwrite: true);
        }
        finally
        {
            File.Delete(made);
            File.Delete(made + ".pub");
        }
    }

    /// <summary>
    /// The private half in the clear at <paramref name="path"/>, in a file this account alone may open: made
    /// with that access list, not given it afterwards, since ssh-keygen refuses a key others could read and
    /// a folder's inherited list may name others.
    /// </summary>
    private void Unsealed(string path)
    {
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        FileSecurity alone = new();

        alone.SetOwner(me);
        alone.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        alone.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));

        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(SealedFile), Entropy, DataProtectionScope.CurrentUser);

        using FileStream written = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, alone);

        written.Write(plain);
        CryptographicOperations.ZeroMemory(plain);
    }

    /// <summary>Runs the OpenSSH that ships with Windows, and says what it said where it failed.</summary>
    private static void Keygen(params string[] arguments)
    {
        ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process keygen = Process.Start(start) ?? throw new AgentSetupRefusal("ssh-keygen did not start.");

        string error = keygen.StandardError.ReadToEnd();

        keygen.WaitForExit();

        if (keygen.ExitCode != 0)
        {
            throw new AgentSetupRefusal($"ssh-keygen refused: {error.Trim()}");
        }
    }
}
