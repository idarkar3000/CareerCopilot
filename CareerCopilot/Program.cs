using CareerCopilot;
using CareerCopilot.Models;
using CareerCopilot.Services;

var builder = WebApplication.CreateBuilder(args);

var botConfig = builder.Configuration.GetSection("BotConfig").Get<BotConfig>() ?? new BotConfig();

builder.Services.AddSingleton(botConfig);
builder.Services.AddSingleton<JobDatabase>();
builder.Services.AddSingleton<CandidateProfileProvider>();
builder.Services.AddSingleton<CvSanitizer>();
builder.Services.AddHttpClient<JobScraperService>();
builder.Services.AddHttpClient<GeminiScorerService>(client =>
{
    // Sin esto se hereda el valor por defecto de HttpClient, que son 100s por llamada
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(botConfig.GeminiTimeoutSeconds, 5, 90));
});
builder.Services.AddSingleton<CvCompilerService>();
builder.Services.AddSingleton<LocalCvBuilder>();

// Worker se registra dos veces a propósito: como singleton (lo resuelve TelegramNotifierService
// para /run, /scan y /cv) y como IHostedService devolviendo esa MISMA instancia, para que el
// ciclo automático y los comandos compartan estado en vez de ser dos Workers distintos.
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());

// TelegramNotifierService necesita lanzar el pipeline y Worker necesita a TelegramNotifierService
// para notificar. IManualActions se resuelve de forma perezosa cuando se usa, no al montar el DI.
builder.Services.AddSingleton<TelegramNotifierService>(sp =>
{
    var config = sp.GetRequiredService<BotConfig>();
    var db = sp.GetRequiredService<JobDatabase>();
    var logger = sp.GetRequiredService<ILogger<TelegramNotifierService>>();
    var scorer = sp.GetRequiredService<GeminiScorerService>();
    var cvCompiler = sp.GetRequiredService<CvCompilerService>();

    return new TelegramNotifierService(
        config,
        db,
        logger,
        scorer,
        cvCompiler,
        () => sp.GetRequiredService<Worker>());
});

var app = builder.Build();

app.MapMethods("/", new[] { "GET", "HEAD" }, () => Results.Ok(new
{
    status = "Healthy",
    service = "CareerCopilot",
    timestamp = DateTime.UtcNow
}));

app.MapMethods("/health", new[] { "GET", "HEAD" }, () => Results.Ok("OK"));

app.Run();