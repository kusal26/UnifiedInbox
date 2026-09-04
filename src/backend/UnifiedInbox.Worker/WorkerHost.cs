using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RabbitMQ.Client;
using UnifiedInbox.Application;
using UnifiedInbox.Application.Tenancy;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Services;
using UnifiedInbox.Infrastructure.Storage;

namespace UnifiedInbox.Worker;

/// <summary>
/// Composes the worker host. Kept as a separate seam from the top-level Program so tests
/// can build the real worker (with its actual hosted services and consumers) against
/// test infrastructure.
/// </summary>
public static class WorkerHost
{
    public static IHost CreateHost(string[] args, Action<HostApplicationBuilder>? configure = null, Action<IServiceCollection>? services = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        configure?.Invoke(builder);

        var database = builder.Configuration.GetConnectionString("Database") ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION") ?? "Host=localhost;Port=5432;Database=unified_inbox;Username=unified_inbox;Password=local_only_password";
        var rabbit = builder.Configuration["RabbitMq:Connection"] ?? Environment.GetEnvironmentVariable("RABBITMQ_CONNECTION") ?? "amqp://guest:guest@localhost:5672/";
        var tenantHeaderKey = builder.Configuration["Messaging:TenantHeaderSigningKey"] ?? Environment.GetEnvironmentVariable("TENANT_MESSAGE_SIGNING_KEY");
        if (string.IsNullOrWhiteSpace(tenantHeaderKey))
        {
            if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("TENANT_MESSAGE_SIGNING_KEY is required outside Development.");
            tenantHeaderKey = "development-only-tenant-header-key";
        }

        builder.Services.AddSingleton<ICurrentTenant, WorkerTenantContext>();
        builder.Services.AddDbContext<InboxDbContext>(options => options.UseNpgsql(database));
        builder.Services.AddScoped<ITenantExecutionScope, TenantExecutionScope>();
        builder.Services.AddSingleton<IObjectStorage, MinioObjectStorage>();
        builder.Services.AddSingleton<IAttachmentScanner, ClamAvScanner>();
        builder.Services.AddScoped<IAttachmentService, AttachmentService>();
        builder.Services.AddHttpClient<WhatsAppMessageSender>();
        builder.Services.AddScoped<MessageProcessor>();
        builder.Services.AddSingleton(new ConnectionFactory { Uri = new Uri(rabbit), AutomaticRecoveryEnabled = true });
        builder.Services.AddSingleton(new TenantHeaderSigner(tenantHeaderKey));
        builder.Services.AddHostedService<OutboxDispatcher>();
        builder.Services.AddHostedService<MessagingConsumer>();
        builder.Services.AddHostedService<RetrySweeper>();
        builder.Services.AddHostedService<ChannelHealthMonitor>();
        builder.Services.AddHostedService<AttachmentCleanupWorker>();
        services?.Invoke(builder.Services);
        return builder.Build();
    }
}

/// <summary>Worker processes have no ambient authenticated actor; tenant context is always explicit via execution scopes.</summary>
public sealed class WorkerTenantContext : ICurrentTenant
{
    public Guid? TenantId => null;
    public Guid? UserId => null;
    public Domain.UserRole? Role => null;
}
