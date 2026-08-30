using System.Text;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// Where a saved password rests, and what it is honestly protected from.
///
/// <para>The Credential Manager tests write into the real Credential Manager and remove what they
/// wrote. That is deliberate: a store tested against a stub would prove this client can talk to the
/// stub, and the thing worth knowing is whether Windows accepts the entry — and whether the user can
/// then find it in the tool they already have.</para>
/// </summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"quickshell-secrets-{Guid.NewGuid():N}");

    private readonly List<SshEndpoint> _wrote = [];

    private static SshEndpoint Somewhere => SshEndpoint.For("host.example", "user");

    public void Dispose()
    {
        // Whatever reached the real Credential Manager comes out again, whether the test passed or
        // not: a suite that leaves entries behind is a suite that pollutes a user's own store.
        foreach (SshEndpoint endpoint in _wrote)
        {
            try
            {
                SecretStore.Installed().Forget(endpoint);
            }
            catch (SshException)
            {
                // Already gone is the outcome this was arranging.
            }
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- The buffer: a password is never a string ----

    /// <summary>
    /// The design's own criterion: a password is never present in a managed string. Enforced by
    /// there being nowhere to put one — the credential takes a buffer and nothing else.
    /// </summary>
    [Fact]
    public void APasswordHasNowhereToBeAString()
    {
        System.Reflection.ConstructorInfo[] constructors =
            typeof(SshCredential.Password).GetConstructors();

        Assert.All(constructors, constructor =>
            Assert.DoesNotContain(constructor.GetParameters(),
                                  parameter => parameter.ParameterType == typeof(string)));

        Assert.DoesNotContain(typeof(SshCredential.Password).GetProperties(),
                              property => property.PropertyType == typeof(string));
    }

    /// <summary>Erasing means erasing: the bytes are zero afterwards, not merely unreachable.</summary>
    [Fact]
    public void ASecretIsZeroedRatherThanAbandoned()
    {
        Secret secret = Secret.From("hunter2");

        Assert.Equal("hunter2", Encoding.UTF8.GetString(secret.Bytes));
        Assert.False(secret.IsErased);

        secret.Dispose();

        Assert.True(secret.IsErased);
        Assert.True(secret.Bytes.ToArray().All(byte_ => byte_ == 0),
                    "a disposed secret still had its bytes");

        // And twice is not an error, because a credential and its owner may both dispose it.
        secret.Dispose();
    }

    /// <summary>Disposing the credential erases the password it was given.</summary>
    [Fact]
    public void DisposingACredentialErasesItsPassword()
    {
        Secret secret = Secret.From("hunter2");
        SshCredential.Password credential = new(secret);

        credential.Dispose();

        Assert.True(secret.IsErased);
    }

    // ---- Credential Manager: the user's own tool, holding the entry ----

    [Fact]
    public void WhatIsSavedComesBackAndWhatIsForgottenDoesNot()
    {
        SecretStore store = SecretStore.Installed();

        Remember(Somewhere);

        using (Secret secret = Secret.From("a password"))
        {
            store.Save(Somewhere, secret);
        }

        using (Secret? read = store.Load(Somewhere))
        {
            Assert.NotNull(read);
            Assert.Equal("a password", Encoding.UTF8.GetString(read.Bytes));
        }

        Assert.True(store.Forget(Somewhere));
        Assert.Null(store.Load(Somewhere));
        Assert.False(store.Forget(Somewhere));
    }

    /// <summary>Nothing saved is nothing read, rather than an error a caller has to catch.</summary>
    [Fact]
    public void AnAccountWithNothingSavedReadsAsNothing()
    {
        Assert.Null(SecretStore.Installed().Load(SshEndpoint.For("never.saved", "nobody")));
    }

    // ---- The file store, and what a master password changes ----

    [Fact]
    public void AFileStoreRoundTripsAndTheFileIsNotThePassword()
    {
        SecretStore store = SecretStore.In(_directory);

        using (Secret secret = Secret.From("a password"))
        {
            store.Save(Somewhere, secret);
        }

        using Secret? read = store.Load(Somewhere);

        Assert.NotNull(read);
        Assert.Equal("a password", Encoding.UTF8.GetString(read.Bytes));

        // The obvious failure, checked rather than assumed: the plaintext is not sitting in the file.
        string written = Directory.EnumerateFiles(_directory).Single();

        Assert.DoesNotContain("a password",
                              Encoding.UTF8.GetString(File.ReadAllBytes(written)),
                              StringComparison.Ordinal);
    }

    /// <summary>
    /// A master password is what the ciphertext actually depends on: the wrong one does not open it,
    /// and it fails as "there is nothing here" rather than as a crash a caller must handle.
    /// </summary>
    [Fact]
    public void TheWrongMasterPasswordOpensNothing()
    {
        using Secret right = Secret.From("open sesame");
        using Secret wrong = Secret.From("open sesamf");

        using (Secret secret = Secret.From("a password"))
        {
            SecretStore.In(_directory, right).Save(Somewhere, secret);
        }

        Assert.Null(SecretStore.In(_directory, wrong).Load(Somewhere));

        using Secret? read = SecretStore.In(_directory, right).Load(Somewhere);

        Assert.NotNull(read);
        Assert.Equal("a password", Encoding.UTF8.GetString(read.Bytes));
    }

    /// <summary>
    /// The entry's name is bound into the ciphertext, so a saved password cannot be replayed as
    /// another host's by anybody who can write to the store.
    /// </summary>
    [Fact]
    public void CiphertextMovedToAnotherHostDoesNotOpen()
    {
        using Secret master = Secret.From("open sesame");

        SshEndpoint elsewhere = SshEndpoint.For("elsewhere.example", "user");
        SecretStore store = SecretStore.In(_directory, master);

        using (Secret secret = Secret.From("a password"))
        {
            store.Save(Somewhere, secret);
        }

        // The same bytes, filed under a different host, which is what an attacker with write access
        // to the store would try.
        string original = Directory.EnumerateFiles(_directory).Single();

        using (Secret placeholder = Secret.From("x"))
        {
            store.Save(elsewhere, placeholder);
        }

        string other = Directory.EnumerateFiles(_directory)
                                .Single(file => !string.Equals(file, original, StringComparison.Ordinal));

        File.Copy(original, other, overwrite: true);

        Assert.Null(store.Load(elsewhere));
    }

    /// <summary>
    /// Portable mode requires a master password rather than offering one, and says why. DPAPI binds
    /// to one Windows account and a portable install is by definition one that may meet another.
    /// </summary>
    [Fact]
    public void PortableModeWillNotSaveWithoutAMasterPassword()
    {
        SshException refused = Assert.Throws<SshException>(() =>
            SecretStore.In(_directory, master: null, portable: true));

        Assert.Contains("master password", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DPAPI", refused.Means, StringComparison.Ordinal);

        using Secret master = Secret.From("open sesame");

        SecretStore store = SecretStore.In(_directory, master, portable: true);

        Assert.True(store.Portable);
        Assert.True(store.HasMasterPassword);
    }

    // ---- What the user is told ----

    /// <summary>
    /// The design's other criterion: the settings surface states what DPAPI alone does not protect
    /// against. The words live here rather than in a window so that every surface says the same
    /// thing and this can assert on them.
    /// </summary>
    [Fact]
    public void TheWordingSaysWhatDpapiDoesNotDo()
    {
        string said = SecretStore.WhatThisDoesNotProtect;

        // What it does: a copy will not open elsewhere.
        Assert.Contains("another machine", said, StringComparison.OrdinalIgnoreCase);

        // What it does not: anything already running as this user.
        Assert.Contains("not protected", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("running as you", said, StringComparison.OrdinalIgnoreCase);

        // And what to do about that.
        Assert.Contains("master password", said, StringComparison.OrdinalIgnoreCase);
    }

    private void Remember(SshEndpoint endpoint) => _wrote.Add(endpoint);
}
