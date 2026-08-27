using Xunit.Sdk;
using Xunit.v3;

// One at a time. Two of these measure how long a read waited between arriving and being parsed, and
// a second test saturating the thread pool in the middle of that measurement makes the number a fact
// about the test host rather than about the pipeline.
[assembly: Parallelization(Mode = ParallelMode.None)]
