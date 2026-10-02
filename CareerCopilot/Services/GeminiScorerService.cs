using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CareerCopilot.Models;

namespace CareerCopilot.Services;

public class GeminiScorerService
{
    private readonly HttpClient _http;
    private readonly BotConfig _config;
    private readonly CandidateProfileProvider _profile;
    private readonly CvSanitizer _sanitizer;
    private readonly ILogger<GeminiScorerService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Modelos marcados como agotados hasta su reset diario (medianoche hora del Pacífico)
    private readonly Dictionary<string, DateTimeOffset> _exhaustedModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fallos seguidos por modelo. Google responde 503 cuando un modelo está saturado, y eso no
    /// marca cuota agotada, así que se cuenta y solo se salta el modelo cuando insiste.
    /// </summary>
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fallos seguidos a partir de los cuales un modelo se deja de llamar.</summary>
    private const int MaxConsecutiveFailures = 3;

    private readonly Lock _quotaLock = new();

    /// <summary>Último modelo que devolvió una evaluación válida; se prueba primero.</summary>
    private volatile string? _preferredModel;

    // Modelos que se van probando por orden hasta que uno devuelva una evaluación válida
    private static readonly string[] ActiveModels =
    {
        "gemini-3.8-flash",
        "gemini-3.7-flash",
        "gemini-3.6-flash",
        "gemini-3.5-flash",
        "gemini-3.1-flash-lite"
    };

    public GeminiScorerService(
        HttpClient http,
        BotConfig config,
        CandidateProfileProvider profile,
        CvSanitizer sanitizer,
        ILogger<GeminiScorerService> logger)
    {
        _http = http;
        _config = config;
        _profile = profile;
        _sanitizer = sanitizer;
        _logger = logger;
    }

