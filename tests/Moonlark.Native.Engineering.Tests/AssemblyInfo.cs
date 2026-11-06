using Xunit;

// These collections create executable stand-ins while other collections launch processes.
// Linux can reject that overlap with ETXTBSY, even with distinct per-test directories.
// Serialize fixture lifetimes; keep every assertion and the separate native concurrency suite.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
