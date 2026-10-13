using Xunit;

namespace Quickshell.Transport.Tests;

/// <summary>
/// The tests that open a Pageant window, stand-in or real, run one at a time: there is one window
/// called Pageant per desktop that the client can find, and two would answer for each other.
/// </summary>
[CollectionDefinition(Collection, DisableParallelization = true)]
public sealed class PageantWindow
{
    public const string Collection = "Pageant's window";
}
