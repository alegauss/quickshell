using Quickshell.App;

namespace Quickshell.App.Tests;

/// <summary>
/// A clipboard nobody else can reach, for every test that is about what the window does with text.
///
/// <para><b>QS180.</b> The system clipboard is one object every process on the desk shares: writing
/// it erased what the person at the machine had copied, and a phone-link service or a remote
/// desktop's bridge opening it between a test's write and its read made that test red on a tree
/// nothing had changed. This one holds a string and answers for it.</para>
/// </summary>
internal sealed class HeldClipboard : IClipboard
{
    /// <summary>What it holds. Empty is holding nothing, the same answer the system one gives.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>How many times something was put on it, so a test can say nothing was.</summary>
    public int Writes { get; private set; }

    /// <inheritdoc/>
    public string Read() => Text;

    /// <inheritdoc/>
    public bool Write(string text)
    {
        Text = text;
        Writes++;

        return true;
    }
}
