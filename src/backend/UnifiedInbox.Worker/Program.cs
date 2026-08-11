using UnifiedInbox.Worker;
using UnifiedInbox.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<InMemoryInboxStore>();
builder.Services.AddHostedService<OutboxDispatcher>();

var host = builder.Build();
host.Run();
