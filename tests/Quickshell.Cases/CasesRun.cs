using System.IO;

using Winwright.Processes;
using Winwright.Projects;
using Winwright.Scenarios;
using Winwright.Verdicts;
using Winwright.Windowing;

using Xunit;

namespace Quickshell.Cases;

/// <summary>
/// Every UI case this repository declares, run against a launched application.
///
/// <para><b>QS147, and what it replaces.</b> Three dialogs in this client were verified by granting
/// foreground, sending a synthetic keystroke and photographing the screen. Two worked. The third
/// could not be photographed at all — Windows declined foreground twenty-five times running on an
/// unattended desk, which is also the state that makes DXGI report no frame statistics and is
/// exactly the state CI is in permanently. A method that needs somebody at the machine is not a
/// harness.</para>
///
/// <para>Reading the accessibility tree needs neither foreground nor a capture. A picture is still
/// worth taking for a human to look at; it should not be the evidence.</para>
///
/// <para><b>One test and not one per case</b>, following the sibling project that already runs this
/// way: the engine runs the selection and answers a verdict over all of it, and splitting it per
/// case would relaunch the application each time and throw away the window the fixtures lend.</para>
/// </summary>
public sealed class CasesRun
{
    /// <summary>
    /// Runs them, or skips with the reason this desk could not be observed.
    ///
    /// <para><b>A desk that cannot be observed is a third verdict, and xunit.v3 has the word for
    /// it (QS222).</b> This used to pass, saying in a message nobody reads that it checked nothing,
    /// because xUnit had no other verdict; QS136's table then counted a UI suite that drove no case
    /// as one passed test. A skip shows in that table with the desk's reason and is held to
    /// <c>tests/skips.json</c>, where this assembly has no allowance — so a desk that lost its
    /// observer between two runs fails the run instead of passing it quietly.</para>
    /// </summary>
    [Fact]
    public void EveryUiCaseInThisRepositoryRuns()
    {
        Desk desk = Desk.Read();

        if (!desk.CanObserve)
        {
            Assert.Skip($"no UI case ran: this desk lacks {desk.FirstAbsent!.Name}");
        }

        string repository = Repository.Root;

        ProjectDeclaration project = ProjectDeclaration.Find(repository);
        IReadOnlyList<CaseDeclaration> declared =
            ScenarioFile.Across(ScenarioFile.LoadAll(Path.Combine(repository, "cases")));

        using ProcessRegister register = ProcessRegister.For(project);

        // No working directory set here since winwright 1.0.1: the engine starts the application in
        // the directory winwright.json sits in, so a fixture argument naming a file in the repository
        // means that file under every runner (QS181, WW508).
        SuiteVerdict verdict = Suite.Launch(declared, Selection.All, register, project);

        // The whole reading and not the outcome: xUnit shows one message, so the message has to be
        // the report. A red that says "Broken" and nothing else costs a debugger session to turn
        // into a sentence — this session already paid that twice by truncating a log.
        Assert.True(verdict.Outcome == RunOutcome.Passed, Read(verdict));
        Assert.Equal(0, verdict.ExitCode);

        // And nothing this run started is still on the desk.
        Assert.Empty(register.StopAll());
    }

    /// <summary>
    /// The whole reading, down to what each failed check actually read.
    ///
    /// <para>A line per case names the case and not what it saw, which leaves a reader attaching a
    /// debugger to find out. Failures, then what could not be asked, then what broke before either —
    /// and the trace, because a case can fail at a step that is not where it went wrong.</para>
    /// </summary>
    private static string Read(SuiteVerdict verdict)
    {
        List<string> lines = [.. verdict.Render()];

        foreach (CaseResult unhappy in verdict.Unhappy)
        {
            lines.Add(string.Empty);
            lines.Add($"{unhappy.Declared.Name}:");

            lines.AddRange(unhappy.Verdict.Failures.Select(one => $"  failed    {one}"));
            lines.AddRange(unhappy.Verdict.Unchecked.Select(one => $"  unchecked {one}"));
            lines.AddRange(unhappy.Verdict.Broke.Select(one => $"  broke     {one}"));
            lines.AddRange(unhappy.Trace.Select(one => $"  trace     {one}"));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
