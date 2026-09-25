using CareerCopilot.Models;
using CareerCopilot.Services;

namespace CareerCopilot;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly BotConfig _config;
    private readonly JobDatabase _db;
    private readonly JobScraperService _scraper;
    private readonly GeminiScorerService _scorer;
    private readonly CvCompilerService _cvCompiler;
    private readonly TelegramNotifierService _notifier;

    public Worker(
        ILogger<Worker> logger,
        BotConfig config,
        JobDatabase db,
        JobScraperService scraper,
        GeminiScorerService scorer,
        CvCompilerService cvCompiler,
        TelegramNotifierService notifier)
    {
        _logger = logger;
        _config = config;
        _db = db;
        _scraper = scraper;
        _scorer = scorer;
        _cvCompiler = cvCompiler;
        _notifier = notifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CareerCopilot Worker iniciado.");

        _db.SeedDefaultQueries(_config.SearchQueries);

        // StartReceiving -> StartReceivingAsync: ahora borra un posible webhook residual
        // antes de arrancar el long polling. No bloquea el resto del Worker: internamente
        // arranca su propio bucle de recepción en segundo plano.
        await _notifier.StartReceivingAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPipelineAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error crítico durante el ciclo de ejecución del pipeline.");
            }

            _logger.LogInformation("Esperando {Minutes} minutos hasta la siguiente revisión programada...", _config.CheckIntervalMinutes);
            await Task.Delay(TimeSpan.FromMinutes(_config.CheckIntervalMinutes), stoppingToken);
        }
    }

    public async Task RunPipelineAsync(CancellationToken ct)
    {
        var allOffers = new List<JobOffer>();

        // 1. Tecnoempleo (HTML Parser)
        var tecnoQueries = new[] { "c#", ".net" };
        var tecnoTotal = 0;
        foreach (var tq in tecnoQueries)
        {
            if (ct.IsCancellationRequested) break;
            var tecnoOffers = await _scraper.FetchTecnoEmpleoJobsAsync(tq, ct);
            tecnoTotal += tecnoOffers.Count;
            allOffers.AddRange(tecnoOffers);
            await Task.Delay(1000, ct);
        }
        _logger.LogInformation(">>> [Tecnoempleo] Obtenidas: {Count} ofertas", tecnoTotal);

        // 2. Adzuna API
        // OJO: el tier gratuito de Adzuna es limitado (varía según el plan que te haya
        // asignado el registro, del orden de unos pocos miles de llamadas/mes). Con 2
        // queries por ciclo y un CheckIntervalMinutes agresivo (p.ej. cada 60 min = 48
        // llamadas/día) puedes acercarte al límite mensual. Si empiezas a ver 429 aquí,
        // sube CheckIntervalMinutes o reduce las queries de Adzuna antes de pedir un plan
        // de pago.
        var adzunaQueries = new[] { "c# junior", ".net junior" };
        var adzunaTotal = 0;
        foreach (var aq in adzunaQueries)
        {
            if (ct.IsCancellationRequested) break;
            var adzOffers = await _scraper.FetchAdzunaJobsAsync(aq, ct);
            adzunaTotal += adzOffers.Count;
            allOffers.AddRange(adzOffers);
            await Task.Delay(1000, ct);
        }
        _logger.LogInformation(">>> [Adzuna API] Obtenidas: {Count} ofertas", adzunaTotal);

        // 3. Remotive API
        var remoteOffers = await _scraper.FetchRemotiveJobsAsync(ct);
        allOffers.AddRange(remoteOffers);
        _logger.LogInformation(">>> [Remotive API] Obtenidas: {Count} ofertas", remoteOffers.Count);
        await Task.Delay(1000, ct);

        // 4. LinkedIn
        var activeQueries = _db.GetSearchQueries().Take(4).ToList();
        var linkedInTotal = 0;
        foreach (var query in activeQueries)
        {
            if (ct.IsCancellationRequested) break;
            var liOffers = await _scraper.FetchLinkedInJobsAsync(query, ct);
            linkedInTotal += liOffers.Count;
            allOffers.AddRange(liOffers);
            _logger.LogInformation(">>> [LinkedIn] '{Query}': {Count} ofertas", query, liOffers.Count);
            await Task.Delay(1200, ct);
        }
        _logger.LogInformation(">>> [LinkedIn Total] Obtenidas: {Count} ofertas", linkedInTotal);

        var uniqueOffers = allOffers.GroupBy(o => o.Id).Select(g => g.First()).ToList();
        _logger.LogInformation("================================================");
        _logger.LogInformation("TOTAL OFERTAS ÚNICAS DESCARGADAS: {Count}", uniqueOffers.Count);
        _logger.LogInformation("================================================");

        // 5. Filtrado, evaluación y compilación
        foreach (var offer in uniqueOffers)
        {
            if (ct.IsCancellationRequested) break;

            if (_db.HasBeenProcessed(offer.Id))
            {
                continue;
            }

            if (!PassesLocalFilter(offer))
            {
                _logger.LogInformation("Descartada por filtro local: '{Title}'", offer.Title);
                _db.MarkAsProcessed(offer.Id, offer.Title, offer.Company, 0);
                continue;
            }

            _logger.LogInformation("-> Evaluando con Gemini: '{Title}' en {Company}...", offer.Title, offer.Company);
            var eval = await _scorer.EvaluateAsync(offer, ct);

            if (eval == null)
            {
                // IMPORTANTE: a propósito NO se llama a _db.MarkAsProcessed aquí.
                // Un fallo de Gemini (404 de modelo, 429/503 transitorio, corte de red...)
                // no significa que la oferta no encaje: significa que no hemos podido
                // evaluarla todavía. Si la marcáramos como procesada, quedaría enterrada
                // para siempre en SQLite aunque el problema de Gemini se resuelva al
                // minuto siguiente. Al no marcarla, se reintentará en el próximo ciclo
                // (dentro de {Minutes} min).
                _logger.LogWarning(
                    "Gemini no pudo evaluar '{Title}' (respuesta vacía o no disponible); se reintentará en el próximo ciclo.",
                    offer.Title);
                continue;
            }

            _logger.LogInformation("Gemini score: {Score}/100", eval.Score);
            _db.MarkAsProcessed(offer.Id, offer.Title, offer.Company, eval.Score);

            if (eval.Score >= _config.MinScoreThreshold)
            {
                _logger.LogInformation("¡SUPERÓ EL UMBRAL ({Score})! Compilando PDF en Typst...", eval.Score);
                var pdfPath = await _cvCompiler.GeneratePdfAsync(offer, eval, ct);

                if (string.IsNullOrEmpty(pdfPath))
                {
                    _logger.LogError("Fallo al compilar el PDF con Typst. Comprueba si 'typst' está instalado.");
                }

                _logger.LogInformation("Enviando notificación a Telegram...");
                await _notifier.SendNotificationAsync(offer, eval, pdfPath, ct);
                _logger.LogInformation("¡Notificación enviada!");
            }

            await Task.Delay(5000, ct);
        }
    }

    private bool PassesLocalFilter(JobOffer offer)
    {
        var text = $"{offer.Title} {offer.Description}".ToLowerInvariant();

        // Si se han configurado palabras obligatorias, debe cumplir al menos una
        if (_config.RequiredKeywords != null && _config.RequiredKeywords.Any())
        {
            var matchesTech = _config.RequiredKeywords.Any(kw => text.Contains(kw.ToLowerInvariant()));
            if (!matchesTech) return false;
        }

        // Si contiene alguna de las palabras excluidas en el título, se descarta
        var titleLower = offer.Title.ToLowerInvariant();
        if (_config.ExcludedKeywords != null && _config.ExcludedKeywords.Any())
        {
            var isExcluded = _config.ExcludedKeywords.Any(kw => titleLower.Contains(kw.ToLowerInvariant()));
            if (isExcluded) return false;
        }

        return true;
    }
}