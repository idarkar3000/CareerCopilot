using System.Text.Json.Serialization;

namespace CareerCopilot.Models;

public class EvaluationResult
{
    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("match")]
    public bool Match { get; set; }

    [JsonPropertyName("strengths")]
    public List<string> Strengths { get; set; } = new();

    [JsonPropertyName("concerns")]
    public List<string> Concerns { get; set; } = new();

    /// <summary>CV completo adaptado a la oferta. Nulo solo si el modelo no lo devolvió.</summary>
    [JsonPropertyName("cv")]
    public CvDocument? Cv { get; set; }

    /// <summary>True si el CV se montó sin Gemini, por falta de cuota. No lleva score fiable.</summary>
    [JsonPropertyName("isLocal")]
    public bool IsLocal { get; set; }

    /// <summary>Tecnologías verificables que el CV mencionó y que NO aparecen en el perfil. Solo para diagnóstico.</summary>
    [JsonPropertyName("sanitizerViolations")]
    public List<string> SanitizerViolations { get; set; } = new();
}
