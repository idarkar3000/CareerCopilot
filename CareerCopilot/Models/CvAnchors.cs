using System.Text.Json.Serialization;

namespace CareerCopilot.Models;

/// <summary>
/// Datos que el CV no puede dejar vacíos aunque el modelo no los devuelva. Van en el bloque
/// cv-anchors de prompts/profile.md y el compilador los mete sobre lo que devuelve Gemini.
/// </summary>
public sealed class CvAnchors
{
    [JsonPropertyName("experience")]
    public List<CvAnchorEntry> Experience { get; set; } = new();

    [JsonPropertyName("projects")]
    public List<CvAnchorEntry> Projects { get; set; } = new();

    /// <summary>Resumen, habilidades, formación e idiomas. Permite montar un CV sin llamar a Gemini.</summary>
    [JsonPropertyName("profile")]
    public CvProfileAnchors Profile { get; set; } = new();

    public bool IsEmpty => Experience.Count == 0 && Projects.Count == 0;
}

public sealed class CvProfileAnchors
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("skills")]
    public List<CvAnchorLine> Skills { get; set; } = new();

    [JsonPropertyName("education")]
    public List<CvAnchorLine> Education { get; set; } = new();

    [JsonPropertyName("languages")]
    public List<CvAnchorLine> Languages { get; set; } = new();

    /// <summary>True si hay con qué montar un CV local sin depender de Gemini.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Summary) &&
        Skills.Count == 0 && Education.Count == 0 && Languages.Count == 0;
}

public sealed class CvAnchorLine
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public sealed class CvAnchorEntry
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("org")]
    public string Org { get; set; } = string.Empty;

    [JsonPropertyName("dates")]
    public string Dates { get; set; } = string.Empty;

    [JsonPropertyName("stack")]
    public string Stack { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("urlLabel")]
    public string UrlLabel { get; set; } = string.Empty;

    /// <summary>Descripción en primera persona. Se usa si el modelo no trae viñetas.</summary>
    [JsonPropertyName("fallback")]
    public string Fallback { get; set; } = string.Empty;

    /// <summary>
    /// Todos los puntos que se pueden contar de esta entrada, uno por competencia. De aquí se
    /// eligen los que mejor encajen con cada oferta.
    /// </summary>
    [JsonPropertyName("points")]
    public List<string> Points { get; set; } = new();
}
