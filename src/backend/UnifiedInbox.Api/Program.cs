using UnifiedInbox.Infrastructure;
using UnifiedInbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<InMemoryInboxStore>();
builder.Services.AddSingleton<UnifiedInbox.Application.IInboxStore>(x => x.GetRequiredService<InMemoryInboxStore>());
builder.Services.AddControllers();
builder.Services.AddSignalR();
var postgres = builder.Configuration.GetConnectionString("Postgres") ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
if (!string.IsNullOrWhiteSpace(postgres)) builder.Services.AddDbContext<InboxDbContext>(options => options.UseNpgsql(postgres));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.UseCors();
app.Use(async (context, next) =>
{
    var store = context.RequestServices.GetRequiredService<InMemoryInboxStore>();
    var header = context.Request.Headers.Authorization.ToString();
    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && store.TrySession(header[7..], out var tenantId, out var userId))
    { context.Items["tenantId"] = tenantId; context.Items["userId"] = userId; }
    await next();
});
app.MapControllers();
app.MapHub<UnifiedInbox.Api.Hubs.InboxHub>("/hubs/inbox");
app.Run();

public partial class Program { }
