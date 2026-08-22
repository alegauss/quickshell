using Xunit.Sdk;
using Xunit.v3;

// These tests share the screen, which is a global resource. Run in parallel they each put a
// topmost window up and occlude one another, DXGI answers DXGI_STATUS_OCCLUDED, frame statistics
// stop advancing, and the queue-depth measurement fails on about two runs in three - a real
// measurement defeated by the harness rather than by the code it is measuring.
[assembly: Parallelization(Mode = ParallelMode.None)]
