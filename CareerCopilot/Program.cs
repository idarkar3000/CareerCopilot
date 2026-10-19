using CareerCopilot.Endpoints;
using CareerCopilot.Models;
using CareerCopilot.Repositories;
using CareerCopilot.Services;
using CareerCopilot.Workers;

var builder = WebApplication.CreateBuilder(args);

var botConfig = builder.Configuration.GetSection("BotConfig").Get<BotConfig>() ?? new BotConfig();

builder.Services.AddSingleton(botConfig);
builder.Services.AddSingleton<IJobRepository, SqliteJobRepository>();
builder.Services.AddSingleton<ICandidateProfileProvider, CandidateProfileProvider>();
builder.Services.AddSingleton<ICvSanitizer, CvSanitizer>();
builder.Services.AddHttpClient<IJobScraperService, JobScraperService>();
builder.Services.AddHttpClient<IGeminiScorerService, GeminiScorerService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(botConfig.GeminiTimeoutSeconds, 5, 90));
});
builder.Services.AddSingleton<ICvCompilerService, CvCompilerService>();
builder.Services.AddSingleton<ILocalCvBuilder, LocalCvBuilder>();

builder.Services.AddSingleton<CareerCopilotWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CareerCopilotWorker>());

builder.Services.AddSingleton<ITelegramNotifierService>(sp =>
{
    var config = sp.GetRequiredService<BotConfig>();
    var db = sp.GetRequiredService<IJobRepository>();
    var logger = sp.GetRequiredService<ILogger<TelegramNotifierService>>();
    var scorer = sp.GetRequiredService<IGeminiScorerService>();
    var cvCompiler = sp.GetRequiredService<ICvCompilerService>();

    return new TelegramNotifierService(
        config,
        db,
        logger,
        scorer,
        cvCompiler,
        () => sp.GetRequiredService<CareerCopilotWorker>());
});

var app = builder.Build();

WarnMissingCandidateSettings(app.Logger, botConfig);

app.MapHealthEndpoints();

app.Run();


static bool Missing(string? value, string envName, List<string> missing)
{
    if (!string.IsNullOrWhiteSpace(value)) return false;

    missing.Add(envName);
    return true;
}

static void WarnMissingCandidateSettings(ILogger logger, BotConfig config)
{
    var candidate = config.Candidate;

    var requiredMissing = new List<string>();
    Missing(candidate.FullName, "BotConfig__Candidate__FullName", requiredMissing);

    var optionalMissing = new List<string>();
    Missing(candidate.Headline, "BotConfig__Candidate__Headline", optionalMissing);
    Missing(candidate.Email, "BotConfig__Candidate__Email", optionalMissing);
    Missing(candidate.Phone, "BotConfig__Candidate__Phone", optionalMissing);
    Missing(candidate.City, "BotConfig__Candidate__City", optionalMissing);
    Missing(candidate.Availability, "BotConfig__Candidate__Availability", optionalMissing);
    Missing(candidate.FooterNote, "BotConfig__Candidate__FooterNote", optionalMissing);
    Missing(candidate.PdfFileNameTemplate, "BotConfig__Candidate__PdfFileNameTemplate", optionalMissing);
    Missing(candidate.OutputDir, "BotConfig__Candidate__OutputDir", optionalMissing);

    var links = candidate.Links ?? new List<ContactLink>();
    for (var i = 0; i < links.Count; i++)
    {
        Missing(links[i].Label, $"BotConfig__Candidate__Links__{i}__Label", optionalMissing);
        Missing(links[i].Url, $"BotConfig__Candidate__Links__{i}__Url", optionalMissing);
    }

    if (links.Count == 0)
    {
        logger.LogInformation(
            "No hay enlaces en BotConfig__Candidate__Links__*: la cabecera saldrá sin GitHub ni LinkedIn. Se pueden añadir con Links__0__Label y Links__0__Url.");
    }

    if (requiredMissing.Count > 0)
    {
        logger.LogError(
            "Faltan datos obligatorios del candidato: {Vars}. Sin ellos la cabecera del CV va en blanco y el PDF se nombra solo con el puesto. Créalas en el panel de Render.",
            string.Join(", ", requiredMissing));
    }

    if (optionalMissing.Count > 0)
    {
        logger.LogWarning(
            "Datos del candidato sin rellenar (la cabecera saldrá incompleta): {Vars}",
            string.Join(", ", optionalMissing));
    }
}
