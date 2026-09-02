using Xunit;

// The AddIn under test is a process-wide singleton-ish surface (static bus file
// override, static write queue) — run test classes strictly sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
