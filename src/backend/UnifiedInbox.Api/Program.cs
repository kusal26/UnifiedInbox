using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using UnifiedInbox.Api.Hubs;
using UnifiedInbox.Api.Security;
using UnifiedInbox.Application;
using UnifiedInbox.Application.Tenancy;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;
using UnifiedInbox.Infrastructure.Configuration;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Services;
using UnifiedInbox.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Database") ?? builder.Configuration.GetConnectionString("Postgres") ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION") ?? "Host=localhost;Port=5432;Database=unified_inbox;Username=unified_inbox;Password=local_only_password";
var signingKey = builder.Configuration["Jwt:SigningKey"] ?? Environment.GetEnvironmentVariable("JWT_SIGNING_KEY") ?? (builder.Environment.IsDevelopment() ? "development-only-signing-key-change-before-production" : throw new InvalidOperationException("Jwt:SigningKey is required."));
builder.Configuration["Jwt:SigningKey"] = signingKey;

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<UnifiedInbox.Api.ProblemExceptionHandler>();
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = context =>
{
    // Automatic 400s for malformed/missing request bodies are RFC 7807 errors too: give them a
    // stable code and trace id so clients never see a body without correlation.
    var problem = new Microsoft.AspNetCore.Mvc.ValidationProblemDetails(context.ModelState)
    {
        Status = StatusCodes.Status400BadRequest,
        Title = "Invalid request",
        Type = "https://unifiedinbox.app/problems/invalid_request",
        Extensions = { ["traceId"] = context.HttpContext.TraceIdentifier, ["code"] = "invalid_request" },
    };
    return new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(problem);
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentTenant, CurrentRequestContext>();
builder.Services.AddDbContext<InboxDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<ITenantExecutionScope, TenantExecutionScope>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddScoped<IAuthService, AuthenticationService>();
builder.Services.AddScoped<IInboxService, PersistentInboxService>();
builder.Services.AddScoped<IAdministrationService, AdministrationService>();
builder.Services.AddScoped<IInvitationService, InvitationService>();
builder.Services.AddScoped<IChannelService, ChannelService>();
builder.Services.AddHttpClient<IWhatsAppGraphClient, WhatsAppGraphClient>();
builder.Services.AddScoped<IWhatsAppTemplateService, WhatsAppTemplateService>();
builder.Services.AddScoped<IWebhookService, WebhookService>();
builder.Services.AddScoped<IAttachmentService, AttachmentService>();
builder.Services.AddSingleton<IObjectStorage, MinioObjectStorage>();
builder.Services.AddSingleton<IAttachmentScanner, ClamAvScanner>();
builder.Services.AddSingleton<IMailSender, SmtpMailSender>();
builder.Services.AddSingleton<WhatsAppSignatureValidator>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"], ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"], ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), ClockSkew = TimeSpan.FromSeconds(30) };
    options.Events = new JwtBearerEvents { OnMessageReceived = context => { if (context.Request.Path.StartsWithSegments("/hubs/inbox") && context.Request.Query.TryGetValue("access_token", out var token)) context.Token = token; return Task.CompletedTask; } };
});
builder.Services.AddAuthorizationBuilder().AddPolicy("Admin", policy => policy.RequireRole(nameof(UserRole.Owner), nameof(UserRole.Admin))).AddPolicy("Owner", policy => policy.RequireRole(nameof(UserRole.Owner)));
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuthorizationAuditHandler>();
var signalR = builder.Services.AddSignalR();
var redis = builder.Configuration["Redis:Connection"] ?? Environment.GetEnvironmentVariable("REDIS_CONNECTION"); if (!string.IsNullOrWhiteSpace(redis)) signalR.AddStackExchangeRedis(redis);
var rabbit = builder.Configuration["RabbitMq:Connection"] ?? Environment.GetEnvironmentVariable("RABBITMQ_CONNECTION");
if (!string.IsNullOrWhiteSpace(rabbit)) { builder.Services.AddSingleton(new ConnectionFactory { Uri = new Uri(rabbit), AutomaticRecoveryEnabled = true }); builder.Services.AddHostedService<RealtimeSubscriber>(); }
builder.Services.AddRateLimiter(options => { options.RejectionStatusCode = 429; options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("tenant_id")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })); });
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()).WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"]).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseExceptionHandler(); app.UseRateLimiter(); app.UseCors(); app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (Guid.TryParse(context.User.FindFirst("tenant_id")?.Value, out var tenantId))
    {
        var tenantScope = context.RequestServices.GetRequiredService<ITenantExecutionScope>();
        await tenantScope.RunAsync(tenantId, _ => next(context), context.RequestAborted);
        return;
    }
    await next(context);
});
app.UseAuthorization();
app.MapControllers(); app.MapHub<InboxHub>("/hubs/inbox", options => options.CloseOnAuthenticationExpiration = true);
// Migrations run only in the dedicated one-shot migrator container
// (docker compose service `migrator`) or with --migrate / RUN_MIGRATIONS=true.
// Ordinary API startup never migrates.
if (args.Contains("--migrate") || Environment.GetEnvironmentVariable("RUN_MIGRATIONS") == "true")
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<InboxDbContext>();
    await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("SEED_DEVELOPMENT") == "true")
        await DevelopmentSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>());
    return;
}
ProductionGuard.Validate(app.Configuration, app.Environment.IsProduction());
if (app.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("SEED_DEVELOPMENT") == "true")
{
    await using var scope = app.Services.CreateAsyncScope();
    await DevelopmentSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<InboxDbContext>(), scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>());
}
app.Run();

public partial class Program { }
