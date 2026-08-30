using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;

namespace Quickshell.Transport;

/// <summary>
/// Windows Credential Manager, reached directly.
///
/// <para><b>Why this rather than a file of our own.</b> A user can open Credential Manager, see
/// every secret this client has saved, and delete any of them — with a tool that came with their
/// computer and that they have no reason to distrust. Nothing a terminal could build would be as
/// good, and a client whose stored secrets are visible only through itself is asking to be taken on
/// faith.</para>
///
/// <para>Four calls, and no more of the API than that: write, read, delete, free.</para>
/// </summary>
internal static partial class CredentialManager
{
    /// <summary>A secret that belongs to the user and is not shared with anything else.</summary>
    private const uint Generic = 1;

    /// <summary>Kept until the user removes it, which is what "saved" has to mean.</summary>
    private const uint PersistLocalMachine = 2;

    /// <summary>The largest blob the API accepts, and a bound on what this will read back.</summary>
    private const int MaximumBlob = 2560;

    /// <summary>Writes or replaces one entry.</summary>
    public static unsafe void Write(string target, ReadOnlySpan<byte> blob)
    {
        if (blob.Length > MaximumBlob)
        {
            throw new SshException(
                SshFailureKind.Unrecognised,
                $"That secret is {blob.Length} bytes and Credential Manager takes {MaximumBlob}.",
                "Windows bounds what one credential may hold.");
        }

        fixed (byte* bytes = blob)
        fixed (char* name = target)
        fixed (char* user = target)
        {
            Credential credential = new()
            {
                Type = Generic,
                TargetName = name,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = bytes,
                Persist = PersistLocalMachine,
                UserName = user,
            };

            if (!CredWriteW(&credential, 0))
            {
                throw Failed("write", target);
            }
        }
    }

    /// <summary>Reads one entry, or null where there is none.</summary>
    public static unsafe byte[]? Read(string target)
    {
        Credential* credential = null;

        try
        {
            if (!CredReadW(target, Generic, 0, &credential))
            {
                // Not found is by far the commonest reason and is not a failure: a user who has
                // saved nothing has nothing to read.
                return Marshal.GetLastWin32Error() == 1168 ? null : throw Failed("read", target);
            }

            if (credential->CredentialBlobSize == 0 || credential->CredentialBlob is null)
            {
                return null;
            }

            return new ReadOnlySpan<byte>(credential->CredentialBlob,
                                          (int)credential->CredentialBlobSize).ToArray();
        }
        finally
        {
            if (credential is not null)
            {
                CredFree(credential);
            }
        }
    }

    /// <summary>Removes one entry.</summary>
    /// <returns>Whether there was one to remove.</returns>
    public static bool Delete(string target)
    {
        if (CredDeleteW(target, Generic, 0))
        {
            return true;
        }

        return Marshal.GetLastWin32Error() == 1168 ? false : throw Failed("delete", target);
    }

    private static SshException Failed(string what, string target)
    {
        int error = Marshal.GetLastWin32Error();

        return new SshException(
            SshFailureKind.Unrecognised,
            $"Credential Manager would not {what} the entry for {target}.",
            $"Windows answered error {error}.",
            "The secret was not saved, so nothing is stored that this client believes is there.");
    }

    /// <summary>
    /// The subset of <c>CREDENTIALW</c> this uses. Laid out in full because the struct is passed by
    /// address and a short one would have Windows reading past it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Credential
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CredWriteW(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CredReadW(string target, uint type, uint flags,
                                                 Credential** credential);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static unsafe partial void CredFree(void* buffer);
}
