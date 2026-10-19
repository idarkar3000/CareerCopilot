using CareerCopilot.Models;
using CareerCopilot.Repositories;
using CareerCopilot.Services;

namespace CareerCopilot.Workers
{
    public class CareerCopilotWorker : BackgroundService, IManualActions
    {
        private readonly ILogger<CareerCopilotWorker> _logger;
        private readonly BotConfig _config;
        private readonly IJobRepository _db;
        private readonly IJobScraperService _scraper;
        private readonly IGeminiScorerService _scorer;
        private readonly ICvCompilerService _cvCompiler;
        private readonly ILocalCvBuilder _localCv;
        private readonly ITelegramNotifierService _notifier;

        private int _linkedInRotationOffset = 0;
        private int _evaluationsThisCycle;

        private readonly Lock _pipelineLock = new();
        private bool _pipelineBusy;
        private string _pipelineOrigin = string.Empty;
        private bool _geminiFailureReported;

        public CareerCopilotWorker(
            ILogger<CareerCopilotWorker> logger,
            BotConfig config,
            IJobRepository db,
            IJobScraperService scraper,
            IGeminiScorerService scorer,
            ICvCompilerService cvCompiler,
            ILocalCvBuilder localCv,
            ITelegramNotifierService notifier)
        {
            _logger = logger;
            _config = config;
            _db = db;
            _scraper = scraper;
            _scorer = scorer;
            _cvCompiler = cvCompiler;
            _localCv = localCv;
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

        public bool IsBusy
        {
            get
            {
                lock (_pipelineLock)
                {
                    return _pipelineBusy;
                }
            }
        }

        public string BusyOrigin
        {
            get
            {
                lock (_pipelineLock)
                {
                    return string.IsNullOrEmpty(_pipelineOrigin) ? "otro escaneo" : _pipelineOrigin;
                }
            }
        }

        public async Task<bool> RunPipelineAsync(CancellationToken ct)
        {
            if (!TryBeginPipeline("ciclo programado")) return false;

            try
            {
                var allOffers = new List<JobOffer>();

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

                var remoteOffers = await _scraper.FetchRemotiveJobsAsync(ct);
                allOffers.AddRange(remoteOffers);
                _logger.LogInformation(">>> [Remotive API] Obtenidas: {Count} ofertas", remoteOffers.Count);
                await Task.Delay(1000, ct);

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

                if (_config.Feeds is { Count: > 0 })
                {
                    var feedOffers = await _scraper.FetchGenericFeedsAsync(ct);
                    allOffers.AddRange(feedOffers);
                    _logger.LogInformation(">>> [Feeds RSS] Obtenidas: {Count} ofertas", feedOffers.Count);
                }

                var allQueries = _db.GetSearchQueries();
                List<string> activeQueries;
                if (allQueries.Count <= 4)
                {
                    activeQueries = allQueries;
                }
                else
                {
                    // Rota 4 búsquedas por ciclo: así LinkedIn no recibe siempre las mismas de golpe.
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
                    await ProcessOfferSafelyAsync(offer, ct);
                }

                return true;
            }
            finally
            {
                EndPipeline();
            }
        }

        private async Task ProcessOfferSafelyAsync(JobOffer offer, CancellationToken ct)
        {
            try
            {
                await ProcessOfferAsync(offer, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Fallo al procesar '{Title}'; se sigue con el resto del ciclo.", offer.Title);
            }
        }

        public async Task<bool> RunAdHocScanAsync(string query, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                _logger.LogWarning("RunAdHocScanAsync llamado sin término de búsqueda; se ignora.");
                return false;
            }

            if (!TryBeginPipeline($"/scan '{query}'")) return false;

            try
            {
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
                    await ProcessOfferSafelyAsync(offer, ct);
                }

                _logger.LogInformation("=== Fin del escaneo puntual (/scan) para: '{Query}' ===", query);
                return true;
            }
            finally
            {
                EndPipeline();
            }
        }

