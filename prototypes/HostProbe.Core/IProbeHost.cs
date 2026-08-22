namespace HostProbe.Core;

/// <summary>
/// What the driver needs from a host. Every host implements exactly this, so the run is the
/// same run three times and the only variable is how a frame reaches the screen.
/// </summary>
public interface IProbeHost
{
    /// <summary>How the report names this host.</summary>
    string HostName { get; }

    /// <summary>The state the render loop draws from and the input handler writes to.</summary>
    Pane Pane { get; }

    /// <summary>Runs an action on the host's UI thread and waits for it.</summary>
    void RunOnUi(Action action);

    /// <summary>The pane's rectangle in screen pixels. Read on the UI thread.</summary>
    (int X, int Y, int Width, int Height) PaneScreenRect { get; }

    /// <summary>The top-level window, for the screenshots and for taking the foreground.</summary>
    nint WindowHandle { get; }

    /// <summary>Opens a dropdown positioned so that it overlaps the running pane.</summary>
    void OpenDropdown();

    void CloseDropdown();

    /// <summary>Shows a modal dialog overlapping the running pane, without blocking the driver.</summary>
    void ShowModal();

    void CloseModal();

    /// <summary>Closes the application once the run is written.</summary>
    void Shutdown();
}
