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

        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash-latest:generateContent?key={_config.GeminiApiKey}";

        var prompt = $@"
Eres un reclutador técnico experto en perfiles .NET y C#.
Evalúa la adecuación entre esta oferta de empleo y el perfil del candidato.

PERFIL DEL CANDIDATO:
- Nombre: Adrián Espínola Gumiel
- Nivel: Junior Backend Developer (.NET / C#)
- Formación: Grado Universitario en Diseño y Desarrollo de Videojuegos (URJC) y Técnico Superior en Animación 3D, Juegos y Entornos Interactivos.
- Experiencia: Prácticas finalizadas como desarrollador backend en EPAM Neoris.
- Stack principal: C#, .NET 8/9, ASP.NET Core, REST APIs, Microservicios, Entity Framework Core, Dapper, SQL Server, SQLite, Postman, Docker, Git.
- Conocimientos adicionales: Arquitecturas limpias (Vertical Slice, CQRS), WPF, XAML, integración con LLMs y herramientas AI.

OFERTA DE EMPLEO:
- Título: {job.Title}
- Empresa: {job.Company}
- Descripción:
{job.Description}

INSTRUCCIONES DE RESPUESTA:
Responde EXCLUSIVAMENTE con un JSON válido con este formato exacto, sin explicaciones ni bloques markdown ```json:
{{
  ""score"": <entero de 0 a 100 indicando compatibilidad real para un perfil junior backend .NET>,
  ""match"": <true si score >= {_config.MinScoreThreshold}, false en caso contrario>,
  ""strengths"": [""punto fuerte 1"", ""punto fuerte 2"", ""punto fuerte 3""],
  ""concerns"": [""aspecto a tener en cuenta o requisito no cumplido 1"", ""punto 2""]
}}";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.2,
                responseMimeType = "application/json"
            }
        };

        try
        {
            var response = await _http.PostAsJsonAsync(endpoint, payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Error de la API de Gemini ({Status}): {Body}", response.StatusCode, errorBody);
                return null;
            }

            var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiApiResponse>(cancellationToken: ct);
            var rawText = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(rawText))
            {
                _logger.LogWarning("Gemini no devolvió texto de respuesta.");
                return null;
            }

            rawText = rawText.Trim();
            if (rawText.StartsWith("```json")) rawText = rawText[7..];
            if (rawText.StartsWith("```")) rawText = rawText[3..];
            if (rawText.EndsWith("```")) rawText = rawText[..^3];
            rawText = rawText.Trim();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<EvaluationResult>(rawText, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excepción al evaluar la oferta con Gemini.");
            return null;
        }
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