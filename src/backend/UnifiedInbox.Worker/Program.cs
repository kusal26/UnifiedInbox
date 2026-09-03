using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using UnifiedInbox.Application;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Worker;

var builder = Host.CreateApplicationBuilder(args);
var database = builder.Configuration.GetConnectionString("Database") ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION") ?? "Host=localhost;Port=5432;Database=unified_inbox;Username=unified_inbox;Password=local_only_password";
var rabbit = builder.Configuration["RabbitMq:Connection"] ?? Environment.GetEnvironmentVariable("RABBITMQ_CONNECTION") ?? "amqp://guest:guest@localhost:5672/";
builder.Services.AddSingleton<ICurrentTenant, WorkerTenantContext>();
builder.Services.AddDbContext<InboxDbContext>(options => options.UseNpgsql(database));
builder.Services.AddSingleton(new ConnectionFactory { Uri = new Uri(rabbit), AutomaticRecoveryEnabled = true });
builder.Services.AddHttpClient<WhatsAppMessageSender>();
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<MessagingConsumer>();
var host = builder.Build();
// The worker never migrates; the one-shot migrator container owns schema changes.
await host.RunAsync();

namespace UnifiedInbox.Worker { public sealed class WorkerTenantContext : ICurrentTenant { public Guid? TenantId => null; public Guid? UserId => null; public UnifiedInbox.Domain.UserRole? Role => null; } }
