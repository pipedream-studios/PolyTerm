using Xunit;

// Each test class launches its own real PolyTerm.exe via a fresh PolyTermAppFixture. Running two
// test classes concurrently launches two instances simultaneously — confirmed to cause a real,
// intermittent failure: UIA3Automation.FromHandle threw a COM E_FAIL, most likely a UIA
// initialization race between the two concurrently-starting automation clients. Sequential
// execution avoids it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
