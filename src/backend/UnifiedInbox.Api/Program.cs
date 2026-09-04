using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using UnifiedInbox.Api.Hubs;
using UnifiedInbox.Api.Security;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Services;
using UnifiedInbox.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Database") ?? builder.Configuration.GetConnectionString("Postgres") ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION") ?? "Host=localhost;Port=5432;Database=unified_inbox;Username=unified_inbox;Password=local_only_password";
var signingKey = builder.Configuration["Jwt:SigningKey"] ?? Environment.GetEnvironmentVariable("JWT_SIGNING_KEY") ?? (builder.Environment.IsDevelopment() ? "development-only-signing-key-change-before-production" : throw new InvalidOperationException("Jwt:SigningKey is required."));
builder.Configuration["Jwt:SigningKey"] = signingKey;

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<UnifiedInbox.Api.ProblemExceptionHandler>();
builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentTenant, CurrentRequestContext>();
builder.Services.AddScoped<TenantSessionInterceptor>();
builder.Services.AddDbContext<InboxDbContext>((services, options) => options.UseNpgsql(connectionString).AddInterceptors(services.GetRequiredService<TenantSessionInterceptor>()));
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddScoped<IAuthService, AuthenticationService>();
builder.Services.AddScoped<IInboxService, PersistentInboxService>();
builder.Services.AddScoped<IAdministrationService, AdministrationService>();
builder.Services.AddScoped<IInvitationService, InvitationService>();
builder.Services.AddScoped<IChannelService, ChannelService>();
builder.Services.AddHttpClient<IWhatsAppGraphClient, WhatsAppGraphClient>();
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
var signalR = builder.Services.AddSignalR();
var redis = builder.Configuration["Redis:Connection"] ?? Environment.GetEnvironmentVariable("REDIS_CONNECTION"); if (!string.IsNullOrWhiteSpace(redis)) signalR.AddStackExchangeRedis(redis);
var rabbit = builder.Configuration["RabbitMq:Connection"] ?? Environment.GetEnvironmentVariable("RABBITMQ_CONNECTION");
if (!string.IsNullOrWhiteSpace(rabbit)) { builder.Services.AddSingleton(new ConnectionFactory { Uri = new Uri(rabbit), AutomaticRecoveryEnabled = true }); builder.Services.AddHostedService<RealtimeSubscriber>(); }
builder.Services.AddRateLimiter(options => { options.RejectionStatusCode = 429; options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("tenant_id")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })); });
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()).WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"]).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseExceptionHandler(); app.UseRateLimiter(); app.UseCors(); app.UseAuthentication(); app.UseAuthorization();
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
RejectUnsafeProductionConfiguration(app);
if (app.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("SEED_DEVELOPMENT") == "true")
{
    await using var scope = app.Services.CreateAsyncScope();
    await DevelopmentSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<InboxDbContext>(), scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>());
}
app.Run();

static void RejectUnsafeProductionConfiguration(WebApplication app)
{
    if (!app.Environment.IsProduction()) return;
    var config = app.Configuration;
    var fake = (config["WhatsApp:UseFake"] ?? Environment.GetEnvironmentVariable("WHATSAPP_USE_FAKE"))?.ToLowerInvariant();
    if (fake == "true") throw new InvalidOperationException("Fake WhatsApp provider mode is forbidden in Production.");
    if (string.IsNullOrWhiteSpace(config["WhatsApp:AppSecret"] ?? Environment.GetEnvironmentVariable("WHATSAPP_APP_SECRET"))) throw new InvalidOperationException("WhatsApp:AppSecret is required in Production.");
    if (string.IsNullOrWhiteSpace(config["WhatsApp:VerifyToken"] ?? Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN"))) throw new InvalidOperationException("WhatsApp:VerifyToken is required in Production.");
    var masterKey = config["Credentials:MasterKey"] ?? Environment.GetEnvironmentVariable("CREDENTIAL_MASTER_KEY") ?? "";
    try { if (Convert.FromBase64String(masterKey).Length != 32) throw new InvalidOperationException("Credentials:MasterKey must decode to exactly 32 bytes in Production."); }
    catch (FormatException) { throw new InvalidOperationException("Credentials:MasterKey must be valid base64 in Production."); }
    var jwt = config["Jwt:SigningKey"] ?? "";
    if (jwt.Length < 32 || jwt == "development-only-signing-key-change-before-production") throw new InvalidOperationException("Jwt:SigningKey must be a production secret (32+ chars).");
}

public partial class Program { }
