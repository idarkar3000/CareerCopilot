using System.Text;
using System.Text.Json;
using CareerCopilot.Models;

namespace CareerCopilot.Services;

public class GeminiScorerService
{
    private readonly HttpClient _httpClient;
    private readonly BotConfig _config;
    private readonly ILogger<GeminiScorerService> _logger;

    // Case-insensitive porque el responseSchema pide claves camelCase (score, tailoredSummary...)
    // mientras que EvaluationResult usa PascalCase (Score, TailoredSummary...). Sin esto,
    // Deserialize<EvaluationResult> devuelve casi todos los campos en su valor por defecto
    // (null / lista vacía) sin lanzar ninguna excepción, con lo que el "tailoring" nunca llega
    // a producirse aunque Gemini responda perfectamente.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Modelos válidos a fecha de hoy (comprobado); gemini-2.5-flash tiene fecha de apagado
    // anunciada (16 oct 2026), así que ya incluye un sustituto de la familia Gemini 3 al final.
private static readonly string[] ActiveModels =
    {
        "gemini-flash-latest",
        "gemini-3.1-flash-lite",
        "gemini-3.5-flash",
        "gemini-2.5-flash"
    };

    public GeminiScorerService(HttpClient httpClient, BotConfig config, ILogger<GeminiScorerService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<EvaluationResult?> EvaluateAsync(JobOffer job, CancellationToken ct)
    {
        var prompt = BuildPrompt(job);
        var jsonPayload = BuildRequestPayload(prompt);

        foreach (var model in ActiveModels)
        {
            if (ct.IsCancellationRequested) return null;

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_config.GeminiApiKey}";
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(url, content, ct);
                var responseString = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    var result = TryParseEvaluation(responseString, model);
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
                if (statusCode == 503 || statusCode == 429)
                {
                    _logger.LogWarning("Modelo {Model} ocupado ({Code}). Probando inmediatamente con el siguiente...", model, statusCode);
                    continue;
                }

                _logger.LogWarning("Respuesta no exitosa ({Model}): {Code} - {Msg}", model, statusCode, responseString);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al invocar modelo {Model}.", model);
            }
        }

        _logger.LogError("Ningún modelo de Gemini devolvió una evaluación válida para '{Title}' en '{Company}'.", job.Title, job.Company);
        return null;
    }

    private string BuildPrompt(JobOffer job) => $$"""
Actúa como un selector técnico senior y experto en filtros ATS.
Evalúa la compatibilidad del candidato con la oferta y redacta las viñetas del currículum ADAPTADAS a esta vacante concreta.

PERFIL BASE DEL CANDIDATO(única fuente de verdad sobre su experiencia real):
{ { _config.CandidateProfile} }

    OFERTA DE TRABAJO:
Puesto: {{job.Title
}}
Empresa: { { job.Company} }
Descripción / Requisitos:
{ { job.Description} }

REGLA DE ORO — ANTI-ALUCINACIÓN (la más importante de todas):
Todo lo que escribas en 'tailoredSummary' y 'tailoredExperience' debe basarse EXCLUSIVAMENTE en hechos,
tecnologías, proyectos y logros que aparezcan literalmente en el PERFIL BASE DEL CANDIDATO de arriba.
Está PROHIBIDO inventar tecnologías, empresas, proyectos, certificaciones, cifras o logros que no figuren
en ese perfil. Tu trabajo NO es inventar contenido nuevo: es SELECCIONAR, PRIORIZAR y REFORMULAR los
logros reales del candidato para resaltar los que mejor encajen con esta oferta concreta. Si la oferta pide
algo que el candidato no tiene, no lo añadas a las viñetas; refléjalo en 'concerns' en su lugar.

REGLAS DE REDACCIÓN:
1.NIVEL PROFESIONAL: el candidato es un Desarrollador Backend JUNIOR que completó sus prácticas en
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
   relacionales. Adapta el enfoque del resumen a lo que más valore la vacante (por ejemplo: si piden
   calidad / testing destaca xUnit y CI/CD; si piden datos / rendimiento destaca SQL, Informix y optimización
   con hash; si piden arquitectura destaca CQRS y microservicios). No uses corchetes '[' ni ']'.
7. 'tailoredExperience': EXACTAMENTE 4 viñetas (entre 120 y 160 caracteres cada una) sobre la experiencia
   en EPAM Neoris. 
   DIRECTRICES ESTRICTAS PARA EVITAR VIÑETAS REPETITIVAS:
   -Prohibido seguir siempre la misma plantilla fija de 4 viñetas idénticas.
   - Analiza los requisitos de la vacante y reordena las prioridades: la primera viñeta SIEMPRE debe ser
     la que ataque de forma más directa el requerimiento principal de la oferta.
   - Si la oferta busca perfil de datos, rendimiento o algoritmos: prioriza la optimización de consultas
     SQL Server e Informix, la refactorización con estructuras hash y diccionarios en memoria (<10s).
   - Si busca APIs, microservicios o web: prioriza CQRS(Read/Write), handlers desacoplados, DTOs,
     CORS y comunicación backend-frontend.
   - Si busca calidad de software o DevOps: prioriza tests unitarios con xUnit, testing con Postman/Swagger
     y automatización con pipelines de CI/CD en Azure DevOps.
   - Sintetiza y redacta de manera fluida y profesional. No uses corchetes '[' ni ']'.
""";

    private static string BuildRequestPayload(string prompt)
    {
        var requestBody = new
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

        return JsonSerializer.Serialize(requestBody);
    }

    private EvaluationResult? TryParseEvaluation(string responseString, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseString);

            if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            {
                // Puede venir bloqueado por seguridad (promptFeedback.blockReason) en vez de candidates.
                if (doc.RootElement.TryGetProperty("promptFeedback", out var feedback))
                {
                    _logger.LogWarning("Modelo {Model} sin candidatos; promptFeedback: {Feedback}", model, feedback.GetRawText());
                }
                else
                {
                    _logger.LogWarning("Modelo {Model} devolvió respuesta sin 'candidates'.", model);
                }
                return null;
            }

            var firstCandidate = candidates[0];
            if (firstCandidate.TryGetProperty("finishReason", out var finishReason) &&
                finishReason.GetString() is { } fr && fr != "STOP")
            {
                _logger.LogWarning("Modelo {Model} terminó con finishReason={Reason} (posible corte o filtro).", model, fr);
            }

            var rawJsonText = firstCandidate
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

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


    private EvaluationResult ValidateAndNormalize(EvaluationResult result, string model)
    {
        if (result.Score < 0 || result.Score > 100)
        {
            _logger.LogWarning("Modelo {Model}: score fuera de rango ({Score}), se ajusta a [0,100].", model, result.Score);
            result.Score = Math.Clamp(result.Score, 0, 100);
        }

        // El propio Worker/appsettings decide el umbral; no dejamos que la IA decida 'match' sola.
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
}