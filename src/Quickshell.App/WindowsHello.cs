using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Quickshell.App;

/// <summary>What Windows Hello answered.</summary>
public enum HelloVerdict
{
    /// <summary>The person at the desk proved they are this account's owner.</summary>
    Verified,

    /// <summary>They declined, or the attempts ran out.</summary>
    Declined,

    /// <summary>
    /// Windows Hello is not there to ask: no PIN or biometric set up for this account, or switched off
    /// by policy.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Asks Windows Hello, the account's PIN, face or fingerprint, to confirm the person at the desk
/// before something sensitive is done for them (QS306).
///
/// <para><b>UserConsentVerifier, reached through its COM interop interface</b> and not through a
/// WinRT projection. The projection is a further assembly of several tens of megabytes beside a
/// client sold on being light, to make one call; the interop interface is the same verifier, with
/// the prompt owned by this window rather than floating unparented. The asynchronous answer is read
/// by polling its status, which spares implementing a completion handler as a COM object.</para>
/// </summary>
public static partial class WindowsHello
{
    private const string ClassName = "Windows.Security.Credentials.UI.UserConsentVerifier";

    /// <summary>IUserConsentVerifierInterop, from UserConsentVerifierInterop.h.</summary>
    private static readonly Guid Interop = new("39E050C3-4E74-441A-8DC0-B81104DF949C");

    /// <summary>IAsyncInfo, which every WinRT asynchronous operation carries.</summary>
    private static readonly Guid AsyncInfo = new("00000036-0000-0000-C000-000000000046");

    /// <summary>IAsyncOperation&lt;T&gt;'s own identifier, the generic type a parameterised one is derived from.</summary>
    private const string AsyncOperation = "{9fc2b0bb-e446-44e2-aa61-9cab8f636af2}";

    /// <summary>IAsyncOperation&lt;UserConsentVerificationResult&gt;, derived as WinRT derives it.</summary>
    private static readonly Guid VerificationOperation =
        Parameterised($"pinterface({AsyncOperation};enum(Windows.Security.Credentials.UI.UserConsentVerificationResult;i4))");

    /// <summary>
    /// Asks, with the prompt owned by <paramref name="window"/>, and answers once the person has.
    /// </summary>
    /// <param name="window">The window the prompt belongs to.</param>
    /// <param name="message">What the prompt says the confirmation is for.</param>
    public static async Task<HelloVerdict> VerifyAsync(nint window, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        nint operation = Request(window, message);

        try
        {
            while (Status(operation) == 0)
            {
                await Task.Delay(50).ConfigureAwait(true);
            }

            if (Status(operation) != 1)
            {
                return HelloVerdict.Declined;
            }

            // Verified, DeviceNotPresent, NotConfiguredForUser, DisabledByPolicy, DeviceBusy,
            // RetriesExhausted, Canceled.
            return Result(operation) switch
            {
                0 => HelloVerdict.Verified,
                1 or 2 or 3 => HelloVerdict.Unavailable,
                _ => HelloVerdict.Declined,
            };
        }
        finally
        {
            Marshal.Release(operation);
        }
    }

    /// <summary>The completed operation's result: GetResults, after IInspectable's six and the two Completed accessors.</summary>
    private static unsafe int Result(nint operation)
    {
        int result;

        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(operation, 8))(operation, &result));

        return result;
    }

    /// <summary>Starts the verification, answering the asynchronous operation it returns.</summary>
    private static unsafe nint Request(nint window, string message)
    {
        nint name = HString(ClassName);
        nint said = HString(message);
        nint factory = 0;

        try
        {
            Guid interop = Interop;

            Check(RoGetActivationFactory(name, &interop, &factory));

            Guid wanted = VerificationOperation;
            nint operation;

            // RequestVerificationForWindowAsync, after IInspectable's six.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, Guid*, nint*, int>)Slot(factory, 6))(
                factory, window, said, &wanted, &operation));

            return operation;
        }
        finally
        {
            if (factory != 0)
            {
                Marshal.Release(factory);
            }

            _ = WindowsDeleteString(said);
            _ = WindowsDeleteString(name);
        }
    }

    /// <summary>The operation's status: started, completed, cancelled or failed, as 0 to 3.</summary>
    private static unsafe int Status(nint operation)
    {
        Guid iid = AsyncInfo;
        nint info;

        Check(Marshal.QueryInterface(operation, in iid, out info));

        try
        {
            int status;

            // get_Status, after IInspectable's six and get_Id.
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 7))(info, &status));

            return status;
        }
        finally
        {
            Marshal.Release(info);
        }
    }

    /// <summary>
    /// The identifier WinRT gives an instance of a generic interface: a version 5 UUID over its
    /// signature, in WinRT's own namespace.
    /// </summary>
    public static Guid Parameterised(string signature)
    {
        byte[] space = [0x11, 0xF4, 0x7A, 0xD5, 0x7B, 0x73, 0x42, 0xC0, 0xAB, 0xAE, 0x87, 0x8B, 0x1E, 0x16, 0xAD, 0xEE];
        byte[] hash = SHA1.HashData([.. space, .. Encoding.UTF8.GetBytes(signature)]);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private static unsafe nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static void Check(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static unsafe nint HString(string text)
    {
        nint made;

        fixed (char* characters = text)
        {
            Check(WindowsCreateString(characters, (uint)text.Length, &made));
        }

        return made;
    }

    [LibraryImport("combase.dll")]
    private static unsafe partial int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);

    [LibraryImport("combase.dll")]
    private static unsafe partial int WindowsCreateString(char* sourceString, uint length, nint* made);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint made);
}
