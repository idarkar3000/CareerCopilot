using CareerCopilot;
using CareerCopilot.Models;
using CareerCopilot.Services;

var builder = WebApplication.CreateBuilder(args);

var botConfig = builder.Configuration.GetSection("BotConfig").Get<BotConfig>()
    ?? throw new InvalidOperationException("Falta la sección 'BotConfig' en appsettings.json.");

builder.Services.AddSingleton(botConfig);
builder.Services.AddSingleton<JobDatabase>();
builder.Services.AddHttpClient<JobScraperService>();
builder.Services.AddHttpClient<GeminiScorerService>();
builder.Services.AddSingleton<CvCompilerService>();
builder.Services.AddSingleton<TelegramNotifierService>();

builder.Services.AddHostedService<Worker>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { status = "alive", timestamp = DateTime.UtcNow }));
app.MapGet("/health", () => Results.Ok("OK"));

app.Run();