    public async Task<EvaluationResult?> EvaluateAsync(JobOffer job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.GeminiApiKey))
        {
            _logger.LogError("GeminiApiKey no está configurada.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(_profile.Text))
        {
            _logger.LogError("No hay perfil del candidato cargado; no se puede evaluar '{Title}'.", job.Title);
            return null;
        }

        var payload = BuildRequestPayload(job);

        foreach (var model in BuildModelOrder())
        {
            if (ct.IsCancellationRequested) return null;

            var result = await CallWithQuotaRetryAsync(model, job.Title, payload, ct);
            if (result != null)
            {
                _preferredModel = model;
                return result;
            }
        }

        _logger.LogError("Ningún modelo de Gemini devolvió una evaluación válida para '{Title}' en '{Company}'.", job.Title, job.Company);
        return null;
    }

    /// <summary>Reordena los modelos para saltar los agotados y priorizar el que más ha funcionado.</summary>
    private List<string> BuildModelOrder()
    {
        lock (_quotaLock)
        {
            PruneExhausted();

            var available = ActiveModels.Where(m => !_exhaustedModels.ContainsKey(m)).ToList();

            if (_preferredModel is not null && available.Remove(_preferredModel))
            {
                available.Insert(0, _preferredModel);
            }

            if (available.Count == 0)
            {
                var next = _exhaustedModels.Values.DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
                _logger.LogWarning(
                    "Todos los modelos están sin cuota. Se reintentará a partir de {Reset:HH:mm} hora del Pacífico ({Minutes} min).",
                    next.ToPacificTime(), Math.Max(0, (int)(next - DateTimeOffset.UtcNow).TotalMinutes));
            }

            return available;
        }
    }

    private void PruneExhausted()
    {
        var expired = _exhaustedModels.Where(kvp => kvp.Value <= DateTimeOffset.UtcNow).Select(kvp => kvp.Key).ToList();
        foreach (var model in expired)
        {
            _exhaustedModels.Remove(model);
            _failures.Remove(model);
            _logger.LogInformation("Cuota repuesta para el modelo {Model}; vuelve a estar disponible.", model);
        }
    }

    private void MarkExhausted(string model)
    {
        lock (_quotaLock)
        {
            _failures.Remove(model);
            MarkExhaustedLocked(model);
        }
    }

    private void MarkExhaustedLocked(string model)
    {
        var resume = DateTimeOffset.UtcNow.NextPacificMidnight();
        _exhaustedModels[model] = resume;

        _logger.LogWarning(
            "Modelo {Model} marcado como agotado hasta las {Reset:HH:mm} (hora del Pacífico). Se dejará de llamar hasta entonces.",
            model, resume.ToPacificTime());
    }

    /// <summary>
    /// Anota un fallo del modelo. Un 429 es definitivo y lo aparta enseguida; un 503 o un timeout
    /// solo lo apartan cuando se repiten, para no perder el modelo por un pico puntual de demanda.
    /// </summary>
    private void RegisterFailure(string model, bool definitive)
    {
        lock (_quotaLock)
        {
            if (definitive)
            {
                _failures.Remove(model);
                MarkExhaustedLocked(model);
                return;
            }

            var count = _failures.GetValueOrDefault(model) + 1;
            if (count < MaxConsecutiveFailures)
            {
                _failures[model] = count;
                return;
            }

            _failures.Remove(model);
            MarkExhaustedLocked(model);
        }
    }

    /// <summary>El modelo respondió bien, así que se le olvida todo lo anterior.</summary>
    private void RegisterSuccess(string model)
    {
        lock (_quotaLock)
        {
            _failures.Remove(model);
        }
    }

    /// <summary>Llama al modelo y reintenta una vez si la cuota dice que no (429/503).</summary>
    private async Task<EvaluationResult?> CallWithQuotaRetryAsync(string model, string jobTitle, object payload, CancellationToken ct)
    {
        const int maxAttempts = 2;
        const int maxWaitSeconds = 20;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) return null;

            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_config.GeminiApiKey}";

            try
            {
                using var response = await _http.PostAsJsonAsync(endpoint, payload, ct);

                if (response.IsSuccessStatusCode)
                {
                    var rawBody = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogDebug("Respuesta cruda de {Model} para '{Title}': {Body}", model, jobTitle, rawBody);

                    var result = TryParseEvaluation(rawBody, model);
                    if (result != null)
                    {
                        RegisterSuccess(model);
                        _logger.LogInformation("Evaluado y adaptado con éxito usando el modelo: {Model}", model);
                        return result;
                    }

                    return null;
                }

                var statusCode = (int)response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync(ct);

                if (statusCode is 429 or 503)
                {
                    // 429 es cuota agotada; 503 es demanda alta y puede ser un pico pasajero
                    var motivo = statusCode == 429 ? "sin cuota" : "saturado";
                    RegisterFailure(model, definitive: statusCode == 429);

                    if (attempt < maxAttempts)
                    {
                        var wait = GetRetryAfterSeconds(response) ?? 5;
                        wait = Math.Clamp(wait, 1, maxWaitSeconds);

                        _logger.LogWarning(
                            "Modelo {Model} {Motivo} ({Code}). Reintento {Next}/{Max} en {Wait}s.",
                            model, motivo, statusCode, attempt + 1, maxAttempts, wait);

                        await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                        continue;
                    }

                    _logger.LogWarning(
                        "Modelo {Model} {Motivo} ({Code}) tras {Attempts} intentos: {Body}",
                        model, motivo, statusCode, maxAttempts, Summarize(errorBody));
                    return null;
                }

                _logger.LogWarning("Respuesta no exitosa ({Model}): {Code} - {Body}", model, statusCode, Summarize(errorBody));
                return null;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // El timeout del HttpClient, no una cancelación: el modelo se quedó colgado
                RegisterFailure(model, definitive: false);

                _logger.LogWarning(
                    "Modelo {Model} no respondió en {Timeout:N0}s y se da por saturado.",
                    model, _http.Timeout.TotalSeconds);

                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Excepción al invocar modelo {Model}.", model);
                return null;
            }
        }

        return null;
    }

    /// <summary>Recorta el cuerpo de un error de la API para no volcar kilobytes en el log.</summary>
    private static string Summarize(string body) =>
        body.Length <= 300 ? body : body[..300] + "...";

    private static int? GetRetryAfterSeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return (int)Math.Ceiling(delta.TotalSeconds);
        if (retryAfter?.Date is { } date)
        {
            var seconds = (date - DateTimeOffset.UtcNow).TotalSeconds;
            return seconds > 0 ? (int)Math.Ceiling(seconds) : 1;
        }
        return null;
    }

    private object BuildRequestPayload(JobOffer job)
    {
        var prompt = $$"""
            Actúa como un selector técnico senior y preparador de currículums técnicos junior en .NET y C#.
            Evalúa la compatibilidad del candidato con la oferta y redacta el CV completo adaptado a ella.

            PERFIL BASE DEL CANDIDATO (ÚNICA FUENTE DE VERDAD):
            {{_profile.Text}}

            OFERTA DE TRABAJO:
            Puesto: {{job.Title}}
            Empresa: {{job.Company}}
            Ubicación: {{job.Province}} {{job.City}} (remoto: {{job.IsRemote}})
            Descripción / Requisitos:
            {{job.Description}}

            REGLAS DE ORO CONTRA ALUCINACIONES (prioridad absoluta):
            1. TODO lo que escribas debe basarse ESTRICTAMENTE en el PERFIL BASE.
            2. PROHIBIDO inventar cifras de rendimiento, porcentajes, tecnologías, proyectos, certificaciones o empleadores.
            3. REGLA DE AISLAMIENTO: Minimal APIs, Entity Framework Core, SQLite, Telegram.Bot, Serilog, Render, UptimeRobot y WPF son de los proyectos personales, NUNCA de la experiencia en EPAM Neoris. En EPAM solo: Vertical Slice Architecture, microservicios, CQRS, handlers, Dapper, Mapster, FluentValidation, contratos en DI, IBM Informix, SQL Server, JWT, roles y políticas de autorización, Swagger/OpenAPI, Postman, xUnit, Azure DevOps, Scrum, GitFlow y el soporte al frontend con Angular.
            4. NUNCA nombres al cliente ni su sector: di siempre "plataforma corporativa de cliente en PowerBuilder".
            5. La ciudad, el teléfono con prefijo, la disponibilidad y los enlaces los imprime la plantilla: no los repitas en los textos.
            6. Si la vacante exige herramientas que no están en el perfil, refléjtalo solo en 'concerns'.

            REGLAS DE REDACCIÓN:
            1. VOZ: primera persona del singular y nivel JUNIOR ("Implementé", "Diseñé", "Escribí", "Configuré"). Nunca tercera persona ni autobombo.
            2. CONCRECIÓN: cada viñeta dice qué hiciste y con qué, sin relleno.
            3. FONDO DE PUNTOS: elige de 'points', en el bloque cv-anchors del perfil, los puntos que mejor encajen con la oferta. No inventes puntos que no estén ahí.
               - Experiencia Laboral: exactamente 5 'bullets', los 5 más relevantes de sus 'points'. Es la sección más importante del CV.
               - Proyectos: 3 'bullets' por proyecto, los más relevantes de sus 'points'.
               - Si una entrada no trae 'bullets', elige los 5 (experiencia) o 3 (proyecto) primeros puntos de su 'points'.
            4. Estructura del CV ('cv'):
               - 'headline': una línea, el puesto al que aspiras (ej. "Desarrollador Backend .NET / C#").
               - 'summary': dos líneas en primera persona, entre 200 y 280 caracteres, que exponga tu base en C#, ASP.NET Core, microservicios y bases de datos relacionales y la conecte con la vacante. Sin listas ni corchetes. Es un CV de una página: si te pasas, la última sección se queda fuera.
               - 'sections': entre 4 y 5 secciones, siempre en este conjunto y siempre estas, en este orden:
                  * "Experiencia Laboral" (kind "entries", priority 1): la de EPAM Neoris. 'title', 'org', 'dates', 'stack' son OBLIGATORIOS y ningún item puede quedar solo con 'title'.
                  * "Proyectos" (kind "entries", priority 1 o 2): 'title', 'stack' y 3 'bullets' son obligatorios en cada proyecto, y 'url' es OBLIGATORIA siempre que el proyecto tenga repositorio en el perfil. 'dates' solo si las tienes.
                  * Solo los proyectos que aparecen en el perfil. Nunca inventes, combines ni renombres un proyecto.
                 * "Habilidades Técnicas" (kind "texts", priority 1): 5 o 6 items con 'label' ("Lenguajes y frameworks", "Arquitectura y patrones", "Bases de datos", "Seguridad", "Frontend e integración", "DevOps y herramientas") y 'text'.
                 * "Formación Académica" (kind "texts", priority 2): 2 items, uno por titulación, con 'label' y 'text'. Mención de Excelencia Académica, nunca Premio Extraordinario.
                 * "Idiomas" (kind "texts", priority 3): 2 items, con 'label' y 'text'.
               - 'priority': 1 = imprescindible, 2 = recomendable, 3 = secundario. El compilador recorta primero lo de prioridad 3 si el CV no cabe en una página, así que sube a 1 lo que la oferta valore más.
            5. 'score': entero de 0 a 100 con la afinidad técnica real.
            6. 'match': true si score >= {{_config.MinScoreThreshold}}.
            7. 'strengths': exactamente 3 puntos fuertes técnicos reales de la oferta.
            8. 'concerns': 1 o 2 requisitos que pida la oferta y no puedes cubrir.
            9. Si el perfil no da para una sección, es preferible dejar items fuera antes que inventar.
            """;

        return new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        score = new { type = "INTEGER" },
                        match = new { type = "BOOLEAN" },
                        strengths = new { type = "ARRAY", items = new { type = "STRING" } },
                        concerns = new { type = "ARRAY", items = new { type = "STRING" } },
                        cv = new
                        {
                            type = "OBJECT",
                            properties = new
                            {
                                headline = new { type = "STRING" },
                                summary = new { type = "STRING" },
                                sections = new
                                {
                                    type = "ARRAY",
                                    minItems = 4,
                                    items = new
                                    {
                                        type = "OBJECT",
                                        properties = new
                                        {
                                            heading = new { type = "STRING" },
                                            priority = new { type = "INTEGER" },
                                            kind = new { type = "STRING" },
                                            items = new
                                            {
                                                type = "ARRAY",
                                                minItems = 1,
                                                items = new
                                                {
                                                    type = "OBJECT",
                                                    properties = new
                                                    {
                                                        label = new { type = "STRING" },
                                                        text = new { type = "STRING" },
                                                        title = new { type = "STRING" },
                                                        org = new { type = "STRING" },
                                                        dates = new { type = "STRING" },
                                                        stack = new { type = "STRING" },
                                                        url = new { type = "STRING" },
                                                        urlLabel = new { type = "STRING" },
                                                        bullets = new { type = "ARRAY", minItems = 1, items = new { type = "STRING" } }
                                                    }
                                                }
                                            }
                                        },
                                        // Sin esto el modelo puede devolver 'sections' vacío
                                        required = new[] { "heading", "priority", "kind", "items" }
                                    }
                                }
                            },
                            required = new[] { "headline", "summary", "sections" }
                        }
                    },
                    required = new[] { "score", "match", "strengths", "concerns", "cv" }
                }
            }
        };
    }

    private EvaluationResult? TryParseEvaluation(string rawBody, string model)
    {
        try
        {
            var geminiResponse = JsonSerializer.Deserialize<GeminiApiResponse>(rawBody, JsonOptions);
            var candidate = geminiResponse?.Candidates?.FirstOrDefault();

            if (candidate == null)
            {
                _logger.LogWarning("Modelo {Model}: respuesta sin 'candidates' (posible filtro de seguridad).", model);
                return null;
            }

            if (candidate.FinishReason is { } fr && fr != "STOP")
            {
                _logger.LogWarning("Modelo {Model} finalizó con finishReason={Reason}.", model, fr);
            }

            var rawJsonText = candidate.Content?.Parts?.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(rawJsonText))
            {
                _logger.LogWarning("Modelo {Model} devolvió texto vacío.", model);
                return null;
            }

            var result = JsonSerializer.Deserialize<EvaluationResult>(rawJsonText, JsonOptions);
            if (result == null)
            {
                _logger.LogWarning("Modelo {Model}: no se pudo deserializar EvaluationResult.", model);
                return null;
            }

            return ValidateAndNormalize(result, model);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Modelo {Model}: JSON de evaluación inválido o incompleto.", model);
            return null;
        }
    }

    private EvaluationResult? ValidateAndNormalize(EvaluationResult result, string model)
    {
        result.Score = Math.Clamp(result.Score, 0, 100);
        result.Match = result.Score >= _config.MinScoreThreshold;

        result.Strengths ??= new List<string>();
        result.Concerns ??= new List<string>();

        // Sin secciones no hay nada que maquetar. Se cuenta como fallo del modelo para que el
        // bucle pruebe con el siguiente en vez de avisar de un CV inexistente.
        if (result.Cv == null || result.Cv.Sections.Count == 0)
        {
            _logger.LogWarning(
                "Modelo {Model}: devolvió un CV sin secciones (secciones={Count}); se considera respuesta inválida y se prueba otro modelo.",
                model, result.Cv?.Sections.Count ?? 0);
            return null;
        }

        if (string.IsNullOrWhiteSpace(result.Cv.Summary))
        {
            _logger.LogWarning("Modelo {Model}: 'cv.summary' vacío.", model);
        }

        LogCvShape(result.Cv, model);

        result.SanitizerViolations = _sanitizer.Sanitize(result.Cv);

        return result;
    }

    /// <summary>Registra la estructura que devolvió el modelo: qué entradas vienen incompletas.</summary>
    private void LogCvShape(CvDocument cv, string model)
    {
        var entries = cv.Sections
            .Where(s => string.Equals(s.Kind, "entries", StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s.Items ?? new List<CvItem>())
            .ToList();

        var incomplete = entries
            .Where(i => string.IsNullOrWhiteSpace(i.Org)
                        || string.IsNullOrWhiteSpace(i.Stack)
                        || i.Bullets is not { Count: > 0 })
            .ToList();

        if (incomplete.Count == 0)
        {
            _logger.LogInformation("Estructura del CV de {Model}: {Sections} secciones, {Entries} entradas completas.",
                model, cv.Sections.Count, entries.Count);
            return;
        }

        _logger.LogWarning(
            "Modelo {Model}: {Incomplete} de {Total} entradas sin org, stack o viñetas ({Detail}). Se rellenarán desde las anclas del perfil.",
            model, incomplete.Count, entries.Count,
            string.Join(" | ", incomplete.Select(i =>
                $"{i.Title}: org={(string.IsNullOrWhiteSpace(i.Org) ? "no" : "sí")}, " +
                $"stack={(string.IsNullOrWhiteSpace(i.Stack) ? "no" : "sí")}, " +
                $"bullets={i.Bullets?.Count ?? 0}, " +
                $"url={(string.IsNullOrWhiteSpace(i.Url) ? "no" : "sí")}")));
    }

    private sealed class GeminiApiResponse
    {
        [JsonPropertyName("candidates")]
        public List<Candidate>? Candidates { get; set; }
    }

    private sealed class Candidate
    {
        [JsonPropertyName("content")]
        public ContentData? Content { get; set; }

        [JsonPropertyName("finishReason")]
        public string? FinishReason { get; set; }
    }

    private sealed class ContentData
    {
        [JsonPropertyName("parts")]
        public List<PartData>? Parts { get; set; }
    }

    private sealed class PartData
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}

internal static class PacificTime
{
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Pacific Standard Time" : "America/Los_Angeles");

    /// <summary>La cuota diaria del tier gratuito de Gemini se reinicia a medianoche en el Pacífico.</summary>
    public static DateTimeOffset NextPacificMidnight(this DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone);
        var nextDay = local.Date.AddDays(1);
        var offset = Zone.GetUtcOffset(new DateTimeOffset(nextDay, TimeSpan.Zero));

        return new DateTimeOffset(nextDay, TimeSpan.Zero).ToOffset(offset).ToUniversalTime();
    }

    public static DateTimeOffset ToPacificTime(this DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, Zone);
}
