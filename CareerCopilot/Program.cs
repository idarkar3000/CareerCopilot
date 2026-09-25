using CareerCopilot;
using CareerCopilot.Models;
using CareerCopilot.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuración leída de appsettings.json y Variables de Entorno (Render)
var botConfig = builder.Configuration.GetSection("BotConfig").Get<BotConfig>() ?? new BotConfig();

// Inyección de dependencias
builder.Services.AddSingleton(botConfig);
builder.Services.AddSingleton<JobDatabase>();
builder.Services.AddHttpClient<JobScraperService>();
builder.Services.AddHttpClient<GeminiScorerService>();
builder.Services.AddSingleton<CvCompilerService>();

// Registramos TelegramNotifierService como Singleton (lo consume Worker)
builder.Services.AddSingleton<TelegramNotifierService>();

// Worker en segundo plano (se encarga de arrancar el pipeline y el listener de Telegram)
builder.Services.AddHostedService<Worker>();

var app = builder.Build();

// Endpoints compatibles con GET y HEAD para UptimeRobot (Keep-Alive gratuito)
app.MapMethods("/", new[] { "GET", "HEAD" }, () => Results.Ok(new
{
    status = "Healthy",
    service = "CareerCopilot",
    timestamp = DateTime.UtcNow
}));

app.MapMethods("/health", new[] { "GET", "HEAD" }, () => Results.Ok("OK"));

app.Run();