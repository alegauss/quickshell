using System.Globalization;
using System.IO;

namespace Quickshell.App;

/// <summary>
/// The kill switch for agents (QS334): one chord that stops every agent gateway on this account, and
/// one palette command that lets them run again.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The file that stops the gateways: <see cref="Locations.AgentStop"/> unless a caller says otherwise.</summary>
    public string? AgentStopFile { get; set; }

    private string AgentStopFileOrDefault => AgentStopFile ?? Locations.Current.AgentStop;

    /// <summary>
    /// Stops every agent: writes the file each gateway watches, which cancels their jobs, closes their
    /// connections and refuses their calls until <see cref="LetAgentsRun"/>.
    /// </summary>
    public void StopAgents()
    {
        string file = AgentStopFileOrDefault;

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + "\n");

        Say("Every agent is stopped. Each gateway cancels the jobs it started, closes its connections and refuses what an "
            + "agent asks, until Let agents run again in the palette.", "Agents");
    }

    /// <summary>Lets the gateways answer agents again, after a stop.</summary>
    public void LetAgentsRun()
    {
        string file = AgentStopFileOrDefault;

        if (!File.Exists(file))
        {
            Say("No agent is stopped.", "Agents");

            return;
        }

        File.Delete(file);
        Say("Agents can work through the gateway again. Nothing that was cancelled has been started again.", "Agents");
    }

    /// <summary>Ctrl+Shift+F12 and the palette: stop every agent (QS334).</summary>
    private sealed class StoppingAgents(MainWindow window) : Doing(window)
    {
        /// <inheritdoc/>
        public override string Name => "Stop every agent";

        /// <inheritdoc/>
        public override void Execute(object? parameter) => Window.StopAgents();
    }

    /// <summary>The palette, alone: undoing a stop is decided, never pressed by accident.</summary>
    private sealed class LettingAgentsRun(MainWindow window) : Doing(window)
    {
        /// <inheritdoc/>
        public override string Name => "Let agents run again";

        /// <inheritdoc/>
        public override void Execute(object? parameter) => Window.LetAgentsRun();
    }
}
