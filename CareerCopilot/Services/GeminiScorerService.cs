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

    private static readonly string[] ActiveModels =
    {
        "gemini-2.5-flash",
        "gemini-2.0-flash",
        "gemini-1.5-flash"
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
Actúa como un selector técnico senior y preparador de CVs técnicos en .NET y C#.
Evalúa la compatibilidad del candidato con la oferta y redacta las secciones adaptadas del currículum.

PERFIL BASE DEL CANDIDATO (ÚNICA FUENTE DE VERDAD):
{{_config.CandidateProfile}}

OFERTA DE TRABAJO:
Puesto: {{job.Title}}
Empresa: {{job.Company}}
Descripción / Requisitos:
{{job.Description}}

REGLAS DE ORO CONTRA ALUCINACIONES:
1. Todo lo que redactes en 'tailoredSummary' y 'tailoredExperience' debe basarse ESTRICTAMENTE en datos presentes en el PERFIL BASE.
2. PROHIBIDO inventar cifras cuantitativas de rendimiento (ej: 'reducción de 60s a 10s', porcentajes ficticios) o tecnologías que no domine.
3. REGLA DE AISLAMIENTO: Las viñetas de 'tailoredExperience' pertenecen EXCLUSIVAMENTE a las responsabilidades en EPAM Neoris (Backend, Microservicios, CQRS, Minimal APIs, SQL Server con Dapper, Mapster, Angular, xUnit). NUNCA traslades tareas de proyectos personales como Pdf_Signer (WPF, firmas manuscritas, visores PDF) o RadarChollos a la experiencia laboral de EPAM Neoris.
4. Si la vacante exige herramientas no presentes en el perfil (ej. Java, PHP, AWS avanzado, React), refléjalas honestamente en 'concerns'.

REGLAS DE REDACCIÓN:
1. VOZ Y TONO ESTRICTOS: Tanto 'tailoredSummary' como 'tailoredExperience' DEBEN ESTAR ESCRITOS EN PRIMERA PERSONA DEL SINGULAR ("Soy desarrollador backend...", "Cuento con experiencia en...", "Implementé...", "Diseñé..."). NUNCA hables en tercera persona ("Ingeniero de software que aporta...", "El candidato cuenta con...").
2. NIVEL PROFESIONAL: El candidato es un Desarrollador Backend JUNIOR con prácticas finalizadas. Emplea verbos directos de ejecución técnica ("Desarrollé", "Configuré", "Implementé", "Optimicé").
3. 'score': Entero de 0 a 100 evaluando afinidad técnica real.
4. 'match': true si score >= {{_config.MinScoreThreshold}}, false en caso contrario.
5. 'strengths': Exactamente 3 puntos fuertes técnicos reales alineados con la oferta.
6. 'concerns': 1 o 2 requisitos que pida la oferta y el candidato no posea o deba reforzar.
7. 'tailoredSummary': Resumen profesional escrito en PRIMERA PERSONA ("Soy...", "Aporto..."), fluido, sólido y continuo de 4 a 6 líneas (ENTRE 500 Y 700 CARACTERES). Debe exponer tu base en C#, ASP.NET Core, microservicios, bases de datos relacionales y cómo encajas con la vacante. No uses corchetes '[' ni ']'.
8. 'tailoredExperience': EXACTAMENTE 4 viñetas técnicas redactadas en primera persona ("Diseñé...", "Implementé...", "Configuré...") de ENTRE 150 Y 210 CARACTERES cada una, adaptadas de las responsabilidades reales en EPAM Neoris. No uses corchetes '[' ni ']'.
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
            _logger.LogWarning("Modelo {Model}: 'tailoredExperience' vacío; se utilizará fallback en compilación.", model);
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