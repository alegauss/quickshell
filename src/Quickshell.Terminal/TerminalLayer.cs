namespace Quickshell.Terminal;

/// <summary>
/// Names the layer this assembly is, so that a reference to it reads as a layer and not as
/// a utility grab bag. The buffer and the parser land here; nothing graphical ever does.
/// </summary>
public static class TerminalLayer
{
    /// <summary>The layer's name, as the layering test and the diagnostics both spell it.</summary>
    public const string Name = "terminal";
}
