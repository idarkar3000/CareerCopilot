using CareerCopilot;
using CareerCopilot.Models;
using CareerCopilot.Services;

var builder = WebApplication.CreateBuilder(args);

var botConfig = builder.Configuration.GetSection("BotConfig").Get<BotConfig>() ?? new BotConfig();

builder.Services.AddSingleton(botConfig);
builder.Services.AddSingleton<JobDatabase>();
builder.Services.AddHttpClient<JobScraperService>();
builder.Services.AddHttpClient<GeminiScorerService>();
builder.Services.AddSingleton<CvCompilerService>();

// Worker se registra dos veces a propósito:
//  1) Como singleton de su propio tipo, para poder inyectarlo/resolverlo directamente
//     (lo necesita la fábrica de TelegramNotifierService de abajo para /run y /scan).
//  2) Como IHostedService, devolviendo esa MISMA instancia (no una nueva) para que el bucle
//     automático y los comandos manuales de Telegram compartan estado (p.ej. la rotación de
//     términos de LinkedIn) en vez de operar sobre dos Workers distintos.
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());

// TelegramNotifierService necesita poder disparar Worker.RunPipelineAsync (/run) o
// Worker.RunAdHocScanAsync (/scan), pero Worker también depende de TelegramNotifierService
// para notificar. Para romper el ciclo, el delegado resuelve Worker de forma perezosa desde
// el IServiceProvider cuando se invoca (en tiempo de ejecución), no al construir el grafo de DI.
builder.Services.AddSingleton<TelegramNotifierService>(sp =>
{
    var config = sp.GetRequiredService<BotConfig>();
    var db = sp.GetRequiredService<JobDatabase>();
    var logger = sp.GetRequiredService<ILogger<TelegramNotifierService>>();
    var scorer = sp.GetRequiredService<GeminiScorerService>();
    var cvCompiler = sp.GetRequiredService<CvCompilerService>();
    var appLifetime = sp.GetRequiredService<IHostApplicationLifetime>();

    Func<string?, Task> triggerScan = async query =>
    {
        var worker = sp.GetRequiredService<Worker>();

        // Un escaneo (completo o puntual) puede tardar varios minutos por el throttling entre peticiones a fuentes externas, se le da un poco de margen
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, appLifetime.ApplicationStopping);

        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                await worker.RunPipelineAsync(linkedCts.Token);
            }
            else
            {
                await worker.RunAdHocScanAsync(query, linkedCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("El escaneo disparado manualmente (/run o /scan) se canceló por timeout o apagado de la app.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error ejecutando el escaneo disparado manualmente (/run o /scan).");
        }
    };

    return new TelegramNotifierService(config, db, logger, scorer, cvCompiler, triggerScan);
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