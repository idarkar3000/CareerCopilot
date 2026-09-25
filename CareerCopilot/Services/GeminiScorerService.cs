using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CareerCopilot.Models;

namespace CareerCopilot.Services;

public class GeminiScorerService
{
    private readonly HttpClient _httpClient;
    private readonly BotConfig _config;
    private readonly ILogger<GeminiScorerService> _logger;

    private const string CandidateProfile = """
PERFIL BASE DEL CANDIDATO — Adrián Espínola Gumiel
Este documento es la ÚNICA fuente de verdad que debe usar la IA para redactar
resúmenes y viñetas adaptadas. Nivel: Desarrollador Backend JUNIOR (prácticas
finalizadas). No inventar tecnologías, cifras o logros que no estén aquí.

Experiencia profesional:
Desarrollador Backend .NET (Prácticas) — EPAM Neoris (Marzo 2026 – Junio 2026)
- Diseño e implementación de servicios backend para plataforma modular en microservicios y Vertical Slice Architecture.
- Arquitectura backend: implementación del patrón CQRS (separación de Commands y Queries).
- APIs y seguridad: construcción y aseguramiento de endpoints con Minimal APIs, JWT, FluentValidation y middlewares globales para manejo de excepciones y asincronía estricta (async/await, CancellationToken).
- Persistencia: esquemas relacionales y mapeo en SQL Server con Dapper (database-per-service).
- Estandarización y rendimiento: mapeo de datos y DTOs centralizado con Mapster (MapsterConfig).
- Integración frontend: ajuste y depuración de capa de servicios en Angular para consumo de APIs REST.
- Testing y Calidad: pruebas unitarias de dominio, Scrum en Azure DevOps (Kanban, Git Flow, Pull Requests), CI/CD.
- Adopción de herramientas de IA en el flujo diario para testing, refactorización y depuración.

Proyectos personales en .NET:
1. RadarChollos (github.com/idarkar3000/RadarChollos):
   - Servicio backend en C# / .NET 9 con IHostedService y servidor web ligero en ASP.NET Core con endpoint de salud.
   - Motor interno de reglas con SQLite y EF Core (palabras obligatorias, exclusiones, límites de precio y comercios).
   - Bot Telegram.Bot con comandos en caliente (/start, /help, /list, /add, /delete) para usuario verificado.
   - Docker multi-stage en Render con keep-alive vía UptimeRobot, logging Serilog, tests con xUnit y FluentAssertions.

2. Pdf_Signer (github.com/idarkar3000/Pdf_Signer):
   - App de escritorio en C# y WPF (.NET 10) para visualización y estampado de firmas en PDFs.
   - Visor PDF interactivo (PdfiumViewer) con zoom y vista adaptable.
   - Lienzo para firma manuscrita con grosor y borrador. Estampado dinámico, Drag & Drop y Deshacer/Rehacer (Ctrl+Z/Ctrl+Y).
   - Exportación vectorizada garantizando integridad documental con PdfPig.

Formación académica e Idiomas:
- Grado en Ingeniería en Diseño y Desarrollo de Videojuegos — Universidad Rey Juan Carlos (URJC), 2022–2026. Titulado.
- Técnico Superior en Animación 3D, Juegos y Entornos Interactivos — IFP, 2019–2021. Premio Extraordinario.
- Idiomas: Español (Nativo), Inglés (B2, TOEIC 805/990).

Habilidades técnicas consolidadas:
- Lenguajes y Core: C#, .NET / .NET Core (9 y 10), ASP.NET Core (Minimal APIs, Web APIs), LINQ, Async/Await, WPF / XAML.
- Arquitectura y patrones: CQRS, Vertical Slice Architecture, Microservicios, IHostedService / Background Services, Inyección de Dependencias, JWT, FluentValidation, Middlewares globales.
- Bases de datos y ORMs: IBM Informix, SQL Server, SQLite, Entity Framework Core, Dapper, Mapster (MapsterConfig).
- Testing: xUnit, FluentAssertions.
- DevOps y herramientas: Docker, Git, GitHub, Azure DevOps, Swagger / OpenAPI, Postman, Render, UptimeRobot, Telegram.Bot SDK, Serilog.
- Integración: Angular, adopción de IA en desarrollo.
""";

    public GeminiScorerService(HttpClient httpClient, BotConfig config, ILogger<GeminiScorerService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<EvaluationResult?> EvaluateAsync(JobOffer job, CancellationToken ct)
    {
        try
        {
            var prompt = $@"
Actúa como un evaluador técnico y reclutador senior especializado en .NET y C#.
Evalúa la adecuación del candidato para la siguiente oferta de trabajo utilizando EXCLUSIVAMENTE el Perfil Base proporcionado.

REGLAS ESTRICTAS:
1. NO inventes experiencia ni tecnologías que no estén en el Perfil Base.
2. Si la oferta pide tecnologías ajenas al perfil (ej. Java, PHP, AWS avanzado, React), márcalas como 'concerns'.
3. Si la oferta pide .NET, C#, SQL Server, microservicios, CQRS o WPF, resáltalo en 'strengths'.
4. 'tailoredSummary': Resumen profesional de 2 a 3 frases adaptado al puesto, enfatizando las fortalezas reales (MÁXIMO 420 caracteres).
5. 'tailoredExperience': Lista de 3 o 4 viñetas adaptadas de la experiencia en EPAM Neoris relevantes para este rol (MÁXIMO 150 caracteres por viñeta).

--- PERFIL BASE ---
{CandidateProfile}

--- OFERTA DE EMPLEO ---
Puesto: {job.Title}
Empresa: {job.Company}
Descripción:
{job.Description}

Responde ÚNICAMENTE con un objeto JSON válido con esta estructura exacta:
{{
  ""score"": (número entero de 0 a 100),
  ""strengths"": [""punto 1"", ""punto 2"", ""punto 3""],
  ""concerns"": [""punto 1"", ""punto 2""],
  ""tailoredSummary"": ""texto del resumen"",
  ""tailoredExperience"": [""viñeta 1"", ""viñeta 2"", ""viñeta 3""]
}}";

            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={_config.GeminiApiKey}";

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[] { new { text = prompt } }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.2,
                    responseMimeType = "application/json"
                }
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(endpoint, jsonContent, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Error de la API de Gemini ({StatusCode}): {Error}", response.StatusCode, errorText);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);

            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() == 0) return null;

            var rawText = candidates[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(rawText)) return null;

            var result = JsonSerializer.Deserialize<EvaluationResult>(rawText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al evaluar la oferta '{Title}' con Gemini.", job.Title);
            return null;
        }
    }
}