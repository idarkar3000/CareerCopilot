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

    [JsonPropertyName("tailoredSummary")]
    public string TailoredSummary { get; set; } = string.Empty;

    [JsonPropertyName("tailoredExperience")]
    public List<string> TailoredExperience { get; set; } = new();
}