        private async Task ProcessOfferAsync(JobOffer offer, CancellationToken ct)
        {
            if (_db.HasBeenProcessed(offer.Id))
            {
                return;
            }

            if (!PassesLocalFilter(offer))
            {
                _logger.LogInformation("Descartada por filtro local: '{Title}'", offer.Title);
                _db.MarkAsProcessed(offer, 0);
                return;
            }

            if (!TryTakeEvaluationSlot())
            {
                _logger.LogInformation(
                    "Tope de {Max} ofertas evaluadas por ciclo alcanzado; '{Title}' queda pendiente para el siguiente ciclo.",
                    _config.MaxOffersPerCycle, offer.Title);
                return;
            }

            _logger.LogInformation("-> Evaluando con Gemini: '{Title}' en {Company}...", offer.Title, offer.Company);
            var eval = await _scorer.EvaluateAsync(offer, ct);

            if (eval == null)
            {
                _logger.LogWarning(
                    "Gemini no pudo evaluar '{Title}' (respuesta vacía o no disponible); se reintentará más adelante.",
                    offer.Title);

                await ReportGeminiFailureAsync(offer, ct);
                return;
            }

            _logger.LogInformation("Gemini score: {Score}/100", eval.Score);
            _db.MarkAsProcessed(offer, eval.Score);

            if (eval.Score >= _config.MinScoreThreshold)
            {
                _logger.LogInformation("¡SUPERÓ EL UMBRAL ({Score})! Compilando PDF en Typst...", eval.Score);
                var pdfPath = await _cvCompiler.GeneratePdfAsync(offer, eval, ct);

                if (string.IsNullOrEmpty(pdfPath))
                {
                    _logger.LogError(
                        "Fallo al compilar el PDF con Typst para '{Title}'. Comprueba si 'typst' está instalado o si el CV no cabe en una página.",
                        offer.Title);
                }

                _logger.LogInformation("Enviando notificación a Telegram...");
                await _notifier.SendNotificationAsync(offer, eval, pdfPath, ct);
                _logger.LogInformation("¡Notificación enviada!");
            }

            await Task.Delay(5000, ct);
        }

        public async Task<CvRequestResult> RegenerateCvAsync(JobOffer offer, CancellationToken ct, bool localOnly = false)
        {
            if (!TryBeginPipeline($"/cv '{Shorten(offer.Title)}'"))
            {
                return new CvRequestResult(false, $"⏳ Hay un escaneo en marcha ({BusyOrigin}). Inténtalo de nuevo en unos minutos.");
            }

            try
            {
                EvaluationResult? eval = null;
                var fellBack = false;

                if (localOnly)
                {
                    _logger.LogInformation("CV local a petición de Telegram para '{Title}' (sin Gemini).", offer.Title);
                }
                else
                {
                    _logger.LogInformation("Regenerando CV a petición de Telegram para '{Title}'...", offer.Title);
                    eval = await _scorer.EvaluateAsync(offer, ct);

                    if (eval == null)
                    {
                        fellBack = true;
                        _logger.LogWarning("Gemini no pudo evaluar '{Title}'; se genera el CV en modo local.", offer.Title);
                    }
                }

                eval ??= _localCv.Build(offer);

                if (eval == null)
                {
                    return new CvRequestResult(
                        false,
                        "❌ No se pudo generar el CV local: el perfil no trae el bloque <code>profile</code> " +
                        "(summary, skills, education, languages).",
                        JobTitle: offer.Title, JobLink: offer.Link);
                }

                var pdfPath = await _cvCompiler.GeneratePdfAsync(offer, eval, ct);
                if (string.IsNullOrEmpty(pdfPath))
                {
                    var why = eval.IsLocal
                        ? "el CV no cabe en una sola página"
                        : $"la evaluación se hizo ({eval.Score}/100) pero el CV no se pudo compilar en una sola página";
                    return new CvRequestResult(
                        false,
                        $"⚠️ {char.ToUpperInvariant(why[0])}{why[1..]}. Revisa que <b>typst</b> esté en el PATH.",
                        Score: eval.Score, JobTitle: offer.Title, JobLink: offer.Link, IsLocal: eval.IsLocal);
                }

                var suffix = eval.SanitizerViolations.Count > 0
                    ? $"\n\n⚠️ El guardarraíl descartó {eval.SanitizerViolations.Count} tecnología(s) sin respaldo en tu perfil: <code>{Html(eval.SanitizerViolations.Distinct().Take(6))}</code>"
                    : string.Empty;

                if (eval.IsLocal) return LocalCvMessage(offer, pdfPath, fellBack, suffix);

                _logger.LogInformation("CV regenerado con score {Score}/100.", eval.Score);

                return new CvRequestResult(
                    true,
                    $"✅ CV regenerado para <b>{Html(offer.Title)}</b> ({eval.Score}/100).{suffix}",
                    pdfPath, eval.Score, offer.Title, offer.Link);
            }
            finally
            {
                EndPipeline();
            }
        }

        private CvRequestResult LocalCvMessage(JobOffer offer, string pdfPath, bool fellBack, string suffix)
        {
            var coverage = CheckKeywords(offer);
            var reason = fellBack
                ? "Gemini no tenía cuota, así que el CV se montó con el texto de tu perfil"
                : "pedido en modo local, sin llamar a Gemini";

            var keywords = coverage.Matched.Count > 0
                ? $"\n<i>Palabras clave de tus filtros que salen en la oferta:</i> <code>{Html(coverage.Matched.ToList())}</code>"
                : "\n<i>Ninguna de tus palabras clave filtradas aparece en esta oferta.</i>";

            var missing = coverage.Missing.Count > 0
                ? $"\n<i>No aparecen:</i> <code>{Html(coverage.Missing.ToList())}</code>"
                : string.Empty;

            var caution = "<i>Sin puntuación de IA: las viñetas se eligen por las palabras de la oferta, pero el texto " +
                          "no está adaptado como lo haría el modelo.</i>";

            return new CvRequestResult(
                true,
                $"✅ CV generado para <b>{Html(offer.Title)}</b>.\n<i>{reason}.</i>{keywords}{missing}\n{caution}{suffix}",
                pdfPath, Score: 0, JobTitle: offer.Title, JobLink: offer.Link,
                IsLocal: true, Coverage: coverage);
        }

