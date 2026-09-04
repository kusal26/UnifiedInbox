using UnifiedInbox.Worker;

var host = WorkerHost.CreateHost(args);
// The worker never migrates; the one-shot migrator container owns schema changes.
await host.RunAsync();
