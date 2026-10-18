using System.Text.Json.Serialization;

namespace CareerCopilot.Models;

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

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 2;

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

    [JsonIgnore]
    public bool FromProfile { get; set; }

    [JsonPropertyName("bullets")]
    public List<string>? Bullets { get; set; }
}
