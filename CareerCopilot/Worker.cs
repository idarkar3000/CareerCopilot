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

    // Desplazamiento para rotar qué términos de búsqueda recurrente se escanean en LinkedIn
    private int _linkedInRotationOffset = 0;

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

    /// <summary>
    /// Ciclo completo: todas las fuentes configuradas.
    /// </summary>
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

        // 4. InfoJobs API
        var infoJobsQueries = new[] { "c# junior", ".net junior", "programador .net trainee" };
        var infoJobsTotal = 0;
        foreach (var ijq in infoJobsQueries)
        {
            if (ct.IsCancellationRequested) break;
            var ijOffers = await _scraper.FetchInfoJobsJobsAsync(ijq, ct);
            infoJobsTotal += ijOffers.Count;
            allOffers.AddRange(ijOffers);
            await Task.Delay(1000, ct);
        }
        _logger.LogInformation(">>> [InfoJobs API] Obtenidas: {Count} ofertas", infoJobsTotal);

        // 5. Feeds RSS/Atom genéricos
        if (_config.Feeds is { Count: > 0 })
        {
            var feedOffers = await _scraper.FetchGenericFeedsAsync(ct);
            allOffers.AddRange(feedOffers);
            _logger.LogInformation(">>> [Feeds RSS] Obtenidas: {Count} ofertas", feedOffers.Count);
        }

        // 6. LinkedIn — rotando el subconjunto de términos activos
        var allQueries = _db.GetSearchQueries();
        List<string> activeQueries;
        if (allQueries.Count <= 4)
        {
            activeQueries = allQueries;
        }
        else
        {
            activeQueries = allQueries.Skip(_linkedInRotationOffset % allQueries.Count).Take(4).ToList();
            if (activeQueries.Count < 4)
            {
                activeQueries.AddRange(allQueries.Take(4 - activeQueries.Count));
            }
            _linkedInRotationOffset = (_linkedInRotationOffset + 4) % allQueries.Count;
        }

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

        foreach (var offer in uniqueOffers)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessOfferAsync(offer, ct);
        }
    }

    /// <summary>
    /// Escaneo puntual para el comando de Telegram /scan &lt;término&gt;: solo consulta las
    /// fuentes que aceptan un término de búsqueda (Tecnoempleo, LinkedIn, Adzuna, InfoJobs).
    /// No se guarda en SearchQueries, así que no pasa a formar parte del rastreo recurrente.
    /// Remotive y los feeds RSS no aceptan término de búsqueda, así que se omiten aquí
    /// (ya se cubren en el ciclo completo).
    /// </summary>
    public async Task RunAdHocScanAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogWarning("RunAdHocScanAsync llamado sin término de búsqueda; se ignora.");
            return;
        }

        _logger.LogInformation("=== Escaneo puntual (/scan) para: '{Query}' ===", query);

        var allOffers = new List<JobOffer>();

        var tecnoOffers = await _scraper.FetchTecnoEmpleoJobsAsync(query, ct);
        allOffers.AddRange(tecnoOffers);
        _logger.LogInformation(">>> [Tecnoempleo /scan] '{Query}': {Count} ofertas", query, tecnoOffers.Count);
        await Task.Delay(1000, ct);

        var liOffers = await _scraper.FetchLinkedInJobsAsync(query, ct);
        allOffers.AddRange(liOffers);
        _logger.LogInformation(">>> [LinkedIn /scan] '{Query}': {Count} ofertas", query, liOffers.Count);
        await Task.Delay(1200, ct);

        var adzOffers = await _scraper.FetchAdzunaJobsAsync(query, ct);
        allOffers.AddRange(adzOffers);
        _logger.LogInformation(">>> [Adzuna /scan] '{Query}': {Count} ofertas", query, adzOffers.Count);
        await Task.Delay(1000, ct);

        var ijOffers = await _scraper.FetchInfoJobsJobsAsync(query, ct);
        allOffers.AddRange(ijOffers);
        _logger.LogInformation(">>> [InfoJobs /scan] '{Query}': {Count} ofertas", query, ijOffers.Count);

        var uniqueOffers = allOffers.GroupBy(o => o.Id).Select(g => g.First()).ToList();
        _logger.LogInformation(">>> [/scan '{Query}'] Total ofertas únicas: {Count}", query, uniqueOffers.Count);

        foreach (var offer in uniqueOffers)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessOfferAsync(offer, ct);
        }

        _logger.LogInformation("=== Fin del escaneo puntual (/scan) para: '{Query}' ===", query);
    }

    /// <summary>
    /// Lógica compartida por RunPipelineAsync y RunAdHocScanAsync: dedup, filtro local,
    /// evaluación con Gemini, compilación del CV y notificación si supera el umbral.
    /// </summary>
    private async Task ProcessOfferAsync(JobOffer offer, CancellationToken ct)
    {
        if (_db.HasBeenProcessed(offer.Id))
        {
            return;
        }

        if (!PassesLocalFilter(offer))
        {
            _logger.LogInformation("Descartada por filtro local: '{Title}'", offer.Title);
            _db.MarkAsProcessed(offer.Id, offer.Title, offer.Company, 0);
            return;
        }

        _logger.LogInformation("-> Evaluando con Gemini: '{Title}' en {Company}...", offer.Title, offer.Company);
        var eval = await _scorer.EvaluateAsync(offer, ct);

        if (eval == null)
        {
            // A propósito NO se marca como procesada: un fallo de Gemini no significa que la
            // oferta no encaje, solo que no se ha podido evaluar todavía. Se reintentará en el
            // próximo ciclo (o la próxima vez que se dispare /run o /scan sobre ella).
            _logger.LogWarning(
                "Gemini no pudo evaluar '{Title}' (respuesta vacía o no disponible); se reintentará más adelante.",
                offer.Title);
            return;
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

    private bool PassesLocalFilter(JobOffer offer)
    {
        var text = $"{offer.Title} {offer.Description}".ToLowerInvariant();

        if (_config.RequiredKeywords != null && _config.RequiredKeywords.Any())
        {
            var matchesTech = _config.RequiredKeywords.Any(kw => text.Contains(kw.ToLowerInvariant()));
            if (!matchesTech) return false;
        }

        var titleLower = offer.Title.ToLowerInvariant();
        if (_config.ExcludedKeywords != null && _config.ExcludedKeywords.Any())
        {
            var isExcluded = _config.ExcludedKeywords.Any(kw => titleLower.Contains(kw.ToLowerInvariant()));
            if (isExcluded) return false;
        }

        // --- (Remoto España/Global O Presencial/Híbrido Madrid) ---
        var isRemote = offer.IsRemote
            || text.Contains("remoto")
            || text.Contains("teletrabajo")
            || text.Contains("remote");

        var isMadrid = offer.Province.Contains("madrid", StringComparison.OrdinalIgnoreCase)
            || offer.City.Contains("madrid", StringComparison.OrdinalIgnoreCase)
            || text.Contains("madrid");

        if (!isRemote && !isMadrid)
        {
            return false;
        }

        return true;
    }

    private static bool PassesLocationFilter(JobOffer offer, string combinedText)
    {
        // A. ¿Es remoto? 
        var isRemote = offer.IsRemote
            || combinedText.Contains("remoto")
            || combinedText.Contains("teletrabajo")
            || combinedText.Contains("remote")
            || combinedText.Contains("100% remoto")
            || combinedText.Contains("full remote");

        if (isRemote)
        {
            return true;
        }

        // B. Si no es remoto, debe ser en Madrid (provincia, ciudad o mención en texto)
        var isMadrid = offer.Province.Contains("Madrid", StringComparison.OrdinalIgnoreCase)
            || offer.City.Contains("Madrid", StringComparison.OrdinalIgnoreCase)
            || combinedText.Contains("madrid");

        return isMadrid;
    }
}