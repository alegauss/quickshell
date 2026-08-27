using Xunit.Sdk;
using Xunit.v3;

// One at a time, and for a reason that is not tidiness.
//
// These tests start real processes and one of them counts this process's handles before and after
// twenty sessions. A second test opening a channel in the middle of that measurement makes the
// number mean nothing, and the failure it produces is a leak report about code that does not leak.
[assembly: Parallelization(Mode = ParallelMode.None)]
