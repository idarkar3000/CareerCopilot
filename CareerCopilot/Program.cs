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
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(botConfig.GeminiTimeoutSeconds, 5, 90));
});
builder.Services.AddSingleton<CvCompilerService>();
builder.Services.AddSingleton<LocalCvBuilder>();

builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());

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

WarnMissingCandidateSettings(app.Logger, botConfig);

app.MapMethods("/", new[] { "GET", "HEAD" }, () => Results.Ok(new
{
    status = "Healthy",
    service = "CareerCopilot",
    timestamp = DateTime.UtcNow
}));

app.MapMethods("/health", new[] { "GET", "HEAD" }, () => Results.Ok("OK"));

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
            "No hay enlaces en BotConfig__Candidate__Links__*: la cabecera saldrÃ¡ sin GitHub ni LinkedIn. Se pueden aÃ±adir con Links__0__Label y Links__0__Url.");
    }

    if (requiredMissing.Count > 0)
    {
        logger.LogError(
            "Faltan datos obligatorios del candidato: {Vars}. Sin ellos la cabecera del CV va en blanco y el PDF se nombra solo con el puesto. CrÃ©alas en el panel de Render.",
            string.Join(", ", requiredMissing));
    }

    if (optionalMissing.Count > 0)
    {
        logger.LogWarning(
            "Datos del candidato sin rellenar (la cabecera saldrÃ¡ incompleta): {Vars}",
            string.Join(", ", optionalMissing));
    }
}
