using Xunit;

// E# compiler tests emit, verify, and load temporary assemblies in-process. Several
// legacy helpers still share process-global compiler/loader state, so parallel xUnit
// collections can attribute one compilation's metadata or diagnostics to another.
// Keep the suite deterministic while those boundaries are being hardened.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
