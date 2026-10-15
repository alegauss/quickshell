using Xunit.Sdk;
using Xunit.v3;

// QS299. Everything in this assembly drives one desk: each of CasesRun's two tests launches a client
// per case, and some cases press keys and drag the real mouse. xunit runs tests side by side unless
// told otherwise, so one test's window could open under the other's drag or take a Return meant for
// its path box; the Explorer drop went red twice in four guest runs that way, at two different steps,
// on trees whose code it never touched.
[assembly: Parallelization(Mode = ParallelMode.None)]
