using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CareerCopilot.Models;

namespace CareerCopilot.Services;

public class GeminiScorerService
{
    private readonly HttpClient _http;
    private readonly BotConfig _config;
    private readonly ILogger<GeminiScorerService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Modelos vigentes a día de hoy. Si vuelves a ver 404 en todos, comprueba
    // https://ai.google.dev/gemini-api/docs/models antes de asumir otro bug:
    // el catálogo de Gemini cambia con mucha frecuencia.
    private static readonly string[] ActiveModels =
    {
        "gemini-3.8-flash",
        "gemini-3.6-flash",
        "gemini-3.5-flash",
        "gemini-3.1-flash-lite",
        "gemini-2.5-flash"
    };

    public GeminiScorerService(HttpClient http, BotConfig config, ILogger<GeminiScorerService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<EvaluationResult?> EvaluateAsync(JobOffer job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.GeminiApiKey))
        {
            _logger.LogError("GeminiApiKey no está configurada.");
            return null;
        }

        var payload = BuildRequestPayload(job);

        foreach (var model in ActiveModels)
        {
            if (ct.IsCancellationRequested) return null;

            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_config.GeminiApiKey}";

            try
            {
                var response = await _http.PostAsJsonAsync(endpoint, payload, ct);

                if (response.IsSuccessStatusCode)
                {
                    var result = await TryParseEvaluationAsync(response, model, ct);
                    if (result != null)
                    {
                        _logger.LogInformation("Evaluado y adaptado con éxito usando el modelo: {Model}", model);
                        return result;
                    }

                    // JSON válido en el sobre de la API pero contenido inválido/incompleto:
                    // probamos el siguiente modelo en vez de devolver un resultado a medias.
                    continue;
                }

                var statusCode = (int)response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync(ct);

                if (statusCode == 503 || statusCode == 429)
                {
                    _logger.LogWarning("Modelo {Model} ocupado ({Code}). Probando con el siguiente...", model, statusCode);
                    continue;
                }

                _logger.LogWarning("Respuesta no exitosa ({Model}): {Code} - {Body}", model, statusCode, errorBody);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Excepción al invocar modelo {Model}.", model);
            }
        }

        _logger.LogError("Ningún modelo de Gemini devolvió una evaluación válida para '{Title}' en '{Company}'.", job.Title, job.Company);
        return null;
    }

    private object BuildRequestPayload(JobOffer job)
    {
        var prompt = $$"""
Actúa como un selector técnico senior y experto en filtros ATS.
Evalúa la compatibilidad del candidato con la oferta y redacta las viñetas del currículum ADAPTADAS a esta vacante concreta.

PERFIL BASE DEL CANDIDATO (única fuente de verdad sobre su experiencia real):
{{_config.CandidateProfile}}

OFERTA DE TRABAJO:
Puesto: {{job.Title}}
Empresa: {{job.Company}}
Descripción / Requisitos:
{{job.Description}}

REGLA DE ORO — ANTI-ALUCINACIÓN (la más importante de todas):
Todo lo que escribas en 'tailoredSummary' y 'tailoredExperience' debe basarse EXCLUSIVAMENTE en hechos,
tecnologías, proyectos y logros que aparezcan literalmente en el PERFIL BASE DEL CANDIDATO de arriba.
Está PROHIBIDO inventar tecnologías, empresas, proyectos, certificaciones, cifras o logros que no figuren
en ese perfil. Tu trabajo NO es inventar contenido nuevo: es SELECCIONAR, PRIORIZAR y REFORMULAR los
logros reales del candidato para resaltar los que mejor encajen con esta oferta concreta. Si la oferta pide
algo que el candidato no tiene, no lo añadas a las viñetas; refléjalo en 'concerns' en su lugar.

REGLAS DE REDACCIÓN:
1. NIVEL PROFESIONAL: el candidato es un Desarrollador Backend JUNIOR que completó sus prácticas en
   EPAM Neoris. NUNCA digas que es 'Senior' ni que 'Lideró' o 'Dirigió' proyectos. Usa verbos en primera
   persona de ejecución y colaboración: 'Participé en', 'Implementé', 'Desarrollé', 'Refactoricé',
   'Optimicé', 'Colaboré en'.
2. 'score': entero de 0 a 100 evaluando la afinidad técnica real con el puesto, según el perfil base.
3. 'match': true si score >= {{_config.MinScoreThreshold}}, false en caso contrario.
4. 'strengths': exactamente 3 puntos fuertes técnicos del candidato, tomados de su perfil base, que
   estén alineados con esta vacante.
5. 'concerns': 1 o 2 requisitos que pida la vacante y que el candidato no cubra o deba reforzar según su
   perfil base (sé honesto, no minimices carencias reales).
6. 'tailoredSummary': resumen profesional de EXACTAMENTE 3 o 4 líneas (entre 230 y 310 caracteres),
   presentando al candidato como Desarrollador Backend Junior especializado en .NET, C# y bases de datos
   relacionales, priorizando en la redacción los aspectos de su perfil base más relevantes para ESTA
   oferta. No uses corchetes '[' ni ']'.
7. 'tailoredExperience': EXACTAMENTE 4 viñetas (entre 120 y 160 caracteres cada una), extraídas y
   reformuladas a partir de las tareas y logros REALES descritos en el perfil base. Elige las 4 que mejor
   conecten con los requisitos de la vacante; no reutilices siempre las mismas si hay otras más relevantes
   en el perfil base. No uses corchetes '[' ni ']'.
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
                        tailoredSummary = new { type = "STRING" },
                        tailoredExperience = new { type = "ARRAY", items = new { type = "STRING" } }
                    },
                    required = new[] { "score", "match", "strengths", "concerns", "tailoredSummary", "tailoredExperience" }
                }
            }
        };
    }

    private async Task<EvaluationResult?> TryParseEvaluationAsync(HttpResponseMessage response, string model, CancellationToken ct)
    {
        try
        {
            var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiApiResponse>(JsonOptions, ct);
            var candidate = geminiResponse?.Candidates?.FirstOrDefault();

            if (candidate == null)
            {
                _logger.LogWarning("Modelo {Model}: respuesta sin 'candidates' (posible bloqueo de seguridad).", model);
                return null;
            }

            if (candidate.FinishReason is { } fr && fr != "STOP")
            {
                _logger.LogWarning("Modelo {Model} terminó con finishReason={Reason} (posible corte o filtro).", model, fr);
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

    /// <summary>
    /// No confiamos ciegamente en que la IA haya seguido las reglas al pie de la letra: validamos
    /// lo mínimo imprescindible para que CvCompilerService reciba datos coherentes, y recalculamos
    /// 'match' nosotros mismos en vez de fiarnos del booleano devuelto por el modelo.
    /// </summary>
    private EvaluationResult ValidateAndNormalize(EvaluationResult result, string model)
    {
        result.Score = Math.Clamp(result.Score, 0, 100);
        result.Match = result.Score >= _config.MinScoreThreshold;

        if (string.IsNullOrWhiteSpace(result.TailoredSummary))
        {
            _logger.LogWarning("Modelo {Model}: 'tailoredSummary' vacío.", model);
        }

        result.TailoredExperience ??= new List<string>();
        if (result.TailoredExperience.Count == 0)
        {
            _logger.LogWarning("Modelo {Model}: 'tailoredExperience' vacío; el CV usará contenido de reserva.", model);
        }

        result.Strengths ??= new List<string>();
        result.Concerns ??= new List<string>();

        return result;
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