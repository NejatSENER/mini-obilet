using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.EntityFrameworkCore;
using MiniObilet.Api;
using MiniObilet.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// Key Vault via the app's Managed Identity
if (builder.Configuration["KeyVault:Uri"] is { Length: > 0 } kvUri)
    builder.Configuration.AddAzureKeyVault(new Uri(kvUri), new DefaultAzureCredential());

// Application Insights
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

builder.Services.AddDbContext<AppDbContext>((sp, o) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var cs = config.GetConnectionString("Db") ?? throw new InvalidOperationException("ConnectionStrings:Db is missing");
    if (GetDbProvider(config) == "SqlServer")
        o.UseSqlServer(cs, sql => sql.EnableRetryOnFailure());
    else
        o.UseSqlite(cs);
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

await InitializeDatabaseAsync(app);

app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/api/v1/status"));
app.MapGet("/api/v1/status", (IConfiguration config, IHostEnvironment env) => new
{
    app = "Mini Obilet API",
    environment = env.EnvironmentName,
    database = GetDbProvider(config),
    keyVault = !string.IsNullOrEmpty(config["KeyVault:Uri"]),
    paymentKeyLoaded = !string.IsNullOrEmpty(config["PaymentProvider:ApiKey"]),
    monitoring = !string.IsNullOrEmpty(config["APPLICATIONINSIGHTS_CONNECTION_STRING"])
});
app.MapMiniObiletApi();

app.Run();

static string GetDbProvider(IConfiguration config) => config["Db:Provider"] ?? "Sqlite";

static async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    // SQL Server: migrations on startup (single instance). SQLite (local, tests): create schema directly.
    if (GetDbProvider(config) == "SqlServer")
        await db.Database.MigrateAsync();
    else
        await db.Database.EnsureCreatedAsync();

    await SeedData.SeedAsync(db);
}

public partial class Program;
