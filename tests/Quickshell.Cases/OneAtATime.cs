using Xunit.Sdk;
using Xunit.v3;

// QS299. Everything in this assembly drives one desk: CasesRun launches a client per case and
// ExplorerDropRun moves the real mouse across it and types into whatever has the foreground. xunit
// runs test classes side by side unless told otherwise, so a case's window could open under the
// drag or take the Return meant for the browser's path box, and the drop run went red twice in four
// guest runs, at two different steps, on trees whose code it never touched.
[assembly: Parallelization(Mode = ParallelMode.None)]
