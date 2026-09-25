using CareerCopilot;
using CareerCopilot.Models;
using CareerCopilot.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuración leída de appsettings.json y Variables de Entorno (Render)
var botConfig = builder.Configuration.GetSection("BotConfig").Get<BotConfig>() ?? new BotConfig();

// Inyección de servicios en el contenedor de dependencias
builder.Services.AddSingleton(botConfig);
builder.Services.AddSingleton<JobDatabase>();
builder.Services.AddHttpClient<JobScraperService>();
builder.Services.AddHttpClient<GeminiScorerService>();
builder.Services.AddSingleton<CvCompilerService>();

// Registramos TelegramNotifierService como Singleton para que Worker pueda usarlo
builder.Services.AddSingleton<TelegramNotifierService>();

// Worker periódico en segundo plano
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

// Iniciar el listener de Telegram Polling para comandos
var telegramService = app.Services.GetRequiredService<TelegramNotifierService>();
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
telegramService.StartReceiving(lifetime.ApplicationStopping);

app.Run();