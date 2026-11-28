using Xunit;

// Integration tests share process-wide state (temp SQLite hosts, background schedulers),
// so run collections sequentially for deterministic results.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
