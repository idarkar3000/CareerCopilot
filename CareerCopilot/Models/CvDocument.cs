using System.Text.Json.Serialization;

namespace CareerCopilot.Models;

/// <summary>
/// Documento completo del currículum que Gemini genera en una sola llamada junto a la evaluación.
/// La plantilla Typst no maqueta nada fijo: se limita a renderizar estas secciones.
/// </summary>
public class CvDocument
{
    [JsonPropertyName("headline")]
    public string Headline { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("sections")]
    public List<CvSection> Sections { get; set; } = new();
}

public class CvSection
{
    [JsonPropertyName("heading")]
    public string Heading { get; set; } = string.Empty;

    /// <summary>1 = imprescindible, 2 = recomendable, 3 = secundario. El compilador recorta de mayor a menor si no cabe en una página.</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 2;

    /// <summary>"entries" (experiencia/proyectos con fecha y stack) o "texts" (líneas tipo habilidades).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = CvSectionKinds.Texts;

    [JsonPropertyName("items")]
    public List<CvItem> Items { get; set; } = new();
}

public static class CvSectionKinds
{
    public const string Texts = "texts";
    public const string Entries = "entries";

    public static string Normalize(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "entries" or "entry" or "list_entries" or "experience" or "proyectos" => Entries,
            _ => Texts
        };
}

public class CvItem
{
    /// <summary>Etiqueta en negrita para kind = texts (p. ej. "Lenguajes").</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("org")]
    public string? Org { get; set; }

    [JsonPropertyName("dates")]
    public string? Dates { get; set; }

    [JsonPropertyName("stack")]
    public string? Stack { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("urlLabel")]
    public string? UrlLabel { get; set; }

    /// <summary>
    /// Marca que el texto del enlace viene del perfil y no del modelo. Es interno del compilador,
    /// asi que no se serializa a Gemini. Sirve para no recortar un texto de enlace que ya es
    /// correcto: el limite de caracteres es para el texto que se inventa, no para el dato bueno.
    /// </summary>
    [JsonIgnore]
    public bool FromProfile { get; set; }

    [JsonPropertyName("bullets")]
    public List<string>? Bullets { get; set; }
}
