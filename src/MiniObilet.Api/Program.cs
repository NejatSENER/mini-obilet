using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.EntityFrameworkCore;
using MiniObilet.Api;
using MiniObilet.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// Sırlar: Azure'da Key Vault'tan, uygulamanın Managed Identity'siyle (config'te şifre yok)
if (builder.Configuration["KeyVault:Uri"] is { Length: > 0 } kvUri)
    builder.Configuration.AddAzureKeyVault(new Uri(kvUri), new DefaultAzureCredential());

// İzleme: Application Insights (bağlantı dizesi App Service ayarından gelir)
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

builder.Services.AddDbContext<AppDbContext>((sp, o) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var cs = config.GetConnectionString("Db") ?? throw new InvalidOperationException("ConnectionStrings:Db eksik");
    if (VeritabaniSaglayici(config) == "SqlServer")
        o.UseSqlServer(cs, sql => sql.EnableRetryOnFailure());   // Azure SQL'de geçici hatalara karşı
    else
        o.UseSqlite(cs);
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("veritabani");

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

await VeritabaniHazirlaAsync(app);

app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/api/v1/durum"));
app.MapGet("/api/v1/durum", (IConfiguration config, IHostEnvironment env) => new
{
    uygulama = "Mini Obilet API",
    ortam = env.EnvironmentName,
    veritabani = VeritabaniSaglayici(config),
    keyVault = !string.IsNullOrEmpty(config["KeyVault:Uri"]),
    odemeAnahtariYuklendi = !string.IsNullOrEmpty(config["OdemeSaglayici:ApiKey"]),   // değeri asla dönmez
    izleme = !string.IsNullOrEmpty(config["APPLICATIONINSIGHTS_CONNECTION_STRING"])
});
app.MapMiniObilet();

app.Run();

static string VeritabaniSaglayici(IConfiguration config) => config["Db:Provider"] ?? "Sqlite";

static async Task VeritabaniHazirlaAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    // SQL Server: migration'lar (tek instance olduğu için açılışta). SQLite (lokal ve test): şemayı doğrudan oluştur.
    if (VeritabaniSaglayici(config) == "SqlServer")
        await db.Database.MigrateAsync();
    else
        await db.Database.EnsureCreatedAsync();

    await SeedData.YukleAsync(db);
}

public partial class Program;