        public KeywordCoverage CheckKeywords(JobOffer offer)
        {
            var text = CandidateProfileProvider.Normalize($"{offer.Title} {offer.Description}");

            bool Appears(string keyword) =>
                text.Contains(CandidateProfileProvider.Normalize(keyword), StringComparison.Ordinal);

            var matched = _config.RequiredKeywords.Where(Appears).ToList();
            var missing = _config.RequiredKeywords.Where(k => !Appears(k)).ToList();

            return new KeywordCoverage(matched, missing);
        }

        private static string Shorten(string value) =>
            value.Length <= 40 ? value : value[..40].Trim() + "…";

        private static string Html(string? text) =>
            (text ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static string Html(IEnumerable<string> values)
        {
            var joined = string.Join(", ", values.Select(v => v.Replace("<", "&lt;").Replace(">", "&gt;")));
            return joined.Length > 300 ? joined[..300] + "…" : joined;
        }

        private async Task ReportGeminiFailureAsync(JobOffer offer, CancellationToken ct)
        {
            lock (_pipelineLock)
            {
                if (_geminiFailureReported) return;
                _geminiFailureReported = true;
            }

            _logger.LogWarning(
                "Avisando en Telegram del fallo de Gemini; el resto de ofertas del ciclo también fallarán.");

            await _notifier.SendSystemAlertAsync(
                "⚠️ <b>Gemini no está disponible</b>\n\n" +
                $"No he podido evaluar <b>{Html(Shorten(offer.Title))}</b> ni el resto de ofertas de este ciclo.\n\n" +
                "Causa más probable: <b>cuota diaria agotada</b> del modelo en el plan gratuito (20 peticiones/día y modelo).\n\n" +
                "Las ofertas <b>no se marcan como procesadas</b>: se reintentarán en el próximo ciclo.\n" +
                "Si persiste, revisa la clave y el plan en Google AI Studio.",
                ct);
        }

        private bool TryBeginPipeline(string origin)
        {
            lock (_pipelineLock)
            {
                if (_pipelineBusy)
                {
                    _logger.LogWarning(
                        "Escaneo rechazado ({Origin}): ya hay uno en marcha ({Current}).",
                        origin, string.IsNullOrEmpty(_pipelineOrigin) ? "otro" : _pipelineOrigin);
                    return false;
                }

                _pipelineBusy = true;
                _pipelineOrigin = origin;
                _geminiFailureReported = false;
                _evaluationsThisCycle = 0;
                return true;
            }
        }

        private bool TryTakeEvaluationSlot()
        {
            lock (_pipelineLock)
            {
                var max = Math.Max(1, _config.MaxOffersPerCycle);
                if (_evaluationsThisCycle >= max) return false;

                _evaluationsThisCycle++;
                return true;
            }
        }

        private void EndPipeline()    {
            lock (_pipelineLock)
            {
                _pipelineBusy = false;
                _pipelineOrigin = string.Empty;
            }
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

            return PassesLocationFilter(offer, text);
        }

        private bool PassesLocationFilter(JobOffer offer, string combinedText)
        {
            var filter = _config.LocationFilter;

            var remoteKeywords = (filter.RemoteKeywords is { Count: > 0 }
                ? filter.RemoteKeywords
                : new List<string> { "remoto", "teletrabajo", "remote", "full remote", "100% remoto" })
                .Select(k => k.ToLowerInvariant())
                .ToList();

            var isRemote = offer.IsRemote || remoteKeywords.Any(k => combinedText.Contains(k));
            if (isRemote) return true;

            if (filter.AcceptedLocations is not { Count: > 0 }) return true;

            if (string.IsNullOrWhiteSpace(offer.Province) && string.IsNullOrWhiteSpace(offer.City))
            {
                _logger.LogDebug(
                    "Ubicación no publicada por la fuente para '{Title}'; se evalúa igualmente.",
                    offer.Title);
                return true;
            }

            var haystack = $"{offer.Province} {offer.City} {offer.Title} {offer.Description}".ToLowerInvariant();
            return filter.AcceptedLocations.Any(loc => haystack.Contains(loc.ToLowerInvariant()));
        }
    }
}
