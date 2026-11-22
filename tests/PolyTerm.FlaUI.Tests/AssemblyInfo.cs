using Xunit;

// Each test class launches its own real PolyTerm.exe via a fresh PolyTermAppFixture. Running two
// test classes concurrently launches two instances simultaneously — confirmed to cause a real,
// intermittent failure: UIA3Automation.FromHandle threw a COM E_FAIL, most likely a UIA
// initialization race between the two concurrently-starting automation clients. Sequential
// execution avoids it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Routes Console.WriteLine into each test's own output, which the runner writes to the .trx (issue
// #206). The helpers' diagnostics (which activation path a button press took, what
// ResetToMainWindow had to close, CloseDialog retries) are plain Console.WriteLine calls, and
// without this they went nowhere: the failed tests on PR #201's first run had an empty StdOut.
[assembly: CaptureConsole]
